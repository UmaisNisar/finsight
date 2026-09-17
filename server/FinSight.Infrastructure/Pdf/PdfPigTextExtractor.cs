using FinSight.Core.Abstractions;
using FinSight.Core.Parsing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;

namespace FinSight.Infrastructure.Pdf;

/// <summary>
/// Extracts words with positions from text-based PDFs using PdfPig. Scanned PDFs yield few or no
/// words; the parser reports that as <see cref="ParseFailure.NoTextLayer"/>, which is where an OCR
/// extractor would plug in.
/// </summary>
public sealed class PdfPigTextExtractor : IPdfTextExtractor
{
    public const int MaxPages = 80;

    public PdfTextDocument Extract(ReadOnlyMemory<byte> pdf, string? password = null)
    {
        if (pdf.Length < 5 || !pdf.Span[..5].SequenceEqual("%PDF-"u8))
        {
            throw new PdfUnreadableException("The file does not start with a PDF header.");
        }

        try
        {
            var parsingOptions = new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true };
            if (password is not null)
            {
                parsingOptions.Password = password;
            }

            using var document = PdfDocument.Open(pdf.ToArray(), parsingOptions);
            var pages = new List<PdfPageText>();

            foreach (var page in document.GetPages().Take(MaxPages))
            {
                var height = page.Height;
                var words = page.GetWords()
                    .Where(w => !string.IsNullOrWhiteSpace(w.Text))
                    .Select(w => new PdfWord(
                        w.Text,
                        w.BoundingBox.Left,
                        w.BoundingBox.Right,
                        height - w.BoundingBox.Top,
                        height - w.BoundingBox.Bottom))
                    .ToList();

                pages.Add(new PdfPageText(page.Number, page.Width, height, words));
            }

            return new PdfTextDocument(pages);
        }
        catch (PdfDocumentEncryptedException ex)
        {
            throw new PdfPasswordRequiredException("The PDF is password protected.", ex);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or PdfPasswordRequiredException))
        {
            // PdfPig throws many exception types for malformed files; any of them means "not a readable PDF".
            throw new PdfUnreadableException("The PDF could not be read.", ex);
        }
    }
}
