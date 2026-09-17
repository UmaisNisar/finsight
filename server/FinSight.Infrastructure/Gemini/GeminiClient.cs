using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FinSight.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinSight.Infrastructure.Gemini;

public sealed class GeminiOptions
{
    public const string Section = "Gemini";

    /// <summary>
    /// Used when <see cref="FallbackModels"/> isn't set. Gemini's free tier caps requests per day per model, so each extra model
    /// is another day's allowance. Ordered best quality first; the chain is only walked when a model above is used up or failing.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultFallbackModels =
        ["gemini-3.6-flash", "gemini-3.5-flash", "gemini-2.5-flash-lite", "gemini-3.5-flash-lite", "gemini-3.1-flash-lite"];

    /// <summary>Server-side only. Set with user secrets or an environment variable (Gemini__ApiKey).</summary>
    public string? ApiKey { get; set; }

    public string Model { get; set; } = "gemini-2.5-flash";

    /// <summary>Models tried in order after <see cref="Model"/>. Null uses <see cref="DefaultFallbackModels"/>; an empty list turns fallback off.</summary>
    public string[]? FallbackModels { get; set; }

    /// <summary>The whole call, across every model and retry.</summary>
    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>One request to one model. A model that hangs past this is skipped for the next, within <see cref="TimeoutSeconds"/>.</summary>
    public int AttemptTimeoutSeconds { get; set; } = 60;

    /// <summary>Pause before retrying a server error, network failure or unusable answer on the same model.</summary>
    public double RetryDelaySeconds { get; set; } = 1;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    public IReadOnlyList<string> ModelChain()
    {
        var chain = new List<string> { Model.Trim() };
        foreach (var model in FallbackModels ?? DefaultFallbackModels)
        {
            var name = model?.Trim();
            if (!string.IsNullOrEmpty(name) && !chain.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                chain.Add(name);
            }
        }

        return chain;
    }
}

/// <summary>A validated structured answer and the model that gave it, which may be a fallback.</summary>
public sealed record GeminiResult<T>(T Value, string Model);

