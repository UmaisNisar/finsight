using System.Text.RegularExpressions;
using FinSight.Core.Domain;
using FinSight.Core.Text;

namespace FinSight.Core.Parsing;

/// <summary>
/// Bank-agnostic statement parser. It works from word positions rather than plain text so that
/// debit, credit, amount and balance columns can be told apart, then resolves signs using (in order
/// of trust) column semantics, explicit markers (-, CR, parentheses) and the running balance.
/// </summary>
public static partial class StatementParser
{
    private const int MinimumWordsForTextLayer = 25;
    private const decimal BalanceTolerance = 0.015m;

    private enum ColumnRole
    {
        Date,
        PostDate,
        Description,
        Debit,
        Credit,
        Amount,
        Balance,
    }

    private sealed record Column(ColumnRole Role, double Left, double Right)
    {
        public double Center => (Left + Right) / 2;
    }

    private sealed record Token(string Text, double Left, double Right, ParsedMoney? Money)
    {
        public double Center => (Left + Right) / 2;
    }

    private sealed class RawRow
    {
        public required PartialDate Date { get; init; }
        public PartialDate? PostDate { get; init; }
        public required List<string> DescriptionParts { get; init; }
        public required double DescriptionLeft { get; init; }
        public required TextLine Line { get; set; }
        public decimal? Value { get; set; }
        public int? Sign { get; set; }
        public decimal? Balance { get; set; }
        public bool FromColumns { get; set; }
        public bool SignFromKeyword { get; set; }
        public bool BalanceVerified { get; set; }
        public bool HadExplicitMinus { get; set; }
        public int Continuations { get; set; }
    }

