using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FinSight.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinSight.Infrastructure.Gemini;

public sealed class GeminiOptions
{
    public const string Section = "Gemini";

    /// <summary>Server-side only. Set with user secrets or an environment variable (Gemini__ApiKey).</summary>
    public string? ApiKey { get; set; }

    public string Model { get; set; } = "gemini-2.5-flash";

    public int TimeoutSeconds { get; set; } = 90;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>Low-level structured-output call to the Gemini REST API. Used only by <see cref="GeminiService"/>.</summary>
public sealed partial class GeminiClient(HttpClient http, IOptions<GeminiOptions> options, ILogger<GeminiClient> logger)
{
    private const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/models";

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    public string Model => options.Value.Model;

    public async Task<T> GenerateJsonAsync<T>(string systemInstruction, string prompt, JsonObject responseSchema, double temperature, CancellationToken cancellationToken)
        where T : class
    {
        var settings = options.Value;
        if (!settings.IsConfigured)
        {
            throw new AiUnavailableException(AiFailure.NotConfigured);
        }

        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = systemInstruction }) },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt }),
            }),
            ["generationConfig"] = new JsonObject
            {
                ["temperature"] = temperature,
                ["responseMimeType"] = "application/json",
                ["responseSchema"] = responseSchema,
                // Reasoning tokens on 2.5 models share this budget; too small and the JSON is cut off.
                ["maxOutputTokens"] = 16384,
            },
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));

        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Endpoint}/{Uri.EscapeDataString(settings.Model)}:generateContent")
            {
                Content = JsonContent.Create(body),
            };
            request.Headers.Add("x-goog-api-key", settings.ApiKey);

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, timeout.Token);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AiUnavailableException(AiFailure.Timeout, ex);
            }
            catch (HttpRequestException ex)
            {
                if (attempt < 2)
                {
                    continue;
                }

                throw new AiUnavailableException(AiFailure.Unavailable, ex);
            }

            using (response)
            {
                if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1.5), timeout.Token);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    // The body can echo request content, so only the status is logged.
                    LogUpstreamError(logger, (int)response.StatusCode, settings.Model);
                    throw new AiUnavailableException(response.StatusCode == HttpStatusCode.TooManyRequests ? AiFailure.RateLimited : AiFailure.Unavailable);
                }

                var json = await response.Content.ReadFromJsonAsync<JsonObject>(timeout.Token);
                var candidate = json?["candidates"]?.AsArray().FirstOrDefault();
                var text = string.Concat(candidate?["content"]?["parts"]?.AsArray().Select(p => p?["text"]?.GetValue<string>() ?? string.Empty) ?? []);

                if (string.IsNullOrWhiteSpace(text))
                {
                    LogEmptyResponse(logger, candidate?["finishReason"]?.GetValue<string>() ?? "unknown", settings.Model);
                    throw new AiUnavailableException(AiFailure.InvalidResponse);
                }

                try
                {
                    return JsonSerializer.Deserialize<T>(text, ReadOptions) ?? throw new AiUnavailableException(AiFailure.InvalidResponse);
                }
                catch (JsonException ex)
                {
                    LogInvalidJson(logger, settings.Model);
                    throw new AiUnavailableException(AiFailure.InvalidResponse, ex);
                }
            }
        }
    }

    [LoggerMessage(LogLevel.Warning, "Gemini returned HTTP {Status} for model {Model}")]
    private static partial void LogUpstreamError(ILogger logger, int status, string model);

    [LoggerMessage(LogLevel.Warning, "Gemini returned no content (finish reason {FinishReason}) for model {Model}")]
    private static partial void LogEmptyResponse(ILogger logger, string finishReason, string model);

    [LoggerMessage(LogLevel.Warning, "Gemini returned JSON that did not match the expected shape for model {Model}")]
    private static partial void LogInvalidJson(ILogger logger, string model);
}
