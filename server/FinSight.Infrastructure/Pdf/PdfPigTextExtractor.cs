using System.Diagnostics;
using System.IO.Compression;
using FinSight.Core.Abstractions;
using FinSight.Core.Parsing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokens;

namespace FinSight.Infrastructure.Pdf;

/// <summary>
/// Extracts words with positions from text-based PDFs using PdfPig. Scanned PDFs yield few or no
/// words; the parser reports that as <see cref="ParseFailure.NoTextLayer"/>, which is where an OCR
/// extractor would plug in.
/// </summary>
/// <remarks>
/// PDFs come from strangers, so parsing is bounded: at most <see cref="MaxPages"/> pages are read, compressed streams may
/// expand to at most <paramref name="maxDecodedBytes"/> in total (a small file can otherwise inflate to gigabytes), and
/// parsing stops after <paramref name="timeout"/>. A file that exceeds a limit is reported as unreadable.
/// </remarks>
public sealed class PdfPigTextExtractor(long maxDecodedBytes = PdfPigTextExtractor.DefaultMaxDecodedBytes, TimeSpan? timeout = null) : IPdfTextExtractor
{
    public const int MaxPages = 80;
    public const long DefaultMaxDecodedBytes = 128L * 1024 * 1024;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public PdfTextDocument Extract(ReadOnlyMemory<byte> pdf, string? password = null, CancellationToken cancellationToken = default)
    {
        if (pdf.Length < 5 || !pdf.Span[..5].SequenceEqual("%PDF-"u8))
        {
            throw new PdfUnreadableException("The file does not start with a PDF header.");
        }

        var budget = new ParseBudget(maxDecodedBytes, timeout ?? DefaultTimeout, cancellationToken);
        try
        {
            var parsingOptions = new ParsingOptions
            {
                UseLenientParsing = true,
                SkipMissingFonts = true,
                FilterProvider = new BudgetedFilterProvider(DefaultFilterProvider.Instance, budget),
            };
            if (password is not null)
            {
                parsingOptions.Password = password;
            }

            using var document = PdfDocument.Open(pdf.ToArray(), parsingOptions);
            budget.Check();
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

                // Lenient parsing can swallow the budget's exception inside PdfPig, so the budget is checked again here.
                budget.Check();
                pages.Add(new PdfPageText(page.Number, page.Width, height, words));
            }

            return new PdfTextDocument(pages);
        }
        catch (PdfDocumentEncryptedException ex)
        {
            throw new PdfPasswordRequiredException("The PDF is password protected.", ex);
        }
        catch (Exception) when (budget.IsCancelled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or PdfPasswordRequiredException))
        {
            // PdfPig throws many exception types for malformed files; any of them means "not a readable PDF".
            throw new PdfUnreadableException(budget.IsExceeded ? "The PDF is too large or complex to read." : "The PDF could not be read.", ex);
        }
    }

    /// <summary>Time, decompressed size and cancellation shared by every stream decoded while reading one document.</summary>
    private sealed class ParseBudget(long maxDecodedBytes, TimeSpan timeout, CancellationToken cancellationToken)
    {
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private long _decodedBytes;

        public bool IsExceeded { get; private set; }

        public bool IsCancelled => cancellationToken.IsCancellationRequested;

        public long RemainingBytes => maxDecodedBytes - _decodedBytes;

        public void Check()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_elapsed.Elapsed > timeout)
            {
                IsExceeded = true;
            }

            if (IsExceeded)
            {
                throw new PdfTooComplexException();
            }
        }

        public void Add(long bytes)
        {
            _decodedBytes += bytes;
            if (_decodedBytes > maxDecodedBytes)
            {
                IsExceeded = true;
            }

            Check();
        }

        public void Exceed()
        {
            IsExceeded = true;
            Check();
        }
    }

    private sealed class PdfTooComplexException() : Exception("The PDF exceeded the parsing budget.");

    private sealed class BudgetedFilterProvider(IFilterProvider inner, ParseBudget budget) : IFilterProvider
    {
        public IReadOnlyList<IFilter> GetFilters(DictionaryToken dictionary) => Wrap(inner.GetFilters(dictionary));

        public IReadOnlyList<IFilter> GetNamedFilters(IReadOnlyList<NameToken> names) => Wrap(inner.GetNamedFilters(names));

        public IReadOnlyList<IFilter> GetAllFilters() => Wrap(inner.GetAllFilters());

        private List<IFilter> Wrap(IReadOnlyList<IFilter> filters) => filters.Select(f => (IFilter)new BudgetedFilter(f, budget)).ToList();
    }

    private sealed class BudgetedFilter(IFilter inner, ParseBudget budget) : IFilter
    {
        public bool IsSupported => inner.IsSupported;

        public Memory<byte> Decode(Memory<byte> input, DictionaryToken streamDictionary, IFilterProvider filterProvider, int filterIndex)
        {
            budget.Check();

            // PdfPig inflates a whole stream into memory at once, so measure a deflate stream's size first, without keeping
            // its output, and refuse it before the allocation when it would blow the budget.
            if (inner is FlateFilter && InflatedSizeExceeds(input, budget.RemainingBytes))
            {
                budget.Exceed();
            }

            var output = inner.Decode(input, streamDictionary, filterProvider, filterIndex);
            budget.Add(output.Length);
            return output;
        }

        private static bool InflatedSizeExceeds(Memory<byte> input, long limit)
        {
            // Skip the two-byte zlib header, as PdfPig does. Corrupt data is left for PdfPig to handle.
            if (input.Length < 2 || limit < 0)
            {
                return limit < 0;
            }

            try
            {
                using var source = new MemoryStream(input[2..].ToArray(), writable: false);
                using var inflate = new DeflateStream(source, CompressionMode.Decompress);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = inflate.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > limit)
                    {
                        return true;
                    }
                }

                return false;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }
    }
}
