namespace FinSight.Core.Parsing;

/// <summary>A word with its bounding box. Coordinates are in points, measured from the top-left of the page.</summary>
public sealed record PdfWord(string Text, double Left, double Right, double Top, double Bottom)
{
    public double Height => Bottom - Top;
    public double CenterY => (Top + Bottom) / 2;
    public double CenterX => (Left + Right) / 2;
}

public sealed record PdfPageText(int Number, double Width, double Height, IReadOnlyList<PdfWord> Words);

/// <summary>
/// Layout-preserving text of a PDF. Produced by an <c>IPdfTextExtractor</c> (PdfPig today; an OCR
/// engine for scanned statements can produce the same shape later).
/// </summary>
public sealed record PdfTextDocument(IReadOnlyList<PdfPageText> Pages)
{
    public int WordCount => Pages.Sum(p => p.Words.Count);
}