/// <summary>How Google says the key itself is the problem, as opposed to one model objecting.</summary>
public static partial class GeminiErrors
{
    /// <summary>Wrong, expired, restricted away from this API, or on a project with the API switched off. Not a bare PERMISSION_DENIED: one model can refuse a key the others accept.</summary>
    [GeneratedRegex("API_KEY_INVALID|API key not valid|API key expired|API_KEY_SERVICE_BLOCKED|SERVICE_DISABLED", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyRefused();

    [GeneratedRegex("SERVICE_DISABLED|has not been used in project", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ServiceDisabled();

    public static bool IsKeyRefusal(int status, string body) => status is 400 or 401 or 403 && KeyRefused().IsMatch(body);

    public static bool IsServiceDisabled(string body) => ServiceDisabled().IsMatch(body);

    /// <summary>Error bodies are read only to classify them, never logged: they can echo the request.</summary>
    internal static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return body.Length > 4096 ? body[..4096] : body;
    }
}

/// <summary>Low-level structured-output call to the Gemini REST API. Used only by <see cref="GeminiService"/>.</summary>
/// <remarks>
/// The API key comes from <see cref="IGeminiKeyResolver"/>: the current user's own key, else the server key. Models are tried in
/// <see cref="GeminiOptions.ModelChain"/> order:
/// <list type="bullet">
/// <item>Google refusing the key stops at once: every model shares the key.</item>
/// <item>429 moves straight to the next model. A daily quota doesn't refill in the second a retry takes.</item>
/// <item>Any other 4xx moves to the next model, which may not share the objection.</item>
/// <item>5xx, network failures, empty answers and JSON that doesn't parse are retried once, then the next model.</item>
/// </list>
/// Only model names, status codes and a short reason are logged, never response bodies.
/// </remarks>
public sealed partial class GeminiClient(HttpClient http, IGeminiKeyResolver keys, IOptions<GeminiOptions> options, ILogger<GeminiClient> logger)
{
    private const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/models";

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>The primary model. The one that actually answered is in <see cref="GeminiResult{T}.Model"/>.</summary>
    public string Model => options.Value.Model;

    public async Task<T> GenerateJsonAsync<T>(string systemInstruction, string prompt, JsonObject responseSchema, double temperature, CancellationToken cancellationToken)
        where T : class =>
        (await GenerateAsync<T>(systemInstruction, prompt, responseSchema, temperature, thinkingBudget: null, cancellationToken)).Value;

    /// <param name="thinkingBudget">Reasoning tokens, applied only where the model supports it. Null leaves each model's default.</param>
    /// <exception cref="AiUnavailableException">No key, the key was refused, or every model in the chain failed.</exception>
    public async Task<GeminiResult<T>> GenerateAsync<T>(string systemInstruction, string prompt, JsonObject responseSchema, double temperature, int? thinkingBudget,
        CancellationToken cancellationToken)
        where T : class
    {
        var settings = options.Value;
        var apiKey = await keys.ResolveAsync(cancellationToken);
        if (apiKey is null)
        {
            throw new AiUnavailableException(AiFailure.NotConfigured);
        }

        var chain = settings.ModelChain();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));

        var rateLimited = false;
        AiFailure? transient = null;

        foreach (var model in chain)
        {
            var body = Body(model, systemInstruction, prompt, responseSchema, temperature, thinkingBudget);

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                attemptTimeout.CancelAfter(TimeSpan.FromSeconds(settings.AttemptTimeoutSeconds));

                Outcome<T> outcome;
                try
                {
                    outcome = await AttemptAsync<T>(apiKey, model, body, attemptTimeout.Token);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    if (budget.IsCancellationRequested)
                    {
                        LogModelFailed(logger, model, 0, "overall timeout");
                        throw new AiUnavailableException(AiFailure.Timeout, ex);
                    }

                    // This model is slow right now; a retry would likely be as slow, so try the next one.
                    LogModelFailed(logger, model, 0, "timeout");
                    transient = AiFailure.Timeout;
                    break;
                }

                switch (outcome.Kind)
                {
                    case OutcomeKind.Answered:
                        if (!string.Equals(model, chain[0], StringComparison.OrdinalIgnoreCase))
                        {
                            LogFallbackAnswered(logger, model, chain[0]);
                        }

                        return new GeminiResult<T>(outcome.Value!, model);

                    case OutcomeKind.KeyRefused:
                        throw new AiUnavailableException(AiFailure.KeyRefused);

                    case OutcomeKind.RateLimited:
                        rateLimited = true;
                        break;

                    case OutcomeKind.Rejected:
                        break;

                    default:
                        transient = outcome.Kind == OutcomeKind.Invalid ? AiFailure.InvalidResponse : AiFailure.Unavailable;
                        if (attempt < 2)
                        {
                            await DelayAsync(settings, budget, cancellationToken);
                            continue;
                        }

                        break;
                }

                break;
            }
        }

        // A daily quota explains the failure only when nothing else went wrong; a model that doesn't exist for this key says nothing.
        throw new AiUnavailableException(transient ?? (rateLimited ? AiFailure.RateLimited : AiFailure.Unavailable));
    }

