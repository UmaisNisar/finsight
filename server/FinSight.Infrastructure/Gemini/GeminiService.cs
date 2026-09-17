using FinSight.Core.Abstractions;
using FinSight.Core.Insights;
using Microsoft.Extensions.Logging;

namespace FinSight.Infrastructure.Gemini;

/// <summary>
/// The only place the application talks to Gemini. Every response is parsed into a raw type and
/// validated against the data that was sent before anything else sees it. Each call is counted against
/// <see cref="AiQuota"/> first; a call over a limit fails with <see cref="AiFailure.LimitReached"/> without reaching Google.
/// </summary>
public sealed partial class GeminiService(GeminiClient client, IGeminiKeyResolver keys, AiQuota quota, AiKeyHealth health, ILogger<GeminiService> logger)
    : IGeminiService
{
    private const int CategorizationBatchSize = 40;

    /// <summary>Mechanical jobs don't need reasoning, and skipping it keeps them fast.</summary>
    private const int NoThinking = 0;

    /// <summary>Enough reasoning to interpret the facts, well inside the output token budget the JSON shares with it.</summary>
    private const int AnalysisThinking = 2048;

    /// <summary>True when the current user has their own key or the server has one.</summary>
    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken) => await keys.ResolveAsync(cancellationToken) is not null;

    public async Task<IReadOnlyList<MerchantCategorization>> CategorizeTransactionsAsync(
        IReadOnlyList<MerchantCategorizationRequest> merchants, CancellationToken cancellationToken)
    {
        var results = new List<MerchantCategorization>();

        foreach (var batch in merchants.Chunk(CategorizationBatchSize))
        {
            try
            {
                var raw = await CallAsync(AiCallKind.Categorization, ct => client.GenerateAsync<RawMerchantCategorizationResponse>(
                    GeminiPrompts.CategorizationSystemInstruction,
                    GeminiPrompts.CategorizationPrompt(batch),
                    GeminiSchemas.MerchantCategorization(),
                    temperature: 0.1,
                    NoThinking,
                    ct), cancellationToken);

                results.AddRange(MerchantCategorizationValidator.Validate(raw.Value, batch));
            }
            catch (AiUnavailableException) when (results.Count > 0)
            {
                // Keep what earlier batches answered; the rest stay uncategorized and are retried later.
                break;
            }
        }

        return results;
    }

    public async Task<GeminiAnalysisResult> AnalyzeFinancialDataAsync(FinancialFacts facts, CancellationToken cancellationToken)
    {
        var raw = await CallAsync(AiCallKind.Analysis, ct => client.GenerateAsync<RawAnalysis>(
            GeminiPrompts.AnalysisSystemInstruction,
            GeminiPrompts.AnalysisPrompt(facts),
            GeminiSchemas.FinancialAnalysis(),
            temperature: 0.3,
            AnalysisThinking,
            ct), cancellationToken);

        return new GeminiAnalysisResult(AnalysisValidator.Validate(raw.Value, facts), raw.Model);
    }

    public async Task<IReadOnlyList<RecurringReview>> DetectRecurringPatternsAsync(
        IReadOnlyList<RecurringReviewRequest> candidates, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var raw = await CallAsync(AiCallKind.RecurringReview, ct => client.GenerateAsync<RawRecurringReviewResponse>(
            GeminiPrompts.RecurringSystemInstruction,
            GeminiPrompts.RecurringPrompt(candidates),
            GeminiSchemas.RecurringReview(),
            temperature: 0.1,
            NoThinking,
            ct), cancellationToken);

        return MerchantCategorizationValidator.ValidateRecurring(raw.Value, candidates);
    }

    private async Task<GeminiResult<T>> CallAsync<T>(AiCallKind kind, Func<CancellationToken, Task<GeminiResult<T>>> call, CancellationToken cancellationToken)
    {
        var key = await keys.ResolveDetailsAsync(cancellationToken) ?? throw new AiUnavailableException(AiFailure.NotConfigured);

        var decision = quota.TryConsume(key.UserId, key.Email, serverKey: !key.IsUserKey, kind);
        if (decision != AiQuotaDecision.Allowed)
        {
            LogLimitReached(logger, kind, decision);
            throw new AiUnavailableException(AiFailure.LimitReached);
        }

        try
        {
            var result = await call(cancellationToken);
            health.Clear(key.UserId);
            return result;
        }
        catch (AiUnavailableException ex)
        {
            health.Record(key.UserId, ex.Failure);
            throw;
        }
    }

    [LoggerMessage(LogLevel.Warning, "Gemini call skipped: {Kind} is over FinSight's daily limit ({Decision})")]
    private static partial void LogLimitReached(ILogger logger, AiCallKind kind, AiQuotaDecision decision);
}
