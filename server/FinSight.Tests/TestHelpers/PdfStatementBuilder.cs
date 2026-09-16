using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace FinSight.Tests.TestHelpers;

/// <summary>Writes a real, text-based PDF statement so tests exercise PdfPig extraction end to end.</summary>
internal sealed class PdfStatementBuilder
{
    private const double FontSize = 9;
    private const double LineHeight = 14;
    private readonly List<(double X, string Text)[]> _lines = [];

    /// <summary>Cells with negative x are right-aligned to |x|.</summary>
    public PdfStatementBuilder Line(params (double X, string Text)[] cells)
    {
        _lines.Add(cells);
        return this;
    }

    public PdfStatementBuilder Text(string text) => Line((40, text));

    public byte[] Build()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(612, 792);
        var y = 750.0;

        foreach (var cells in _lines)
        {
            foreach (var (x, text) in cells)
            {
                var left = x;
                if (x < 0)
                {
                    var letters = page.MeasureText(text, FontSize, new PdfPoint(0, y), font);
                    var width = letters.Count == 0 ? 0 : letters.Max(l => l.EndBaseLine.X) - letters.Min(l => l.StartBaseLine.X);
                    left = -x - width;
                }

                page.AddText(text, FontSize, new PdfPoint(left, y), font);
            }

            y -= LineHeight;
        }

        return builder.Build();
    }

    public static byte[] SampleChequingStatement() => new PdfStatementBuilder()
        .Text("Maple Credit Union")
        .Text("Everyday Chequing Account")
        .Text("Account number: 000123-4567890")
        .Text("Statement period: August 1, 2026 to August 31, 2026")
        .Line((40, "Date"), (100, "Description"), (-400, "Withdrawals"), (-480, "Deposits"), (-570, "Balance"))
        .Line((40, "Aug 1"), (100, "Opening balance"), (-570, "1,500.00"))
        .Line((40, "Aug 3"), (100, "PAYROLL DEPOSIT ACME CORP"), (-480, "3,100.00"), (-570, "4,600.00"))
        .Line((40, "Aug 4"), (100, "NETFLIX.COM"), (-400, "20.99"), (-570, "4,579.01"))
        .Line((40, "Aug 6"), (100, "POS PURCHASE LOBLAWS #221"), (-400, "142.30"), (-570, "4,436.71"))
        .Line((40, "Aug 9"), (100, "SQ *BLUE DOOR CAFE"), (-400, "18.75"), (-570, "4,417.96"))
        .Line((40, "Aug 15"), (100, "PRE-AUTHORIZED DEBIT MAPLE RESIDENTIAL RENT"), (-400, "1,850.00"), (-570, "2,567.96"))
        .Line((40, "Aug 20"), (100, "INTERNET TRANSFER TO SAVINGS 0042"), (-400, "400.00"), (-570, "2,167.96"))
        .Line((40, "Aug 31"), (100, "Closing balance"), (-570, "2,167.96"))
        .Build();
}