    private async Task<Outcome<T>> AttemptAsync<T>(string apiKey, string model, JsonObject body, CancellationToken cancellationToken)
        where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Endpoint}/{Uri.EscapeDataString(model)}:generateContent")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("x-goog-api-key", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            LogModelFailed(logger, model, 0, "network error");
            return Outcome<T>.Of(OutcomeKind.Transient);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                if (status is 400 or 401 or 403 && GeminiErrors.IsKeyRefusal(status, await GeminiErrors.ReadBodyAsync(response, cancellationToken)))
                {
                    LogModelFailed(logger, model, status, "key refused");
                    return Outcome<T>.Of(OutcomeKind.KeyRefused);
                }

                if (status == 429)
                {
                    LogModelFailed(logger, model, status, "quota used up");
                    return Outcome<T>.Of(OutcomeKind.RateLimited);
                }

                LogModelFailed(logger, model, status, status < 500 ? "rejected" : "server error");
                return Outcome<T>.Of(status < 500 ? OutcomeKind.Rejected : OutcomeKind.Transient);
            }

            string text;
            string finishReason;
            try
            {
                var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
                var candidate = json?["candidates"]?.AsArray().FirstOrDefault();
                text = string.Concat(candidate?["content"]?["parts"]?.AsArray().Select(p => p?["text"]?.GetValue<string>() ?? string.Empty) ?? []);
                finishReason = candidate?["finishReason"]?.GetValue<string>() ?? "unknown";
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
            {
                // Not JSON, or JSON of an unexpected shape (for example an HTML error page from a proxy).
                LogModelFailed(logger, model, status, "unreadable response");
                return Outcome<T>.Of(OutcomeKind.Invalid);
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                LogEmptyResponse(logger, finishReason, model);
                return Outcome<T>.Of(OutcomeKind.Invalid);
            }

            try
            {
                if (JsonSerializer.Deserialize<T>(StripFence(text), ReadOptions) is { } value)
                {
                    return new Outcome<T>(OutcomeKind.Answered, value);
                }
            }
            catch (JsonException)
            {
            }

            LogModelFailed(logger, model, status, "answer was not the requested JSON");
            return Outcome<T>.Of(OutcomeKind.Invalid);
        }
    }

    private static JsonObject Body(string model, string systemInstruction, string prompt, JsonObject responseSchema, double temperature, int? thinkingBudget)
    {
        var generationConfig = new JsonObject
        {
            ["temperature"] = temperature,
            ["responseMimeType"] = "application/json",
            ["responseSchema"] = responseSchema.DeepClone(),
            // Reasoning tokens on 2.5+ models share this budget; too small and the JSON is cut off.
            ["maxOutputTokens"] = 16384,
        };

        if (ThinkingConfig(model, thinkingBudget) is { } thinking)
        {
            generationConfig["thinkingConfig"] = thinking;
        }

        return new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = systemInstruction }) },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt }),
            }),
            ["generationConfig"] = generationConfig,
        };
    }

    /// <summary>
    /// <c>thinkingConfig</c> exists only on the 2.5 and 3.x families. Flash models accept a budget of zero; Pro rejects anything
    /// below 128, so it gets at least that, or no config at all when no thinking was asked for. Other models get none, rather than
    /// a 400 on every request.
    /// </summary>
    public static JsonObject? ThinkingConfig(string model, int? thinkingBudget)
    {
        if (thinkingBudget is not { } budget || !ThinkingModel().IsMatch(model))
        {
            return null;
        }

        if (model.Contains("flash", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonObject { ["thinkingBudget"] = Math.Max(0, budget) };
        }

        return budget > 0 ? new JsonObject { ["thinkingBudget"] = Math.Max(128, budget) } : null;
    }

    [GeneratedRegex(@"2\.5|3\.\d")]
    private static partial Regex ThinkingModel();

    /// <summary>Models occasionally wrap JSON in a code fence despite the JSON response type.</summary>
    private static string StripFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstLine = trimmed.IndexOf('\n', StringComparison.Ordinal);
        var end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstLine > 0 && end > firstLine ? trimmed[(firstLine + 1)..end].Trim() : trimmed;
    }

    private static async Task DelayAsync(GeminiOptions settings, CancellationTokenSource budget, CancellationToken cancellationToken)
    {
        if (settings.RetryDelaySeconds <= 0)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(settings.RetryDelaySeconds), budget.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiUnavailableException(AiFailure.Timeout, ex);
        }
    }

    private enum OutcomeKind
    {
        Answered,
        KeyRefused,
        RateLimited,
        Rejected,
        Transient,
        Invalid,
    }

    private sealed record Outcome<T>(OutcomeKind Kind, T? Value)
        where T : class
    {
        public static Outcome<T> Of(OutcomeKind kind) => new(kind, null);
    }

    [LoggerMessage(LogLevel.Warning, "Gemini model {Model} failed: HTTP {Status}, {Reason}")]
    private static partial void LogModelFailed(ILogger logger, string model, int status, string reason);

    [LoggerMessage(LogLevel.Warning, "Gemini answered with fallback model {Model} because {Primary} failed")]
    private static partial void LogFallbackAnswered(ILogger logger, string model, string primary);

    [LoggerMessage(LogLevel.Warning, "Gemini returned no content (finish reason {FinishReason}) for model {Model}")]
    private static partial void LogEmptyResponse(ILogger logger, string finishReason, string model);
}
