using FinSight.Core.Parsing;

namespace FinSight.Tests.TestHelpers;

/// <summary>
/// Builds a positioned-word document the way a PDF extractor would, so parser tests can describe
/// statement layouts (column positions, alignment, wrapped lines) without binary fixtures.
/// </summary>
internal sealed class StatementLayout
{
    private const double FontSize = 9;
    private const double CharWidth = 4.8;
    private const double LineHeight = 13;

    private readonly List<PdfPageText> _pages = [];
    private List<PdfWord> _words = [];
    private double _y = 40;


    /// <summary>A line of cells. Cells whose x is negative are right-aligned to |x|.</summary>
    public StatementLayout Line(params (double X, string Text)[] cells)
    {
        foreach (var (x, text) in cells)
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var width = text.Length * CharWidth;
            var left = x < 0 ? -x - width : x;
            foreach (var (word, offset) in SplitWords(text))
            {
                var wordLeft = left + offset * CharWidth;
                _words.Add(new PdfWord(word, wordLeft, wordLeft + word.Length * CharWidth, _y, _y + FontSize));
            }
        }

        _y += LineHeight;
        return this;
    }

    public StatementLayout Text(string text) => Line((40, text));

    public StatementLayout Gap(int lines = 1)
    {
        _y += LineHeight * lines;
        return this;
    }

    public StatementLayout NewPage()
    {
        if (_words.Count > 0)
        {
            _pages.Add(new PdfPageText(_pages.Count + 1, 612, 792, _words));
        }

        _words = [];
        _y = 40;
        return this;
    }

    public PdfTextDocument Build()
    {
        var pages = new List<PdfPageText>(_pages);
        if (_words.Count > 0)
        {
            pages.Add(new PdfPageText(pages.Count + 1, 612, 792, _words));
        }

        return new PdfTextDocument(pages);
    }

    private static IEnumerable<(string Word, int Offset)> SplitWords(string text)
    {
        var index = 0;
        foreach (var word in text.Split(' '))
        {
            if (word.Length > 0)
            {
                yield return (word, index);
            }

            index += word.Length + 1;
        }
    }
}
