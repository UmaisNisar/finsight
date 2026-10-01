namespace FinSight.Core.Receipts;

#pragma warning disable CA2227, CA1002
/// <summary>The AI's raw receipt answer, before validation. Shaped to match <c>GeminiSchemas.ReceiptExtraction</c>.</summary>
public sealed class RawReceiptExtraction
{
    public string? OrderNumber { get; set; }
    public List<RawReceiptItem>? Items { get; set; }
    public double? Total { get; set; }
}

public sealed class RawReceiptItem
{
    public string? Name { get; set; }
    public double? Quantity { get; set; }
    public double? Amount { get; set; }
}
#pragma warning restore CA2227, CA1002

/// <summary>Turns the model's raw receipt answer into a clean <see cref="ReceiptExtraction"/>, dropping empty or absurd values.</summary>
public static class ReceiptExtractionValidator
{
    private const int MaxItems = 60;
    private const int MaxNameLength = 160;

    public static ReceiptExtraction Validate(RawReceiptExtraction raw)
    {
        var items = (raw.Items ?? [])
            .Where(i => !string.IsNullOrWhiteSpace(i.Name))
            .Select(i => new ReceiptItem(
                Trim(i.Name!.Trim()),
                i.Quantity is > 0 and < 10_000 ? (int)i.Quantity.Value : null,
                Money(i.Amount)))
            .Take(MaxItems)
            .ToList();

        return new ReceiptExtraction(
            string.IsNullOrWhiteSpace(raw.OrderNumber) ? null : Trim(raw.OrderNumber.Trim()),
            items,
            Money(raw.Total));
    }

    private static string Trim(string value) => value.Length > MaxNameLength ? value[..MaxNameLength] : value;

    // Prices are positive and fit a statement; anything else is a hallucination, so drop it rather than show it.
    private static decimal? Money(double? value) =>
        value is > 0 and < 1_000_000 ? Math.Round((decimal)value.Value, 2) : null;
}
