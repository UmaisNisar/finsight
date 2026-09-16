namespace FinSight.Core.Parsing;

public sealed class TextLine
{
    public required int Page { get; init; }
    public required IReadOnlyList<PdfWord> Words { get; init; }

    public double Top => Words.Min(w => w.Top);
    public double Bottom => Words.Max(w => w.Bottom);
    public double Height => Bottom - Top;
    public string Text => string.Join(' ', Words.Select(w => w.Text));

    public override string ToString() => $"p{Page} @{Top:0}: {Text}";
}

/// <summary>Groups positioned words into visual lines, top to bottom and left to right.</summary>
public static class LineBuilder
{
    public static IReadOnlyList<TextLine> Build(PdfTextDocument document)
    {
        var lines = new List<TextLine>();

        foreach (var page in document.Pages)
        {
            var words = page.Words
                .Where(w => !string.IsNullOrWhiteSpace(w.Text))
                .OrderBy(w => w.CenterY)
                .ThenBy(w => w.Left)
                .ToList();

            var current = new List<PdfWord>();
            double currentCenter = 0;
            double currentHeight = 0;

            foreach (var word in words)
            {
                var tolerance = Math.Max(2.0, Math.Max(currentHeight, word.Height) * 0.5);
                if (current.Count > 0 && Math.Abs(word.CenterY - currentCenter) > tolerance)
                {
                    lines.Add(new TextLine { Page = page.Number, Words = current.OrderBy(w => w.Left).ToList() });
                    current = [];
                }

                current.Add(word);
                currentCenter = current.Average(w => w.CenterY);
                currentHeight = current.Max(w => w.Height);
            }

            if (current.Count > 0)
            {
                lines.Add(new TextLine { Page = page.Number, Words = current.OrderBy(w => w.Left).ToList() });
            }
        }

        return lines;
    }
}
