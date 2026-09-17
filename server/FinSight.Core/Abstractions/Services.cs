using FinSight.Core.Insights;
using FinSight.Core.Parsing;

namespace FinSight.Core.Abstractions;

/// <summary>
/// Turns PDF bytes into positioned text. The PdfPig implementation reads text-based PDFs; an OCR
/// implementation for scanned statements can be added behind the same interface.
/// </summary>
public interface IPdfTextExtractor
{
    /// <param name="password">The PDF's password, for this read only. Implementations must not store or log it.</param>
    /// <exception cref="PdfPasswordRequiredException">The PDF is encrypted and no password was given.</exception>
    /// <exception cref="PdfPasswordIncorrectException">The PDF is encrypted and the password given doesn't open it.</exception>
    /// <exception cref="PdfUnreadableException">The file is not a readable PDF, or is too large or complex to read within the parsing limits.</exception>
    PdfTextDocument Extract(ReadOnlyMemory<byte> pdf, string? password = null, CancellationToken cancellationToken = default);
}

public sealed class PdfPasswordRequiredException : Exception
{
    public PdfPasswordRequiredException() : base("The PDF is password protected.") { }
    public PdfPasswordRequiredException(string message) : base(message) { }
    public PdfPasswordRequiredException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The password given for an encrypted PDF doesn't open it. The message never contains the password.</summary>
public sealed class PdfPasswordIncorrectException : Exception
{
    public PdfPasswordIncorrectException() : base("The password doesn't open the PDF.") { }
    public PdfPasswordIncorrectException(string message) : base(message) { }
    public PdfPasswordIncorrectException(string message, Exception inner) : base(message, inner) { }
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

    /// <summary>Every model in the chain answered 429: Gemini's quota for the key is used up for now.</summary>
    RateLimited,
    Timeout,
    InvalidResponse,
    Unavailable,

    /// <summary>Google refused the key itself (invalid, expired, blocked, or the API is off for its project). No model will help.</summary>
    KeyRefused,

    /// <summary>FinSight's own daily allowance for this user, or the server key's shared ceiling, is used up. Nothing was sent.</summary>
    LimitReached,
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