    [GeneratedRegex(@"\b(OPENING|CLOSING|BEGINNING|ENDING|STARTING|PREVIOUS|NEW|STATEMENT) (STATEMENT )?BALANCE\b|\bBALANCE (BROUGHT |CARRIED )?FORWARD\b|\b(BROUGHT|CARRIED) FORWARD\b|\bTOTALS?\b|\bSUB-?TOTAL\b|\bMINIMUM PAYMENT\b|\bPAYMENT DUE\b|\bCREDIT LIMIT\b|\bAVAILABLE CREDIT\b|\bSTATEMENT (DATE|PERIOD)\b|\bINTEREST RATE\b|\bANNUAL INTEREST\b|\bPAGE \d+ OF \d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex SummaryLine();

    [GeneratedRegex(@"\b(DEPOSIT|PAYROLL|SALARY|CREDIT|REFUND|INTEREST (PAID|EARNED)|TRANSFER FROM|AUTODEPOSIT|RECEIVED|PAYMENT FROM|RETURN)\b", RegexOptions.IgnoreCase)]
    private static partial Regex InflowWords();

    public static ParsedStatement Parse(PdfTextDocument document, StatementParseContext context)
    {
        var warnings = new List<string>();
        var lines = LineBuilder.Build(document);
        var fullText = string.Join('\n', lines.Select(l => l.Text));

        var accountType = StatementMetadataExtractor.DetectAccountType(fullText, context.Subject);
        var currency = StatementMetadataExtractor.DetectCurrency(fullText, context.DefaultCurrency);
        var institution = StatementMetadataExtractor.DetectInstitution(lines, context.SenderAddress);
        var accountMask = StatementMetadataExtractor.DetectAccountMask(fullText);
        var (periodStartRaw, periodEndRaw) = StatementMetadataExtractor.DetectPeriod(lines);

        if (document.WordCount < MinimumWordsForTextLayer)
        {
            var empty = new StatementMetadata(institution, accountType, accountMask, currency, null, null, null, null);
            return new ParsedStatement(empty, [], ["This PDF has no readable text layer. It may be a scanned image."], 0, false, ParseFailure.NoTextLayer);
        }

        var pageWidths = document.Pages.ToDictionary(p => p.Number, p => p.Width);
        var rows = new List<RawRow>();
        decimal? openingBalance = null;
        decimal? closingBalance = null;
        IReadOnlyList<Column>? columns = null;
        RawRow? last = null;

        foreach (var line in lines)
        {
            var tokens = Tokenize(line.Words);

            if (TryReadHeader(tokens, accountType, out var header))
            {
                columns = header;
                last = null;
                continue;
            }

            var texts = tokens.Select(t => t.Text).ToList();
            var hasDate = DateTokenParser.TryParseAt(texts, 0, out var date, out var consumed);

            // Dated rows are only skipped for balance labels, so merchants like "TOTAL WINE" survive.
            var isSummary = hasDate
                ? StatementMetadataExtractor.OpeningBalanceLabel().IsMatch(line.Text) || StatementMetadataExtractor.ClosingBalanceLabel().IsMatch(line.Text)
                : SummaryLine().IsMatch(line.Text);

            if (isSummary)
            {
                var money = tokens.LastOrDefault(t => t.Money is not null)?.Money;
                if (money is not null)
                {
                    if (StatementMetadataExtractor.OpeningBalanceLabel().IsMatch(line.Text))
                    {
                        openingBalance ??= Signed(money.Value);
                    }
                    else if (StatementMetadataExtractor.ClosingBalanceLabel().IsMatch(line.Text))
                    {
                        closingBalance = Signed(money.Value);
                    }
                }

                last = null;
                continue;
            }

            PartialDate? postDate = null;
            if (hasDate && DateTokenParser.TryParseAt(texts, consumed, out var second, out var consumedSecond))
            {
                postDate = second;
                consumed += consumedSecond;
            }

            var amounts = SelectAmountTokens(tokens, consumed, columns, pageWidths.GetValueOrDefault(line.Page, 612));

            if (hasDate && amounts.Count > 0)
            {
                var row = BuildRow(line, tokens, consumed, amounts, columns, accountType, date, postDate);
                if (row is not null)
                {
                    rows.Add(row);
                    last = row;
                }

                continue;
            }

            // Many banks print the date only on the first transaction of each day.
            if (!hasDate && amounts.Count > 0 && last is not null && columns is not null && line.Page == last.Line.Page)
            {
                var row = BuildRow(line, tokens, 0, amounts, columns, accountType, last.Date, last.PostDate);
                if (row is not null && row.DescriptionParts.Count > 0)
                {
                    rows.Add(row);
                    last = row;
                }

                continue;
            }

            // Wrapped description on the following line.
            if (!hasDate && amounts.Count == 0 && last is not null && last.Continuations < 2
                && line.Page == last.Line.Page
                && line.Top - last.Line.Bottom < Math.Max(4, last.Line.Height * 1.2)
                && line.Words[0].Left >= last.DescriptionLeft - 4
                && line.Text.Length <= 80)
            {
                last.DescriptionParts.Add(line.Text);
                last.Continuations++;
                last.Line = new TextLine { Page = line.Page, Words = [.. last.Line.Words, .. line.Words] };
                continue;
            }

            if (!hasDate)
            {
                last = null;
            }
        }

        if (rows.Count == 0)
        {
            var metadata = new StatementMetadata(institution, accountType, accountMask, currency, null, null, openingBalance, closingBalance);
            return new ParsedStatement(metadata, [], ["No transaction rows were recognized in this statement."], 0.1, false, ParseFailure.NoTransactions);
        }

        ResolveSigns(rows, openingBalance, accountType, warnings);

        var order = DetectDateOrder(rows, periodStartRaw, periodEndRaw, currency);
        var periodEnd = periodEndRaw?.Resolve(order, (_, _) => context.ReferenceDate.Year);
        var anchor = periodEnd ?? context.ReferenceDate;

        int InferYear(int month, int day)
        {
            var year = anchor.Year;
            var candidate = new DateOnly(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));
            return candidate > anchor.AddDays(15) ? year - 1 : year;
        }

        var periodStart = periodStartRaw?.Resolve(order, InferYear);
        var transactions = new List<ParsedTransaction>();

        foreach (var row in rows)
        {
            var resolvedDate = row.Date.Resolve(order, InferYear);
            if (resolvedDate is null || row.Value is null || row.Sign is null)
            {
                warnings.Add($"Skipped a row on page {row.Line.Page} with an unreadable date or amount.");
                continue;
            }

            var description = SensitiveDataMasker.Mask(string.Join(' ', row.DescriptionParts).Trim());
            if (description.Length == 0)
            {
                description = "Transaction";
            }

            var confidence = (row.FromColumns ? 0.72 : 0.6)
                + (row.BalanceVerified ? 0.25 : 0)
                - (row.SignFromKeyword ? 0.25 : 0);

            transactions.Add(new ParsedTransaction(
                resolvedDate.Value,
                row.PostDate?.Resolve(order, InferYear),
                description,
                row.Value.Value * row.Sign.Value,
                row.Balance,
                Math.Clamp(confidence, 0.05, 0.99),
                row.Line.Page));
        }

        var reconciled = Reconcile(transactions, openingBalance, closingBalance, warnings);

        periodStart ??= transactions.Min(t => t.Date);
        periodEnd ??= transactions.Max(t => t.Date);

        var outside = transactions.Count(t => t.Date < periodStart.Value.AddDays(-7) || t.Date > periodEnd.Value.AddDays(7));
        if (outside > 0)
        {
            warnings.Add($"{outside} transaction date(s) fall outside the statement period; dates may be misread.");
        }

        var statementConfidence = reconciled
            ? 0.97
            : Math.Round(transactions.Count == 0 ? 0.1 : transactions.Average(t => t.Confidence) * (outside > 0 ? 0.85 : 1), 2);

        return new ParsedStatement(
            new StatementMetadata(institution, accountType, accountMask, currency, periodStart, periodEnd, openingBalance, closingBalance),
            transactions,
            warnings.Distinct().ToList(),
            statementConfidence,
            reconciled,
            transactions.Count == 0 ? ParseFailure.NoTransactions : ParseFailure.None);
    }

    private static List<Token> Tokenize(IReadOnlyList<PdfWord> words)
    {
        var tokens = new List<Token>(words.Count);

        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            var text = word.Text;
            var right = word.Right;

            // "$" "12.00" → "$12.00"; "-" "12.00" → "-12.00"
            if (text is "$" or "€" or "£" or "-" or "−" or "(" && i + 1 < words.Count && words[i + 1].Left - right < 6)
            {
                text += words[i + 1].Text;
                right = words[i + 1].Right;
                i++;
            }

            // "12.00" "CR" → "12.00CR"
            if (i + 1 < words.Count && words[i + 1].Text.ToUpperInvariant() is "CR" or "DR" or "-" && words[i + 1].Left - right < 8
                && MoneyParser.IsMoney(text))
            {
                text += words[i + 1].Text;
                right = words[i + 1].Right;
                i++;
            }

            tokens.Add(new Token(text, word.Left, right, MoneyParser.TryParse(text, out var money) ? money : null));
        }

        return tokens;
    }

