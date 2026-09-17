using FinSight.Core.Insights;
using FinSight.Core.Parsing;

namespace FinSight.Core.Abstractions;

/// <summary>
/// Turns PDF bytes into positioned text. The PdfPig implementation reads text-based PDFs; an OCR
/// implementation for scanned statements can be added behind the same interface.
/// </summary>
public interface IPdfTextExtractor
{
    /// <exception cref="PdfPasswordRequiredException">The PDF is encrypted and no valid password was given.</exception>
    /// <exception cref="PdfUnreadableException">The file is not a readable PDF, or is too large or complex to read within the parsing limits.</exception>
    PdfTextDocument Extract(ReadOnlyMemory<byte> pdf, string? password = null, CancellationToken cancellationToken = default);
}

public sealed class PdfPasswordRequiredException : Exception
{
    public PdfPasswordRequiredException() : base("The PDF is password protected.") { }
    public PdfPasswordRequiredException(string message) : base(message) { }
    public PdfPasswordRequiredException(string message, Exception inner) : base(message, inner) { }
}

public sealed class PdfUnreadableException : Exception
{
    public PdfUnreadableException() : base("The file is not a readable PDF.") { }
    public PdfUnreadableException(string message) : base(message) { }
    public PdfUnreadableException(string message, Exception inner) : base(message, inner) { }
}

public sealed record GeminiAnalysisResult(ValidatedAnalysis Result, string Model);

/// <summary>The single entry point to the AI model. No other code calls Gemini.</summary>
public interface IGeminiService
{
    /// <summary>Whether a key is available for the current user: their own Gemini key, or the server's.</summary>
    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<MerchantCategorization>> CategorizeTransactionsAsync(IReadOnlyList<MerchantCategorizationRequest> merchants, CancellationToken cancellationToken);

    Task<GeminiAnalysisResult> AnalyzeFinancialDataAsync(FinancialFacts facts, CancellationToken cancellationToken);

    Task<IReadOnlyList<RecurringReview>> DetectRecurringPatternsAsync(IReadOnlyList<RecurringReviewRequest> candidates, CancellationToken cancellationToken);
}

public enum AiFailure
{
    NotConfigured,
    RateLimited,
    Timeout,
    InvalidResponse,
    Unavailable,
}

public sealed class AiUnavailableException : Exception
{
    public AiUnavailableException(AiFailure failure) : base($"AI request failed: {failure}") => Failure = failure;

    public AiUnavailableException(AiFailure failure, Exception inner) : base($"AI request failed: {failure}", inner) => Failure = failure;

    public AiUnavailableException() : this(AiFailure.Unavailable) { }

    public AiUnavailableException(string message) : base(message) => Failure = AiFailure.Unavailable;

    public AiUnavailableException(string message, Exception inner) : base(message, inner) => Failure = AiFailure.Unavailable;

    public AiFailure Failure { get; }
}
