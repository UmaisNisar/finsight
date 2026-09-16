using System.Text.RegularExpressions;
using FinSight.Core.Domain;

namespace FinSight.Core.Categories;

public sealed record CategorizationInput(
    string Description,
    string MerchantKey,
    decimal Amount,
    AccountType AccountType);

public sealed record CategorizationResult(
    string CategoryId,
    TransactionType Type,
    CategorySource Source,
    double Confidence,
    bool IsRefund,
    string Reason)
{
    /// <summary>Below this, the transaction is a candidate for AI categorization.</summary>
    public const double AiThreshold = 0.6;

    public bool NeedsAi => Source == CategorySource.Default || Confidence < AiThreshold;
}

/// <summary>
/// Deterministic categorization. Order of precedence:
/// user merchant rules, financial movements (transfers, card payments, payroll, fees),
/// known merchants, generic keywords, then a low-confidence default.
/// </summary>
public static partial class RuleCategorizer
{
    [GeneratedRegex(@"\b(REFUND|RETURN(ED)?|CREDIT VOUCHER|MERCHANDISE CREDIT|CHARGEBACK|PURCHASE CREDIT)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RefundWords();

    [GeneratedRegex(@"\b(PAYROLL|SALARY|DIRECT DEP(OSIT)?|PAY ?CHEQUE|PAYCHECK|WAGES|PAY FROM|EMPLOYER|ADP\b|CERIDIAN|GUSTO|DAYFORCE|PAYWORKS|BACS CREDIT.*SALARY)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Payroll();

    [GeneratedRegex(@"\b(UPWORK|FIVERR|STRIPE ?(TRANSFER|PAYOUT)|SHOPIFY PAYOUT|PAYPAL TRANSFER|TOPTAL|CONTRACTOR PAYMENT|INVOICE PAYMENT)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Freelance();

    [GeneratedRegex(@"\b(CANADA CHILD|CCB\b|GST ?CREDIT|GST/HST|CRA\b.*(REFUND|CREDIT)|EI\b|EMPLOYMENT INSURANCE|CPP\b|OAS\b|IRS TREAS|TAX REFUND|SSA TREAS|HMRC|CHILD BENEFIT|UNIVERSAL CREDIT|GOVERNMENT OF)", RegexOptions.IgnoreCase)]
    private static partial Regex Government();

    [GeneratedRegex(@"\b(INTEREST (PAID|EARNED|CREDIT)|CREDIT INTEREST|INT PAID|DEPOSIT INTEREST|^INTEREST$)", RegexOptions.IgnoreCase)]
    private static partial Regex InterestEarned();

    [GeneratedRegex(@"\b(INTEREST CHARGE|PURCHASE INTEREST|CASH ADVANCE INTEREST|INTEREST ON|OVERDRAFT INTEREST|FINANCE CHARGE)", RegexOptions.IgnoreCase)]
    private static partial Regex InterestCharged();

    [GeneratedRegex(@"\b(MONTHLY (ACCOUNT |PLAN )?FEE|SERVICE (CHARGE|FEE)|ACCOUNT FEE|NSF|OVERDRAFT (FEE|CHARGE|PROTECTION)|ATM FEE|NETWORK FEE|FOREIGN (TRANSACTION|EXCHANGE) FEE|ANNUAL FEE|E-?TRANSFER FEE|WIRE FEE|LATE (PAYMENT )?FEE|CASH ADVANCE FEE|PLAN FEE|MAINTENANCE FEE|FEE$)", RegexOptions.IgnoreCase)]
    private static partial Regex BankFee();

    [GeneratedRegex(@"\b(PAYMENT ?- ?THANK YOU|PAYMENT THANK YOU|THANK YOU FOR YOUR PAYMENT|PAYMENT RECEIVED|AUTOPAY PAYMENT|AUTOMATIC PAYMENT|ONLINE PAYMENT RECEIVED|PAYMENT - ONLINE|PMT RECEIVED|BANK PAYMENT|PAIEMENT MERCI)", RegexOptions.IgnoreCase)]
    private static partial Regex CardPaymentReceived();

    [GeneratedRegex(@"\b(VISA|MASTERCARD|MASTER CARD|AMEX|AMERICAN EXPRESS|CREDIT CARD|CARD PAYMENT|CAPITAL ONE|DISCOVER CARD|CHASE CARD|CITI CARD|MBNA|TRIANGLE MASTERCARD)\b.*\b(PAYMENT|PMT|BILL)|\b(PAYMENT|PMT|BILL PAYMENT)\b.*\b(VISA|MASTERCARD|AMEX|AMERICAN EXPRESS|CREDIT CARD|CAPITAL ONE|MBNA)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CardPaymentSent();

    [GeneratedRegex(@"\b(WEALTHSIMPLE|QUESTRADE|VANGUARD|FIDELITY|SCHWAB|ROBINHOOD|INTERACTIVE BROKERS|ETRADE|E\*TRADE|TD DIRECT INVESTING|RBC DIRECT INVESTING|QTRADE|MOOMOO|BETTERMENT|WEALTHFRONT|TRADING 212|HARGREAVES|RRSP|TFSA|FHSA|RESP|401\(?K\)?|ROTH IRA|STOCKS AND SHARES ISA|MUTUAL FUND|GIC PURCHASE)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Investment();

    [GeneratedRegex(@"\b(TRANSFER (TO|FROM)|TFR (TO|FROM)|ONLINE TRANSFER|INTERNET TRANSFER|ACCOUNT TRANSFER|OWN ACCOUNT|BETWEEN ACCOUNTS|FUNDS TRANSFER|SAVINGS TRANSFER|TRANSFER-?SAVINGS|MOBILE TRANSFER|WWW TRANSFER|TRSF (TO|FROM)|INTERNAL TRANSFER|AUTO ?SAVE|ROUND[- ]?UP)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OwnTransfer();

    [GeneratedRegex(@"\b(E-?TRANSFER|INTERAC E|ZELLE|VENMO|CASH ?APP|PAYPAL (SENT|RECEIVED)|FASTER PAYMENT|SEND E-TFR|E-TFR|EMT\b|WISE\b|REVOLUT TRANSFER|POPMONEY)", RegexOptions.IgnoreCase)]
    private static partial Regex PersonToPerson();

    [GeneratedRegex(@"\b(ATM WITHDRAWAL|CASH WITHDRAWAL|ABM WITHDRAWAL|ATM W/D|ABM W/D|CASH ADVANCE)\b|^ATM\b|^ABM\b", RegexOptions.IgnoreCase)]
    private static partial Regex Cash();

    [GeneratedRegex(@"\b(RENT|RENTAL PAYMENT|PROPERTY MANAGEMENT|LANDLORD|APARTMENTS?|RESIDENTIAL|TENANT)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Rent();

    [GeneratedRegex(@"\b(MORTGAGE|MTG PMT|HOME LOAN)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Mortgage();

    [GeneratedRegex(@"\b(CRA|IRS|HMRC|PROPERTY TAX|INCOME TAX|TAX PAYMENT|CITY OF .* TAX)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Taxes();

    [GeneratedRegex(@"\b(DEPOSIT|MOBILE DEPOSIT|BRANCH DEPOSIT|CHEQUE DEPOSIT|CHECK DEPOSIT|ATM DEPOSIT)\b", RegexOptions.IgnoreCase)]
    private static partial Regex GenericDeposit();

    public static CategorizationResult Categorize(
        CategorizationInput input,
        IReadOnlyDictionary<string, MerchantRule>? merchantRules = null)
    {
        var description = input.Description;
        var inflow = input.Amount > 0;

        if (merchantRules is not null && merchantRules.TryGetValue(input.MerchantKey, out var rule))
        {
            var category = CategoryTaxonomy.Resolve(rule.CategoryId);
            var type = rule.Type ?? CategoryTaxonomy.ImpliedType(category, input.Amount);
            var isRefund = inflow && type == TransactionType.Expense;
            return new CategorizationResult(rule.CategoryId, type, rule.Source, rule.Confidence, isRefund,
                rule.Source == CategorySource.User ? "Your rule for this merchant" : rule.Reason ?? "Previously categorized");
        }

        // Money movements that are not income or spending.
        if (input.AccountType == AccountType.CreditCard && inflow && CardPaymentReceived().IsMatch(description))
        {
            return Transfer(CategoryTaxonomy.CreditCardPayments, 0.97, "Payment towards this credit card");
        }

        if (!inflow && CardPaymentSent().IsMatch(description))
        {
            return Transfer(CategoryTaxonomy.CreditCardPayments, 0.93, "Payment to a credit card");
        }

        if (Investment().IsMatch(description))
        {
            return Transfer(CategoryTaxonomy.Investments, 0.9, "Investment account contribution or withdrawal");
        }

        if (OwnTransfer().IsMatch(description) && !PersonToPerson().IsMatch(description))
        {
            return Transfer(CategoryTaxonomy.Transfers, 0.85, "Transfer between accounts");
        }

        // Fees and interest.
        if (!inflow && InterestCharged().IsMatch(description))
        {
            return Expense(CategoryTaxonomy.InterestCharges, 0.95, "Interest charged");
        }

        if (!inflow && BankFee().IsMatch(description))
        {
            return Expense(CategoryTaxonomy.BankFees, 0.93, "Bank or card fee");
        }

        if (inflow && InterestEarned().IsMatch(description))
        {
            return Income(CategoryTaxonomy.InterestIncome, 0.95, "Interest earned");
        }

        // Income.
        if (inflow && Payroll().IsMatch(description))
        {
            return Income("income.salary", 0.93, "Payroll deposit");
        }

        if (inflow && Government().IsMatch(description))
        {
            return Income("income.government", 0.88, "Government payment or benefit");
        }

        if (inflow && Freelance().IsMatch(description))
        {
            return Income("income.freelance", 0.85, "Freelance or platform payout");
        }

        var refundWords = RefundWords().IsMatch(description);

        // Known merchants first: "SONNET INSURANCE TENANT" is insurance, not rent.
        if (MatchCatalog(MerchantCatalog.Merchants, input, refundWords) is { } merchantMatch)
        {
            return merchantMatch;
        }

        // Housing and taxes are keyword-driven, because payees vary per person.
        if (!inflow && Mortgage().IsMatch(description))
        {
            return Expense("housing.mortgage", 0.9, "Mortgage payment");
        }

        if (!inflow && Rent().IsMatch(description) && !PersonToPerson().IsMatch(description))
        {
            return Expense("housing.rent", 0.8, "Rent payment");
        }

        if (!inflow && Taxes().IsMatch(description))
        {
            return Expense("financial.taxes", 0.8, "Tax payment");
        }

        if (Cash().IsMatch(description))
        {
            return inflow
                ? Income("income.other", 0.5, "Cash deposit")
                : Expense("other.cash", 0.9, "Cash withdrawal");
        }

        if (MatchCatalog(MerchantCatalog.Keywords, input, refundWords) is { } keywordMatch)
        {
            return keywordMatch;
        }

        if (PersonToPerson().IsMatch(description))
        {
            // Could be rent, a shared bill, or a transfer to yourself. Counted, but flagged as uncertain.
            return inflow
                ? Income("income.other", 0.45, "Person-to-person payment received")
                : Expense("other.person-to-person", 0.45, "Person-to-person payment sent");
        }

        if (inflow && refundWords)
        {
            return new CategorizationResult(CategoryTaxonomy.Refunds, TransactionType.Expense, CategorySource.Rule, 0.8, IsRefund: true, "Refund");
        }

        if (inflow)
        {
            return GenericDeposit().IsMatch(description)
                ? Income("income.other", 0.55, "Deposit")
                : new CategorizationResult("income.other", TransactionType.Income, CategorySource.Default, 0.3, false, "Unrecognized deposit");
        }

        return new CategorizationResult(CategoryTaxonomy.Uncategorized, TransactionType.Expense, CategorySource.Default, 0.2, false, "No matching rule");

        static CategorizationResult? MatchCatalog(IEnumerable<CatalogEntry> entries, CategorizationInput input, bool refundWords)
        {
            var entry = entries.FirstOrDefault(e => e.Regex.IsMatch(input.Description));
            if (entry is null)
            {
                return null;
            }

            var category = CategoryTaxonomy.Resolve(entry.CategoryId);
            if (input.Amount > 0 && category.Kind == CategoryKind.Expense)
            {
                // Money back from a merchant you normally pay is a refund, which offsets spending.
                return new CategorizationResult(entry.CategoryId, TransactionType.Expense, CategorySource.Rule,
                    refundWords ? 0.95 : Math.Min(entry.Confidence, 0.8), IsRefund: true, "Refund from a known merchant");
            }

            return new CategorizationResult(entry.CategoryId, CategoryTaxonomy.ImpliedType(category, input.Amount),
                CategorySource.Rule, entry.Confidence, IsRefund: false, entry.Merchant is null ? "Keyword match" : "Known merchant");
        }

        static CategorizationResult Transfer(string id, double confidence, string reason) =>
            new(id, TransactionType.Transfer, CategorySource.Rule, confidence, false, reason);

        static CategorizationResult Expense(string id, double confidence, string reason) =>
            new(id, TransactionType.Expense, CategorySource.Rule, confidence, false, reason);

        static CategorizationResult Income(string id, double confidence, string reason) =>
            new(id, TransactionType.Income, CategorySource.Rule, confidence, false, reason);
    }
}