    private static bool TryReadHeader(List<Token> tokens, AccountType accountType, out IReadOnlyList<Column> columns)
    {
        columns = [];
        if (tokens.Count is < 2 or > 16 || tokens.Any(t => t.Money is not null))
        {
            return false;
        }

        // Group words into phrases: words closer than a space-and-a-half belong to one column heading.
        var phrases = new List<(string Text, double Left, double Right)>();
        foreach (var token in tokens)
        {
            if (phrases.Count > 0 && token.Left - phrases[^1].Right < 9)
            {
                var previous = phrases[^1];
                phrases[^1] = ($"{previous.Text} {token.Text}", previous.Left, token.Right);
            }
            else
            {
                phrases.Add((token.Text, token.Left, token.Right));
            }
        }

        // Summary boxes ("Statement date  Credit limit  Available credit") are not table headers.
        if (phrases.Any(p => Has(p.Text.ToUpperInvariant(), "LIMIT", "DUE", "MINIMUM", "AVAILABLE", "RATE", "SUMMARY")))
        {
            return false;
        }

        var result = new List<Column>();
        foreach (var (text, left, right) in phrases)
        {
            var upper = text.ToUpperInvariant();
            ColumnRole? role =
                Has(upper, "BALANCE") ? ColumnRole.Balance :
                Has(upper, "WITHDRAWAL", "DEBIT", "CHARGES", "PAID OUT", "MONEY OUT", "CHEQUES", "PURCHASES", "SPENT") ? ColumnRole.Debit :
                Has(upper, "DEPOSIT", "CREDIT", "PAID IN", "MONEY IN", "RECEIVED") ? ColumnRole.Credit :
                Has(upper, "PAYMENTS") ? (accountType == AccountType.CreditCard ? ColumnRole.Credit : ColumnRole.Debit) :
                Has(upper, "AMOUNT") ? ColumnRole.Amount :
                Has(upper, "POSTING", "POSTED", "POST DATE") ? ColumnRole.PostDate :
                Has(upper, "DATE") ? ColumnRole.Date :
                Has(upper, "DESCRIPTION", "DETAILS", "TRANSACTION", "PARTICULARS", "ACTIVITY", "MERCHANT", "PAYEE", "NARRATIVE") ? ColumnRole.Description :
                null;

            if (role is not null)
            {
                result.Add(new Column(role.Value, left, right));
            }
        }

        var hasDate = result.Any(c => c.Role is ColumnRole.Date or ColumnRole.PostDate);
        var hasMoney = result.Any(c => c.Role is ColumnRole.Debit or ColumnRole.Credit or ColumnRole.Amount);
        if (!hasDate || !hasMoney || result.Count < 3)
        {
            return false;
        }

        columns = result;
        return true;

        static bool Has(string value, params string[] needles) => needles.Any(value.Contains);
    }

