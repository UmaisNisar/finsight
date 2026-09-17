using FinSight.Core.Abstractions;
using FinSight.Core.Insights;

namespace FinSight.Infrastructure.Gemini;

/// <summary>
/// The only place the application talks to Gemini. Every response is parsed into a raw type and
/// validated against the data that was sent before anything else sees it.
/// </summary>
public sealed class GeminiService(GeminiClient client, IGeminiKeyResolver keys) : IGeminiService
{
    private const int CategorizationBatchSize = 40;

    /// <summary>True when the current user has their own key or the server has one.</summary>
    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken) => await keys.ResolveAsync(cancellationToken) is not null;

    public async Task<IReadOnlyList<MerchantCategorization>> CategorizeTransactionsAsync(
        IReadOnlyList<MerchantCategorizationRequest> merchants, CancellationToken cancellationToken)
    {
        var results = new List<MerchantCategorization>();

        foreach (var batch in merchants.Chunk(CategorizationBatchSize))
        {
            var raw = await client.GenerateJsonAsync<RawMerchantCategorizationResponse>(
                GeminiPrompts.CategorizationSystemInstruction,
                GeminiPrompts.CategorizationPrompt(batch),
                GeminiSchemas.MerchantCategorization(),
                temperature: 0.1,
                cancellationToken);

            results.AddRange(MerchantCategorizationValidator.Validate(raw, batch));
        }

        return results;
    }

    public async Task<GeminiAnalysisResult> AnalyzeFinancialDataAsync(FinancialFacts facts, CancellationToken cancellationToken)
    {
        var raw = await client.GenerateJsonAsync<RawAnalysis>(
            GeminiPrompts.AnalysisSystemInstruction,
            GeminiPrompts.AnalysisPrompt(facts),
            GeminiSchemas.FinancialAnalysis(),
            temperature: 0.3,
            cancellationToken);

        return new GeminiAnalysisResult(AnalysisValidator.Validate(raw, facts), client.Model);
    }

    public async Task<IReadOnlyList<RecurringReview>> DetectRecurringPatternsAsync(
        IReadOnlyList<RecurringReviewRequest> candidates, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var raw = await client.GenerateJsonAsync<RawRecurringReviewResponse>(
            GeminiPrompts.RecurringSystemInstruction,
            GeminiPrompts.RecurringPrompt(candidates),
            GeminiSchemas.RecurringReview(),
            temperature: 0.1,
            cancellationToken);

        return MerchantCategorizationValidator.ValidateRecurring(raw, candidates);
    }
}
