using FinSight.Core.Domain;

namespace FinSight.Core.Categories;

public enum CategoryKind
{
    Income,
    Expense,
    Transfer,
}

public sealed record CategoryGroup(string Id, string Name);

/// <param name="Id">Stable dotted id, e.g. <c>food.restaurants</c>. Stored on transactions.</param>
/// <param name="IsFixed">Default classification for fixed vs. variable spending.</param>
public sealed record CategoryDefinition(
    string Id,
    string Name,
    string GroupId,
    CategoryKind Kind,
    bool IsFixed = false);

/// <summary>
/// The built-in category tree. Users can extend it with <see cref="CustomCategory"/> rows, whose
/// ids are always prefixed <c>custom.</c>.
/// </summary>
public static class CategoryTaxonomy
{
    public const string Uncategorized = "other.uncategorized";
    public const string Refunds = "income.refunds";
    public const string Transfers = "financial.transfers";
    public const string CreditCardPayments = "financial.credit-card-payments";
    public const string Investments = "financial.investments";
    public const string BankFees = "financial.bank-fees";
    public const string InterestCharges = "financial.interest-charges";
    public const string InterestIncome = "income.interest";
    public const string Subscriptions = "entertainment.subscriptions";

    public static readonly IReadOnlyList<CategoryGroup> Groups =
    [
        new("income", "Income"),
        new("housing", "Housing"),
        new("food", "Food"),
        new("transportation", "Transportation"),
        new("shopping", "Shopping"),
        new("entertainment", "Entertainment"),
        new("financial", "Financial"),
        new("health", "Health"),
        new("travel", "Travel"),
        new("personal", "Personal"),
        new("other", "Other"),
    ];

    public static readonly IReadOnlyList<CategoryDefinition> All =
    [
        new("income.salary", "Salary", "income", CategoryKind.Income),
        new("income.freelance", "Freelance", "income", CategoryKind.Income),
        new(InterestIncome, "Interest", "income", CategoryKind.Income),
        new(Refunds, "Refunds", "income", CategoryKind.Income),
        new("income.government", "Government & Benefits", "income", CategoryKind.Income),
        new("income.other", "Other Income", "income", CategoryKind.Income),

        new("housing.rent", "Rent", "housing", CategoryKind.Expense, IsFixed: true),
        new("housing.mortgage", "Mortgage", "housing", CategoryKind.Expense, IsFixed: true),
        new("housing.utilities", "Utilities", "housing", CategoryKind.Expense, IsFixed: true),
        new("housing.phone-internet", "Phone & Internet", "housing", CategoryKind.Expense, IsFixed: true),
        new("housing.home", "Home", "housing", CategoryKind.Expense),

        new("food.groceries", "Groceries", "food", CategoryKind.Expense),
        new("food.restaurants", "Restaurants", "food", CategoryKind.Expense),
        new("food.coffee", "Coffee", "food", CategoryKind.Expense),
        new("food.delivery", "Delivery", "food", CategoryKind.Expense),

        new("transportation.public-transit", "Public Transit", "transportation", CategoryKind.Expense),
        new("transportation.fuel", "Fuel", "transportation", CategoryKind.Expense),
        new("transportation.parking", "Parking", "transportation", CategoryKind.Expense),
        new("transportation.ride-sharing", "Ride Sharing", "transportation", CategoryKind.Expense),
        new("transportation.car", "Car", "transportation", CategoryKind.Expense),

        new("shopping.clothing", "Clothing", "shopping", CategoryKind.Expense),
        new("shopping.electronics", "Electronics", "shopping", CategoryKind.Expense),
        new("shopping.general", "General Shopping", "shopping", CategoryKind.Expense),

        new("entertainment.games", "Games", "entertainment", CategoryKind.Expense),
        new("entertainment.movies", "Movies", "entertainment", CategoryKind.Expense),
        new("entertainment.events", "Events", "entertainment", CategoryKind.Expense),
        new(Subscriptions, "Subscriptions", "entertainment", CategoryKind.Expense, IsFixed: true),

        new(BankFees, "Bank Fees", "financial", CategoryKind.Expense),
        new(InterestCharges, "Interest Charges", "financial", CategoryKind.Expense),
        new("financial.insurance", "Insurance", "financial", CategoryKind.Expense, IsFixed: true),
        new("financial.taxes", "Taxes", "financial", CategoryKind.Expense),
        new(CreditCardPayments, "Credit Card Payments", "financial", CategoryKind.Transfer),
        new(Investments, "Investments", "financial", CategoryKind.Transfer),
        new(Transfers, "Transfers", "financial", CategoryKind.Transfer),

        new("health.pharmacy", "Pharmacy", "health", CategoryKind.Expense),
        new("health.medical", "Medical", "health", CategoryKind.Expense),
        new("health.fitness", "Fitness", "health", CategoryKind.Expense),

        new("travel.flights", "Flights", "travel", CategoryKind.Expense),
        new("travel.hotels", "Hotels", "travel", CategoryKind.Expense),
        new("travel.other", "Travel", "travel", CategoryKind.Expense),

        new("personal.care", "Personal Care", "personal", CategoryKind.Expense),
        new("personal.education", "Education", "personal", CategoryKind.Expense),
        new("personal.gifts-donations", "Gifts & Donations", "personal", CategoryKind.Expense),

        new("other.cash", "Cash Withdrawal", "other", CategoryKind.Expense),
        new("other.person-to-person", "Person-to-Person", "other", CategoryKind.Expense),
        new(Uncategorized, "Other", "other", CategoryKind.Expense),
    ];

    private static readonly Dictionary<string, CategoryDefinition> ById =
        All.ToDictionary(c => c.Id, StringComparer.Ordinal);

    private static readonly Dictionary<string, CategoryGroup> GroupById =
        Groups.ToDictionary(g => g.Id, StringComparer.Ordinal);

    public static bool IsBuiltIn(string id) => ById.ContainsKey(id);

    public static CategoryDefinition? Find(string id) => ById.GetValueOrDefault(id);

    public static CategoryGroup? FindGroup(string id) => GroupById.GetValueOrDefault(id);

    /// <summary>Resolves built-in or custom categories; unknown ids fall back to Uncategorized.</summary>
    public static CategoryDefinition Resolve(string id, IEnumerable<CustomCategory>? custom = null)
    {
        if (ById.TryGetValue(id, out var builtIn))
        {
            return builtIn;
        }

        var match = custom?.FirstOrDefault(c => c.CategoryId == id);
        return match is not null
            ? new CategoryDefinition(match.CategoryId, match.Name, match.GroupId, CategoryKind.Expense)
            : ById[Uncategorized];
    }

    /// <summary>The transaction type a category implies for a given cash direction.</summary>
    public static TransactionType ImpliedType(CategoryDefinition category, decimal amount) => category.Kind switch
    {
        CategoryKind.Transfer => TransactionType.Transfer,
        CategoryKind.Income => amount >= 0 ? TransactionType.Income : TransactionType.Expense,
        _ => TransactionType.Expense,
    };
}