    private static List<Token> SelectAmountTokens(List<Token> tokens, int startIndex, IReadOnlyList<Column>? columns, double pageWidth)
    {
        var candidates = tokens.Skip(startIndex).Where(t => t.Money is not null).ToList();
        if (candidates.Count == 0)
        {
            return candidates;
        }

        if (columns is not null)
        {
            var moneyColumns = columns.Where(c => c.Role is ColumnRole.Debit or ColumnRole.Credit or ColumnRole.Amount or ColumnRole.Balance).ToList();
            var descriptionColumn = columns.FirstOrDefault(c => c.Role == ColumnRole.Description);
            return candidates
                .Where(t => moneyColumns.Any(c => Distance(t, c) < 45)
                    && (descriptionColumn is null || t.Left > descriptionColumn.Left + 20))
                .ToList();
        }

        // Without a header, amounts are the right-most money tokens in the right half of the page.
        return candidates.Where(t => t.Right > pageWidth * 0.5).TakeLast(2).ToList();
    }

    private static RawRow? BuildRow(
        TextLine line,
        List<Token> tokens,
        int descriptionStart,
        List<Token> amounts,
        IReadOnlyList<Column>? columns,
        AccountType accountType,
        PartialDate date,
        PartialDate? postDate)
    {
        var firstAmount = amounts[0];
        var descriptionTokens = tokens.Skip(descriptionStart).Where(t => t.Left < firstAmount.Left && !amounts.Contains(t)).ToList();
        var row = new RawRow
        {
            Date = date,
            PostDate = postDate,
            DescriptionParts = descriptionTokens.Count > 0 ? [string.Join(' ', descriptionTokens.Select(t => t.Text))] : [],
            DescriptionLeft = descriptionTokens.Count > 0 ? descriptionTokens[0].Left : firstAmount.Left,
            Line = line,
        };

        if (columns is not null)
        {
            var moneyColumns = columns.Where(c => c.Role is ColumnRole.Debit or ColumnRole.Credit or ColumnRole.Amount or ColumnRole.Balance).ToList();
            var assigned = new Dictionary<ColumnRole, (Token Token, double Distance)>();
            foreach (var token in amounts)
            {
                var best = moneyColumns.OrderBy(c => Distance(token, c)).First();
                var distance = Distance(token, best);
                if (!assigned.TryGetValue(best.Role, out var existing) || existing.Distance > distance)
                {
                    assigned[best.Role] = (token, distance);
                }
            }

            if (assigned.TryGetValue(ColumnRole.Balance, out var balance))
            {
                row.Balance = Signed(balance.Token.Money!.Value);
            }

            if (assigned.TryGetValue(ColumnRole.Debit, out var debit))
            {
                row.Value = debit.Token.Money!.Value.Value;
                row.Sign = debit.Token.Money.Value.Sign is AmountSignHint.Negative or AmountSignHint.Credit ? 1 : -1;
                row.FromColumns = true;
            }
            else if (assigned.TryGetValue(ColumnRole.Credit, out var credit))
            {
                row.Value = credit.Token.Money!.Value.Value;
                row.Sign = credit.Token.Money.Value.Sign is AmountSignHint.Negative or AmountSignHint.Debit ? -1 : 1;
                row.FromColumns = true;
            }
            else if (assigned.TryGetValue(ColumnRole.Amount, out var amount))
            {
                ApplySignedAmount(row, amount.Token.Money!.Value, accountType);
                row.FromColumns = true;
            }
            else
            {
                return null;
            }

            return row;
        }

        // No header: last token is the balance when there are two.
        var amountToken = amounts.Count == 2 ? amounts[0] : amounts[^1];
        if (amounts.Count == 2)
        {
            row.Balance = Signed(amounts[1].Money!.Value);
        }

        ApplySignedAmount(row, amountToken.Money!.Value, accountType);
        return row;
    }

    private static void ApplySignedAmount(RawRow row, ParsedMoney money, AccountType accountType)
    {
        row.Value = money.Value;
        row.Sign = (money.Sign, accountType) switch
        {
            (AmountSignHint.Credit, _) => 1,
            (AmountSignHint.Debit, _) => -1,
            // Card statements print charges as positive and payments/refunds as negative.
            (AmountSignHint.Negative, AccountType.CreditCard or AccountType.LineOfCredit) => 1,
            (AmountSignHint.None, AccountType.CreditCard or AccountType.LineOfCredit) => -1,
            (AmountSignHint.Negative, _) => -1,
            _ => null,
        };
        row.HadExplicitMinus = money.Sign == AmountSignHint.Negative;
    }

