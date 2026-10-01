namespace FinSight.Core.Receipts;

/// <summary>A merchant whose order confirmations and receipts FinSight knows how to find in Gmail.</summary>
public sealed record ReceiptSender(string Merchant, IReadOnlyList<string> Domains);

/// <summary>
/// Maps a transaction's merchant to the email domains its receipts come from, so a purchase can be matched to the
/// order confirmation in Gmail. Only merchants that email itemised receipts are listed; a bank transfer or an ATM
/// withdrawal has nothing to match. Matching is by merchant name the pipeline already assigns (MerchantCatalog), so a
/// new merchant is added here, not guessed from the raw statement text.
/// </summary>
public static class ReceiptSenders
{
    // Keyed by the merchant display name MerchantCatalog assigns. Domains are the senders that actually mail receipts;
    // a Gmail search uses from:(a OR b), so extra domains only widen the net, never narrow it.
    private static readonly Dictionary<string, ReceiptSender> ByMerchant = new[]
    {
        new ReceiptSender("Amazon", ["amazon.ca", "amazon.com"]),
        new ReceiptSender("Amazon Prime", ["amazon.ca", "amazon.com"]),
        new ReceiptSender("IKEA", ["ikea.com", "ikea.ca"]),
        new ReceiptSender("Uber", ["uber.com"]),
        new ReceiptSender("Uber Eats", ["uber.com"]),
        new ReceiptSender("Lyft", ["lyft.com"]),
        new ReceiptSender("DoorDash", ["doordash.com"]),
        new ReceiptSender("SkipTheDishes", ["skipthedishes.com"]),
        new ReceiptSender("Instacart", ["instacart.com", "instacart.ca"]),
        new ReceiptSender("McDonald's", ["mcdonalds.com", "mcdonalds.ca"]),
        new ReceiptSender("OpenAI", ["openai.com"]),
        new ReceiptSender("Anthropic", ["anthropic.com"]),
        new ReceiptSender("Apple", ["apple.com"]),
        new ReceiptSender("Best Buy", ["bestbuy.ca", "bestbuy.com", "emails.bestbuy.ca"]),
        new ReceiptSender("Walmart", ["walmart.ca", "walmart.com"]),
        new ReceiptSender("The Home Depot", ["homedepot.ca", "homedepot.com"]),
        new ReceiptSender("Costco", ["costco.ca", "costco.com"]),
        new ReceiptSender("GAP", ["gap.com", "gapcanada.ca"]),
        new ReceiptSender("Sleep Country", ["sleepcountry.ca"]),
        new ReceiptSender("Canadian Tire", ["canadiantire.ca"]),
        new ReceiptSender("Sephora", ["sephora.com", "sephora.ca"]),
        new ReceiptSender("Etsy", ["etsy.com"]),
        new ReceiptSender("eBay", ["ebay.com", "ebay.ca"]),
        new ReceiptSender("AliExpress", ["aliexpress.com"]),
        new ReceiptSender("OnePlus", ["oneplus.com"]),
        new ReceiptSender("Shoppers Drug Mart", ["shoppersdrugmart.ca"]),
    }.ToDictionary(s => s.Merchant, StringComparer.OrdinalIgnoreCase);

    /// <summary>The receipt sender for a merchant, or null when FinSight has no receipt source for it.</summary>
    public static ReceiptSender? For(string merchant) =>
        !string.IsNullOrWhiteSpace(merchant) && ByMerchant.TryGetValue(merchant.Trim(), out var sender) ? sender : null;

    public static bool Knows(string merchant) => For(merchant) is not null;
}
