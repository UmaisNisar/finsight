using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FinSight.Core.Domain;
using FinSight.Core.Parsing;
using FinSight.Core.Text;

namespace FinSight.Core.Import.Csv;

/// <summary>
/// Reads transaction CSVs downloaded from online banking. CSV has no standard, so the layout is detected, never assumed:
/// the delimiter, an optional header row (and preamble lines above it), how dates are written across every row, the
/// description columns, and whether amounts are one signed column or separate debit and credit columns.
/// </summary>
/// <remarks>
/// <para>
/// Headers are matched by meaning ("Transaction Date", "Funds Out", "CAD$", "Withdrawals"), which covers generic exports and
/// the headed Canadian layouts: RBC, Tangerine, Scotiabank, Simplii, BMO and American Express. Files without a header are
/// mapped from what each column holds; CIBC (<c>date,description,debit,credit[,card number]</c>), TD
/// (<c>date,description,debit,credit,balance</c>) and Scotiabank's older export (<c>date,amount,-,description,description</c>)
/// are recognised by their shape, which also names the bank. The bank's name only labels the statement; parsing never
/// depends on it.
/// </para>
/// <para>
/// Card and account numbers are reduced to their last four digits as they are read and descriptions are masked, so a full
/// number never leaves this class. When the columns can't be mapped with confidence the result is
/// <see cref="ParseFailure.Unrecognized"/>, never a guess.
/// </para>
/// </remarks>
public sealed partial class CsvStatementParser(StructuredImportLimits limits) : IStatementFileParser
{
    /// <summary>Lines of other text allowed above the table, like BMO's "Following data is valid as of" note.</summary>
    public const int MaxPreambleRows = 15;

    public const string UnrecognizedWarning = "The CSV's columns couldn't be matched to dates, descriptions and amounts.";

    [GeneratedRegex(@"^(?:\d{4,6}[*xX•#]{2,}\d{3,4}|[*xX•]{4,}[ -]?\d{4}|\d{13,19})$")]
    private static partial Regex CardNumberShape();

    public ParsedStatement Parse(ReadOnlyMemory<byte> content, StatementParseContext context, CancellationToken cancellationToken = default)
    {
        var clock = new ParseClock(limits.Timeout, cancellationToken);
        clock.Check();
        try
        {
            var text = TextDecoding.Decode(content.Span);
            var table = CsvTable.Read(text, limits.MaxTransactions + MaxPreambleRows + 1, clock);
            return table is null
                ? StructuredStatement.Failed(context, ParseFailure.Unrecognized, UnrecognizedWarning)
                : Map(table.Value.Delimiter, table.Value.Rows, context, clock);
        }
        catch (ParseLimitExceededException)
        {
            return StructuredStatement.Failed(context, ParseFailure.TooLarge, $"The file has more than {limits.MaxTransactions:N0} rows or took too long to read.");
        }
    }

    private ParsedStatement Map(char delimiter, List<string[]> rows, StatementParseContext context, ParseClock clock)
    {
        var decimalComma = delimiter == ';';
        var width = rows.GroupBy(r => r.Length).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;
        var start = rows.FindIndex(r => r.Length >= width);
        if (start < 0 || start > MaxPreambleRows)
        {
            return Unrecognized(context);
        }

        var layout = IsHeader(rows[start], decimalComma)
            ? FromHeader(rows[start], rows, start + 1)
            : Headerless(rows, start, width, decimalComma);

        if (layout?.DateReader is null)
        {
            return Unrecognized(context);
        }

        return Read(layout, rows, decimalComma, context, clock);
    }

