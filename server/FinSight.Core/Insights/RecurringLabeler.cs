using System.Text.RegularExpressions;
using FinSight.Core.Categories;

namespace FinSight.Core.Insights;

/// <summary>
/// Labels a recurring expense without AI: known subscription, membership and loan merchants first, then the category
/// (streaming, music and software are subscriptions; utilities, phone, insurance and housing are bills; gyms are memberships).
/// Used whenever Gemini's review is unavailable, and for anything the review didn't label.
/// </summary>
public static partial class RecurringLabeler
{
    [GeneratedRegex(@"\b(LOAN|LENDING|FINANCING|FINANCE (PAYMENT|PMT)|AUTO ?(LOAN|FINANCE)|CAR PAYMENT|STUDENT AID|OSAP|NSLSC|SALLIE MAE|NAVIENT|AFFIRM|KLARNA|AFTERPAY|SEZZLE|PAYBRIGHT)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Loans();

    [GeneratedRegex(@"\b(MEMBERSHIP|CAA\b|AAA\b|YMCA|CLUB FEES?|UNION DUES|ASSOCIATION DUES)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Memberships();

    [GeneratedRegex(@"\b(GITHUB|NOTION|CANVA|SLACK|ZOOM|FIGMA|1PASSWORD|LASTPASS|BITWARDEN|NORDVPN|EXPRESSVPN|PROTON|ICLOUD|GOOGLE ONE|SIRIUSXM|TIDAL|DEEZER|APPLE MUSIC|APPLE TV|TWITCH|LINKEDIN PREMIUM|NEW YORK TIMES|NYTIMES|WASHINGTON POST|GLOBE AND MAIL|THE ATHLETIC|SUBSTACK|DUOLINGO|HEADSPACE|MYFITNESSPAL|STRAVA|GAME ?PASS|PLAYSTATION PLUS|NINTENDO SWITCH ONLINE|CRUNCHYROLL|DAZN|FUBO|PHILO|BRITBOX|MUBI|KINDLE UNLIMITED|SCRIBD|EVERAND)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Software();

    /// <returns>The kind, or null when nothing identifies it (shown as "other").</returns>
    public static RecurringKind? Label(string merchant, CategoryDefinition category)
    {
        ArgumentNullException.ThrowIfNull(merchant);
        ArgumentNullException.ThrowIfNull(category);

        if (Loans().IsMatch(merchant) || category.Id == "housing.mortgage")
        {
            return RecurringKind.Loan;
        }

        if (category.Id == "health.fitness" || Memberships().IsMatch(merchant))
        {
            return RecurringKind.Membership;
        }

        if (category.Id == CategoryTaxonomy.Subscriptions || Software().IsMatch(merchant) || IsKnownSubscription(merchant))
        {
            return RecurringKind.Subscription;
        }

        if (category.IsFixed || category.GroupId == "housing")
        {
            return RecurringKind.Bill;
        }

        return null;
    }

    /// <summary>Merchants the categorization catalog already knows as subscriptions (Netflix, Spotify, Adobe…).</summary>
    private static bool IsKnownSubscription(string merchant) =>
        MerchantCatalog.Merchants.Any(e => e.CategoryId == CategoryTaxonomy.Subscriptions && e.Regex.IsMatch(merchant));
}
