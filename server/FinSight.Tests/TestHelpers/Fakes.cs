using System.Collections.Concurrent;
using System.Net;
using System.Text;
using FinSight.Core.Abstractions;
using FinSight.Core.Insights;
using FinSight.Core.Statements;
using FinSight.Infrastructure.Gmail;

namespace FinSight.Tests.TestHelpers;

/// <summary>Stands in for Gemini. Each capability can be replaced per test; calls are recorded.</summary>
internal sealed class FakeGeminiService : IGeminiService
{
    public bool IsConfigured { get; set; } = true;

    public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken) => Task.FromResult(IsConfigured);

    public Func<IReadOnlyList<MerchantCategorizationRequest>, IReadOnlyList<MerchantCategorization>> Categorize { get; set; } = _ => [];

    public Func<FinancialFacts, GeminiAnalysisResult> Analyze { get; set; } = facts =>
        new GeminiAnalysisResult(AnalysisValidator.Validate(new RawAnalysis { Summary = "A steady month with no surprises." }, facts), "fake-model");

    public Func<IReadOnlyList<RecurringReviewRequest>, IReadOnlyList<RecurringReview>> ReviewRecurring { get; set; } = _ => [];

    public List<IReadOnlyList<MerchantCategorizationRequest>> CategorizationCalls { get; } = [];

    public List<FinancialFacts> AnalysisCalls { get; } = [];

    public Task<IReadOnlyList<MerchantCategorization>> CategorizeTransactionsAsync(IReadOnlyList<MerchantCategorizationRequest> merchants, CancellationToken cancellationToken)
    {
        CategorizationCalls.Add(merchants);
        return Task.FromResult(Categorize(merchants));
    }

    public Task<GeminiAnalysisResult> AnalyzeFinancialDataAsync(FinancialFacts facts, CancellationToken cancellationToken)
    {
        AnalysisCalls.Add(facts);
        return Task.FromResult(Analyze(facts));
    }

    public Task<IReadOnlyList<RecurringReview>> DetectRecurringPatternsAsync(IReadOnlyList<RecurringReviewRequest> candidates, CancellationToken cancellationToken) =>
        Task.FromResult(ReviewRecurring(candidates));
}

/// <summary>A resolver that always returns the same Gemini key (or none).</summary>
internal sealed class FixedGeminiKeyResolver(string? apiKey) : FinSight.Infrastructure.Gemini.IGeminiKeyResolver
{
    public Task<string?> ResolveAsync(CancellationToken cancellationToken) => Task.FromResult(apiKey);
}

/// <summary>An HttpClient backend that answers from a script and records every request.</summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    /// <summary>Answers requests once the script runs out. Without it, an unscripted request throws.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage>? Otherwise { get; set; }

    public List<RecordedRequest> Requests { get; } = [];

    public StubHttpHandler Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        Respond(_ => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    public StubHttpHandler Status(HttpStatusCode status, string body = "") =>
        Respond(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });

    public StubHttpHandler Throw(Exception exception) => Respond(_ => throw exception);

    public StubHttpHandler Respond(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _responses.Enqueue(respond);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
        {
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)), body));
        }

        if (_responses.Count == 0 && Otherwise is { } otherwise)
        {
            return otherwise(request);
        }

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException($"No scripted response for {request.Method} {request.RequestUri}");
        }

        return _responses.Dequeue()(request);
    }
}

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body);

/// <summary>A clock tests can move.</summary>
internal sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>A Gmail mailbox held in memory.</summary>
internal sealed class FakeGmailClient : IGmailClient
{
    public List<EmailCandidate> Messages { get; } = [];

    public Dictionary<(string MessageId, string PartId), byte[]> Attachments { get; } = [];

    public int MessageFetches { get; private set; }

    /// <summary>Every search query sent. The fake returns the whole mailbox for any query; the classifier does the filtering.</summary>
    public List<string> Queries { get; } = [];

    public Task<IReadOnlyList<string>> SearchMessageIdsAsync(string accessToken, string query, int max, CancellationToken cancellationToken)
    {
        Queries.Add(query);
        return Task.FromResult<IReadOnlyList<string>>(Messages.Select(m => m.MessageId).Take(max).ToList());
    }

    public Task<EmailCandidate> GetMessageAsync(string accessToken, string messageId, CancellationToken cancellationToken)
    {
        MessageFetches++;
        return Task.FromResult(Messages.Single(m => m.MessageId == messageId));
    }

    public Task<byte[]> DownloadAttachmentAsync(string accessToken, string messageId, string partId, CancellationToken cancellationToken) =>
        Attachments.TryGetValue((messageId, partId), out var bytes)
            ? Task.FromResult(bytes)
            : throw new FileNotFoundException("No such attachment.");
}

/// <summary>
/// Stands in for Google's OAuth token and userinfo endpoints. Each <see cref="Issue"/> call scripts one consent: the
/// authorization code it returns is what Google would append to the callback URL. Token requests are recorded.
/// </summary>
internal sealed class FakeGoogle : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Grant> _grants = new();

    public ConcurrentQueue<Dictionary<string, string>> TokenRequests { get; } = new();

    /// <param name="scope">Space-separated scopes Google reports as granted.</param>
    /// <param name="refreshToken">Null when Google issues no refresh token (no offline access requested).</param>
    public string Issue(string subject, string email, string scope = "openid https://www.googleapis.com/auth/userinfo.email https://www.googleapis.com/auth/userinfo.profile", string? refreshToken = null)
    {
        var code = $"code-{Guid.NewGuid():N}";
        _grants[code] = new Grant(subject, email, scope, refreshToken);
        return code;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!.GetLeftPart(UriPartial.Path);

        if (request.Method == HttpMethod.Post && uri == "https://oauth2.googleapis.com/token")
        {
            var form = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken))
                .ToDictionary(p => p.Key, p => p.Value.ToString());
            TokenRequests.Enqueue(form);

            if (!form.TryGetValue("code", out var code) || !_grants.TryGetValue(code, out var grant))
            {
                return Json(HttpStatusCode.BadRequest, new { error = "invalid_grant" });
            }

            return grant.RefreshToken is null
                ? Json(HttpStatusCode.OK, new { access_token = $"access-{code}", token_type = "Bearer", expires_in = 3599, scope = grant.Scope })
                : Json(HttpStatusCode.OK, new { access_token = $"access-{code}", token_type = "Bearer", expires_in = 3599, scope = grant.Scope, refresh_token = grant.RefreshToken });
        }

        if (request.Method == HttpMethod.Get && uri == "https://www.googleapis.com/oauth2/v3/userinfo"
            && request.Headers.Authorization?.Parameter is { } token && token.StartsWith("access-", StringComparison.Ordinal)
            && _grants.TryGetValue(token["access-".Length..], out var user))
        {
            return Json(HttpStatusCode.OK, new { sub = user.Subject, email = user.Email, email_verified = true, name = "Sam Rivera", given_name = "Sam", family_name = "Rivera" });
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private sealed record Grant(string Subject, string Email, string Scope, string? RefreshToken);
}

/// <summary>Collects every formatted log message, so tests can check secrets never reach the logs.</summary>
internal sealed class CapturingLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyCollection<string> Messages => _messages;

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(_messages);

    public void Dispose()
    {
    }

    private sealed class Logger(ConcurrentQueue<string> messages) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            messages.Enqueue($"{formatter(state, exception)} {exception}");
    }
}