    private ParsedStatement Read(Layout layout, List<string[]> rows, bool decimalComma, StatementParseContext context, ParseClock clock)
    {
        var reader = layout.DateReader!;
        var parsed = new List<Row>();
        var masks = new HashSet<string>(StringComparer.Ordinal);
        var accountTypes = new HashSet<AccountType>();
        var currencies = new Dictionary<string, int>(StringComparer.Ordinal);
        int bad = 0, pending = 0;
        var needed = layout.Columns.Max() + 1;

        for (var i = layout.FirstDataRow; i < rows.Count; i++)
        {
            if (i % 512 == 0)
            {
                clock.Check();
            }

            var row = rows[i];
            if (row.Length < needed)
            {
                // A short line is a footer ("Total", "End of report") unless it starts like a transaction.
                bad += row.Any(CsvDates.LooksLikeDate) ? 1 : 0;
                continue;
            }

            if (layout.Status is { } status && IsPending(row[status]))
            {
                pending++;
                continue;
            }

            if (reader.Parse(row[layout.Date]) is not { } date)
            {
                bad++;
                continue;
            }

            decimal amount;
            if (layout.Amount is { } amountColumn)
            {
                if (!CsvAmounts.TryParse(row[amountColumn], decimalComma, out amount, out _))
                {
                    bad += row[amountColumn].Length == 0 ? 0 : 1;
                    continue;
                }
            }
            else
            {
                var debitText = row[layout.Debit!.Value];
                var creditText = row[layout.Credit!.Value];
                if (IsEmpty(debitText) && IsEmpty(creditText))
                {
                    continue;
                }

                decimal debit = 0, credit = 0;
                if ((!IsEmpty(debitText) && !CsvAmounts.TryParse(debitText, decimalComma, out debit, out _))
                    || (!IsEmpty(creditText) && !CsvAmounts.TryParse(creditText, decimalComma, out credit, out _)))
                {
                    bad++;
                    continue;
                }

                // A debit column holds money out however it is signed; a credit column is money in unless it says otherwise.
                amount = credit - Math.Abs(debit);
            }

            decimal? balance = layout.Balance is { } balanceColumn && CsvAmounts.TryParse(row[balanceColumn], decimalComma, out var b, out _) ? b : null;
            DateOnly? posting = layout.PostingDate is { } postingColumn ? reader.Parse(row[postingColumn]) : null;

            if (layout.AccountNumber is { } accountColumn && SensitiveDataMasker.LastFourOf(row[accountColumn]) is { } mask)
            {
                masks.Add(mask);
            }

            if (layout.AccountTypeColumn is { } typeColumn && AccountTypeOf(row[typeColumn]) is var type && type != AccountType.Unknown)
            {
                accountTypes.Add(type);
            }

            if (layout.Currency is { } currencyColumn && row[currencyColumn].Trim().ToUpperInvariant() is { Length: 3 } code && code.All(char.IsAsciiLetterUpper))
            {
                currencies[code] = currencies.GetValueOrDefault(code) + 1;
            }

            parsed.Add(new Row(date, posting, Description(row, layout), amount, balance, layout.Type is { } t ? row[t] : null));
        }

        if (bad > Math.Max(2, (parsed.Count + bad) / 20))
        {
            return Unrecognized(context);
        }

        if (parsed.Count > limits.MaxTransactions)
        {
            throw new ParseLimitExceededException();
        }

        if (masks.Count > 1 || accountTypes.Count > 1)
        {
            return StructuredStatement.Failed(context, ParseFailure.MultipleAccounts, "The file has transactions from more than one account.");
        }

        var warnings = new List<string>();
        if (bad > 0)
        {
            warnings.Add($"{bad} row{(bad == 1 ? "" : "s")} couldn't be read and {(bad == 1 ? "was" : "were")} skipped.");
        }

        if (pending > 0)
        {
            warnings.Add($"{pending} pending transaction{(pending == 1 ? " was" : "s were")} skipped. Import them again once they post.");
        }

        if (reader.Ambiguous)
        {
            warnings.Add("Dates in this file could be read as day/month or month/day. FinSight read them as month/day.");
        }

        var accountType = accountTypes.Count == 1 ? accountTypes.Single() : layout.AccountType;
        var typeSigned = layout.Amount is not null && layout.Type is not null && parsed.All(r => r.Amount >= 0) && ApplyTypeSigns(parsed);
        if (accountType == AccountType.CreditCard && layout.Amount is not null && !typeSigned
            && StructuredStatement.ChargesArePositive(parsed.Select(r => (r.Description, r.Amount)), majorityDecides: true))
        {
            for (var i = 0; i < parsed.Count; i++)
            {
                parsed[i] = parsed[i] with { Amount = -parsed[i].Amount };
            }

            warnings.Add(StructuredStatement.ReversedSignsWarning);
        }

        var currency = currencies.Count > 0 ? currencies.MaxBy(c => c.Value).Key : layout.CurrencyCode ?? context.DefaultCurrency;

        // Balances follow the rows in date order, whichever way the file lists them.
        var chronological = parsed.Count > 1 && parsed[0].Date > parsed[^1].Date ? Enumerable.Reverse(parsed).ToList() : parsed;
        decimal? opening = null, closing = null;
        if (layout.Balance is not null && chronological.Count > 0 && chronological[0].Balance is { } firstBalance && chronological[^1].Balance is { } lastBalance)
        {
            opening = firstBalance - chronological[0].Amount;
            closing = lastBalance;
        }

        var transactions = chronological
            .Select(r => new ParsedTransaction(r.Date, r.PostingDate, SensitiveDataMasker.Mask(r.Description), r.Amount, r.Balance, StructuredStatement.Confidence, 1))
            .ToList();

        return StructuredStatement.Build(transactions, layout.Institution, accountType, layout.MaskFromColumn ? masks.SingleOrDefault() : null,
            currency, null, null, opening, closing, warnings);
    }