    private static void ResolveSigns(List<RawRow> rows, decimal? openingBalance, AccountType accountType, List<string> warnings)
    {
        // Running balance direction: +1 if balance rises with inflows (bank accounts),
        // -1 if it rises with outflows (amount owed on a card).
        var direction = DetectBalanceDirection(rows, openingBalance, accountType);
        var previousBalance = openingBalance;

        foreach (var row in rows)
        {
            if (row.Balance is not null && previousBalance is not null && row.Value is not null)
            {
                var delta = (row.Balance.Value - previousBalance.Value) * direction;
                if (Math.Abs(delta - row.Value.Value) < BalanceTolerance)
                {
                    row.BalanceVerified = row.Sign is null or 1;
                    row.Sign ??= 1;
                }
                else if (Math.Abs(delta + row.Value.Value) < BalanceTolerance)
                {
                    row.BalanceVerified = row.Sign is null or -1;
                    row.Sign ??= -1;
                }
            }

            if (row.Balance is not null)
            {
                previousBalance = row.Balance;
            }
        }

        var unresolved = rows.Where(r => r.Sign is null).ToList();
        if (unresolved.Count == 0)
        {
            return;
        }

        // A single amount column where some rows carry a minus sign: unsigned rows are money in.
        var anyExplicitNegative = rows.Any(r => r.HadExplicitMinus);
        foreach (var row in unresolved)
        {
            if (anyExplicitNegative)
            {
                row.Sign = 1;
                continue;
            }

            row.Sign = InflowWords().IsMatch(string.Join(' ', row.DescriptionParts)) ? 1 : -1;
            row.SignFromKeyword = true;
        }

        if (unresolved.Any(r => r.SignFromKeyword))
        {
            warnings.Add("Some amounts had no debit/credit marker; their direction was inferred from the description.");
        }
    }

    private static int DetectBalanceDirection(List<RawRow> rows, decimal? openingBalance, AccountType accountType)
    {
        int forward = 0, backward = 0;
        var previous = openingBalance;
        foreach (var row in rows)
        {
            if (row is { Balance: not null, Value: not null, Sign: not null } && previous is not null)
            {
                var signed = row.Value.Value * row.Sign.Value;
                var delta = row.Balance.Value - previous.Value;
                if (Math.Abs(delta - signed) < BalanceTolerance)
                {
                    forward++;
                }
                else if (Math.Abs(delta + signed) < BalanceTolerance)
                {
                    backward++;
                }
            }

            if (row.Balance is not null)
            {
                previous = row.Balance;
            }
        }

        if (forward == backward)
        {
            return accountType is AccountType.CreditCard or AccountType.LineOfCredit ? -1 : 1;
        }

        return forward > backward ? 1 : -1;
    }

    private static DateOrder DetectDateOrder(List<RawRow> rows, PartialDate? periodStart, PartialDate? periodEnd, string currency)
    {
        var numeric = rows.Select(r => r.Date)
            .Concat(rows.Where(r => r.PostDate is not null).Select(r => r.PostDate!.Value))
            .Concat(new[] { periodStart, periodEnd }.Where(d => d is not null).Select(d => d!.Value))
            .Where(d => d.IsNumeric)
            .ToList();

        if (numeric.Any(d => d.First > 12))
        {
            return DateOrder.DayFirst;
        }

        if (numeric.Any(d => d.Second > 12))
        {
            return DateOrder.MonthFirst;
        }

        return currency is "GBP" or "EUR" ? DateOrder.DayFirst : DateOrder.MonthFirst;
    }

    private static bool Reconcile(List<ParsedTransaction> transactions, decimal? opening, decimal? closing, List<string> warnings)
    {
        if (opening is null || closing is null || transactions.Count == 0)
        {
            return false;
        }

        var sum = transactions.Sum(t => t.Amount);
        var bankStyle = Math.Abs(opening.Value + sum - closing.Value) < 0.02m;
        var cardStyle = Math.Abs(opening.Value - sum - closing.Value) < 0.02m;

        if (bankStyle || cardStyle)
        {
            return true;
        }

        warnings.Add("Extracted transactions do not add up to the statement's closing balance. Some rows may be missing or misread.");
        return false;
    }

    private static double Distance(Token token, Column column) =>
        Math.Min(Math.Abs(token.Right - column.Right), Math.Min(Math.Abs(token.Center - column.Center), Math.Abs(token.Left - column.Left)));

    private static decimal Signed(ParsedMoney money) =>
        money.Sign is AmountSignHint.Negative or AmountSignHint.Debit ? -money.Value : money.Value;
}
