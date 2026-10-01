namespace FinSight.Core.Receipts;

/// <summary>How a transaction's receipt details were obtained.</summary>
public enum ReceiptSource
{
    /// <summary>Items were read from the email body by the AI, after the body was redacted.</summary>
    Ai,

    /// <summary>Only the matching email was found (AI was off or unavailable); its subject and a link are kept, no items.</summary>
    LinkOnly,
}

/// <summary>One line on a receipt. Amount is in the receipt's currency and may be absent when the email doesn't itemise prices.</summary>
public sealed record ReceiptItem(string Name, int? Quantity, decimal? Amount);

/// <summary>
/// What the AI pulled out of a redacted receipt email: an order number, the lines bought and the order total.
/// No addresses, card numbers or contact details — those are stripped before the body is ever sent to the AI.
/// </summary>
public sealed record ReceiptExtraction(string? OrderNumber, IReadOnlyList<ReceiptItem> Items, decimal? Total);