    /// <summary>Unsigned amounts with a Debit/Credit type column: the type gives the sign. False when the types don't say.</summary>
    private static bool ApplyTypeSigns(List<Row> rows)
    {
        var signs = rows.Select(r => SignOf(r.Type)).ToList();
        if (signs.Count(s => s != 0) < rows.Count * 0.9)
        {
            return false;
        }

        for (var i = 0; i < rows.Count; i++)
        {
            rows[i] = rows[i] with { Amount = signs[i] < 0 ? -rows[i].Amount : rows[i].Amount };
        }

        return true;
    }

    private static int SignOf(string? type) => Normalize(type ?? "") switch
    {
        "debit" or "dr" or "withdrawal" or "debit card" or "retrait" => -1,
        "credit" or "cr" or "deposit" or "depot" => 1,
        _ => 0,
    };

    private static bool IsPending(string status) => Normalize(status) is "pending" or "authorized" or "authorised" or "en attente";

    private static bool IsEmpty(string value) => value.Length == 0 || value == "-";

    private static string Description(string[] row, Layout layout)
    {
        var parts = new List<string>();
        foreach (var index in layout.Description.Concat(layout.SecondaryDescription))
        {
            var value = string.Join(' ', row[index].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (IsEmpty(value) || parts.Any(p => p.Contains(value, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            parts.Add(value);
        }

        return parts.Count == 0 ? "Transaction" : string.Join(' ', parts);
    }

    private static ParsedStatement Unrecognized(StatementParseContext context) =>
        StructuredStatement.Failed(context, ParseFailure.Unrecognized, UnrecognizedWarning);

    // ---------------------------------------------------------------------------------------------------------------
    // Files with a header row

    private static bool IsHeader(string[] row, bool decimalComma) =>
        row.All(c => !CsvDates.LooksLikeDate(c) && !CsvAmounts.TryParse(c, decimalComma, out _, out _))
        && row.Any(c => c.Length > 0 && !c.All(char.IsAsciiDigit));

    private enum Role
    {
        None,
        Date,
        PostingDate,
        Description,
        SecondaryDescription,
        Amount,
        UsdAmount,
        Debit,
        Credit,
        Balance,
        CardNumber,
        AccountNumber,
        DebitCardNumber,
        Cardholder,
        AccountType,
        Currency,
        Type,
        Status,
    }

    private static readonly string[] PostingDateWords = ["post", "process", "value", "book", "effective", "settle"];

    private static Role Classify(string name) => name switch
    {
        "" => Role.None,
        "first bank card" => Role.DebitCardNumber,
        "cardmember" or "card member" or "cardholder" or "card holder" or "card holder name" or "cardholder name" => Role.Cardholder,
        _ when name.Contains("card", StringComparison.Ordinal) && (name.Contains("number", StringComparison.Ordinal) || name.Contains('#') || name.EndsWith(" no", StringComparison.Ordinal) || name == "card") => Role.CardNumber,
        _ when (name.Contains("account", StringComparison.Ordinal) && (name.Contains("number", StringComparison.Ordinal) || name.Contains('#') || name.EndsWith(" no", StringComparison.Ordinal))) || name is "account" or "acct" => Role.AccountNumber,
        "account type" => Role.AccountType,
        "currency" or "currency code" or "devise" => Role.Currency,
        "status" or "transaction status" => Role.Status,
        "transaction type" or "type" or "type of transaction" or "transaction" or "dr cr" or "cr dr" or "debit credit" or "credit debit" => Role.Type,
        _ when name.Contains("balance", StringComparison.Ordinal) || name.Contains("solde", StringComparison.Ordinal) => Role.Balance,
        _ when name.Contains("date", StringComparison.Ordinal) =>
            PostingDateWords.Any(w => name.Contains(w, StringComparison.Ordinal)) ? Role.PostingDate : Role.Date,
        _ when name.Contains("foreign", StringComparison.Ordinal) || name.Contains("original", StringComparison.Ordinal) => Role.None,
        "usd$" or "usd" or "amount usd" or "usd amount" => Role.UsdAmount,
        _ when name.Contains("debit", StringComparison.Ordinal) || name.Contains("withdraw", StringComparison.Ordinal)
            || name is "funds out" or "money out" or "paid out" or "out" or "charges" or "charge" or "purchases" or "retrait" or "retraits" => Role.Debit,
        _ when name.Contains("credit", StringComparison.Ordinal) || name.Contains("deposit", StringComparison.Ordinal)
            || name is "funds in" or "money in" or "paid in" or "in" or "payments" or "depot" or "depots" => Role.Credit,
        _ when name.Contains("amount", StringComparison.Ordinal) || name is "cad$" or "cad" or "amt" or "montant" or "value" => Role.Amount,
        "description 2" or "memo" or "sub description" or "notes" or "note" or "extended details" => Role.SecondaryDescription,
        _ when name.Contains("description", StringComparison.Ordinal)
            || name is "name" or "payee" or "merchant" or "merchant name" or "details" or "transaction details" or "narrative" or "particulars" or "libelle" => Role.Description,
        _ => Role.None,
    };

    private static Layout? FromHeader(string[] header, List<string[]> rows, int firstDataRow)
    {
        var names = header.Select(Normalize).ToArray();
        var roles = names.Select(Classify).ToArray();
        int? First(Role role) => Array.IndexOf(roles, role) is var i and >= 0 ? i : null;
        List<int> All(Role role) => roles.Select((r, i) => (r, i)).Where(x => x.r == role).Select(x => x.i).ToList();

        var date = First(Role.Date) ?? First(Role.PostingDate);
        var layout = new Layout
        {
            FirstDataRow = firstDataRow,
            PostingDate = First(Role.Date) is not null ? First(Role.PostingDate) : null,
            Description = All(Role.Description),
            SecondaryDescription = All(Role.SecondaryDescription),
            Debit = First(Role.Debit),
            Credit = First(Role.Credit),
            Balance = First(Role.Balance),
            AccountNumber = First(Role.CardNumber) ?? First(Role.AccountNumber),
            AccountTypeColumn = First(Role.AccountType),
            Currency = First(Role.Currency),
            Type = First(Role.Type),
            Status = First(Role.Status),
            MaskFromColumn = true,
        };

        if (date is null)
        {
            return null;
        }

        layout.Date = date.Value;
        if (layout.Description.Count == 0)
        {
            (layout.Description, layout.SecondaryDescription) = (layout.SecondaryDescription, []);
        }

        if (layout.Debit is null || layout.Credit is null)
        {
            layout.Debit = layout.Credit = null;
            var cad = First(Role.Amount);
            var usd = First(Role.UsdAmount);
            int Filled(int? column) => column is { } c ? rows.Skip(firstDataRow).Count(r => r.Length > c && r[c].Length > 0) : -1;
            if (usd is not null && Filled(usd) > Filled(cad))
            {
                layout.Amount = usd;
                layout.CurrencyCode = "USD";
            }
            else
            {
                layout.Amount = cad;
            }

            if (layout.Amount is null)
            {
                return null;
            }
        }

        if (layout.Description.Count == 0)
        {
            return null;
        }

        if (First(Role.CardNumber) is not null)
        {
            layout.AccountType = AccountType.CreditCard;
        }

        IdentifyHeaderedBank(names, roles, layout);
        layout.DateReader = CsvDates.Detect(ColumnValues(rows, firstDataRow, layout.Date));
        return layout;
    }

    /// <summary>Names the bank from its export's exact header, and settles what the header alone implies about the account.</summary>
    private static void IdentifyHeaderedBank(string[] names, Role[] roles, Layout layout)
    {
        var set = names.Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);
        bool Has(params string[] required) => required.All(set.Contains);

        if (Has("account type", "account number", "cheque number", "description 1", "cad$"))
        {
            layout.Institution = "RBC Royal Bank";
        }
        else if (set.SetEquals(["transaction date", "transaction", "name", "memo", "amount"]))
        {
            layout.Institution = "Tangerine";
        }
        else if (roles.Contains(Role.Cardholder) && Has("date processed"))
        {
            layout.Institution = "American Express";
            layout.AccountType = AccountType.CreditCard;
        }
        else if (Has("filter", "sub description", "type of transaction"))
        {
            layout.Institution = "Scotiabank";
        }
        else if (set.SetEquals(["date", "transaction details", "funds out", "funds in"]))
        {
            layout.Institution = "Simplii Financial";
        }
        else if (Has("first bank card"))
        {
            // BMO's bank account export lists the debit card number, which isn't the account number.
            layout.Institution = "BMO";
            layout.AccountType = AccountType.Chequing;
        }
        else if (Has("item #", "card #"))
        {
            layout.Institution = "BMO";
            layout.AccountType = AccountType.CreditCard;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Files without a header row

    private static Layout? Headerless(List<string[]> rows, int start, int width, bool decimalComma)
    {
        var data = rows.Skip(start).Where(r => r.Length == width).ToList();
        if (data.Count == 0)
        {
            return null;
        }

        var tolerance = Math.Max(1, data.Count / 50);
        var columns = Enumerable.Range(0, width).Select(c =>
        {
            var values = data.Select(r => r[c]).ToList();
            var filled = values.Where(v => !IsEmpty(v)).ToList();
            var money = filled.Select(v => CsvAmounts.TryParse(v, decimalComma, out _, out var cents) ? (cents ? 2 : 1) : 0).ToList();
            return new ColumnStats(
                c,
                filled.Count,
                filled.Count(CsvDates.LooksLikeDate),
                money.Count(m => m > 0),
                money.Count(m => m == 2),
                filled.Count(v => CardNumberShape().IsMatch(v.Replace(" ", "", StringComparison.Ordinal))),
                values.All(IsEmpty));
        }).ToList();

        var dateColumn = columns.FirstOrDefault(s => s.Filled >= data.Count - tolerance && s.Dates >= s.Filled - tolerance && s.Dates > 0);
        if (dateColumn is null)
        {
            return null;
        }

        var cardColumn = columns.FirstOrDefault(s => s.Index != dateColumn.Index && s.Filled > 0 && s.Cards == s.Filled);
        var amounts = columns.Where(s => s.Index != dateColumn.Index && s != cardColumn && s.Filled > 0 && s.Money == s.Filled && s.Cents * 2 >= s.Filled).ToList();
        var texts = columns.Where(s => s.Index != dateColumn.Index && s != cardColumn && !amounts.Contains(s) && !s.Empty && s.Money < s.Filled).ToList();
        if (texts.Count == 0 || amounts.Count == 0)
        {
            return null;
        }

        bool AlwaysFilled(ColumnStats s) => s.Filled >= data.Count - tolerance;
        var layout = new Layout { Date = dateColumn.Index, FirstDataRow = start, Description = texts.Select(t => t.Index).ToList() };

        if (amounts.Count >= 2 && Exclusive(data, amounts[0].Index, amounts[1].Index, tolerance))
        {
            // Without a header, two alternating amount columns are debit then credit: the order every bank export uses.
            layout.Debit = amounts[0].Index;
            layout.Credit = amounts[1].Index;
            if (amounts.Count == 3 && AlwaysFilled(amounts[2]))
            {
                layout.Balance = amounts[2].Index;
            }
            else if (amounts.Count > 2)
            {
                return null;
            }
        }
        else if (AlwaysFilled(amounts[0]) && amounts.Count <= 2)
        {
            layout.Amount = amounts[0].Index;
            if (amounts.Count == 2)
            {
                if (!AlwaysFilled(amounts[1]))
                {
                    return null;
                }

                layout.Balance = amounts[1].Index;
            }
        }
        else
        {
            return null;
        }

        if (cardColumn is not null)
        {
            layout.AccountNumber = cardColumn.Index;
            layout.AccountType = AccountType.CreditCard;
            layout.MaskFromColumn = true;
        }

        layout.DateReader = CsvDates.Detect(ColumnValues(rows, start, layout.Date));
        if (layout.DateReader is not null)
        {
            IdentifyHeaderlessBank(layout, width, columns);
        }

        return layout;
    }

    private static void IdentifyHeaderlessBank(Layout layout, int width, List<ColumnStats> columns)
    {
        var date = layout.DateReader!;
        var descriptionIsSecond = layout.Date == 0 && layout.Description is [1];

        // CIBC: 2026-08-15,DESCRIPTION,12.34,, and for credit cards a fifth column with the masked card number.
        if (descriptionIsSecond && layout is { Debit: 2, Credit: 3, Balance: null } && date is { Order: "ymd", Separator: '-' }
            && (width == 4 || (width == 5 && layout.AccountNumber == 4)))
        {
            layout.Institution = "CIBC";
            if (width == 4)
            {
                layout.AccountType = AccountType.Chequing;
            }
        }
        // TD: 08/15/2026,DESCRIPTION,12.34,,1234.56 for bank accounts and cards alike.
        else if (descriptionIsSecond && width == 5 && layout is { Debit: 2, Credit: 3, Balance: 4 } && date is { Order: "mdy", Separator: '/' })
        {
            layout.Institution = "TD Bank";
            layout.DateReader = date with { Ambiguous = false };
        }
        // Scotiabank's older export: 8/15/2026,-12.34,-,"POS PURCHASE","MERCHANT".
        else if (layout.Date == 0 && layout.Amount == 1 && width is 4 or 5 && columns[2].Empty && layout.Description.All(i => i >= 3)
            && date is { Order: "mdy", Separator: '/' })
        {
            layout.Institution = "Scotiabank";
            layout.DateReader = date with { Ambiguous = false };
        }
    }

    private static bool Exclusive(List<string[]> data, int first, int second, int tolerance) =>
        data.Count(r => IsEmpty(r[first]) == IsEmpty(r[second])) <= tolerance;

    private static List<string> ColumnValues(List<string[]> rows, int from, int column) =>
        rows.Skip(from).Where(r => r.Length > column && r[column].Length > 0).Select(r => r[column]).ToList();

    private static AccountType AccountTypeOf(string value)
    {
        var name = Normalize(value);
        return name switch
        {
            _ when name.Contains("chequ", StringComparison.Ordinal) || name.Contains("check", StringComparison.Ordinal) => AccountType.Chequing,
            _ when name.Contains("saving", StringComparison.Ordinal) => AccountType.Savings,
            _ when name.Contains("line of credit", StringComparison.Ordinal) || name == "loc" => AccountType.LineOfCredit,
            _ when name.Contains("visa", StringComparison.Ordinal) || name.Contains("mastercard", StringComparison.Ordinal) || name.Contains("master card", StringComparison.Ordinal)
                || name.Contains("amex", StringComparison.Ordinal) || name.Contains("credit card", StringComparison.Ordinal) => AccountType.CreditCard,
            _ => AccountType.Unknown,
        };
    }

    /// <summary>Lower case, accents removed, punctuation other than <c>$</c> and <c>#</c> turned into single spaces.</summary>
    internal static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(c) || c is '$' or '#' ? char.ToLowerInvariant(c) : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed record Row(DateOnly Date, DateOnly? PostingDate, string Description, decimal Amount, decimal? Balance, string? Type);

    private sealed record ColumnStats(int Index, int Filled, int Dates, int Money, int Cents, int Cards, bool Empty);

    private sealed class Layout
    {
        public int FirstDataRow { get; set; }
        public int Date { get; set; }
        public int? PostingDate { get; set; }
        public List<int> Description { get; set; } = [];
        public List<int> SecondaryDescription { get; set; } = [];
        public int? Amount { get; set; }
        public int? Debit { get; set; }
        public int? Credit { get; set; }
        public int? Balance { get; set; }
        public int? AccountNumber { get; set; }
        public int? AccountTypeColumn { get; set; }
        public int? Currency { get; set; }
        public int? Type { get; set; }
        public int? Status { get; set; }

        /// <summary>The account number column identifies the account, so its last four digits are the statement's.</summary>
        public bool MaskFromColumn { get; set; }

        public string? CurrencyCode { get; set; }
        public string? Institution { get; set; }
        public AccountType AccountType { get; set; }
        public CsvDateReader? DateReader { get; set; }

        public IEnumerable<int> Columns =>
            new[] { Date, PostingDate, Amount, Debit, Credit, Balance, AccountNumber, AccountTypeColumn, Currency, Type, Status }
                .OfType<int>()
                .Concat(Description)
                .Concat(SecondaryDescription);
    }
}
