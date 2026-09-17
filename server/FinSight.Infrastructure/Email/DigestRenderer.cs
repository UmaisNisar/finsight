using System.Globalization;
using System.Net;
using System.Text;

namespace FinSight.Infrastructure.Email;

public sealed record RenderedEmail(string Subject, string Html, string Text);

/// <summary>
/// Renders a monthly summary as HTML (table layout, inline styles, no images, no tracking, dark mode where the client
/// supports it) with a plain-text alternative. Every value that came from data is HTML-encoded.
/// </summary>
public static class DigestRenderer
{
    public const string AmountsOnlyNote = "Amounts only; open FinSight for details.";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static RenderedEmail Render(MonthlyDigest digest, Uri appUrl, string unsubscribeUrl, bool isTest)
    {
        var month = digest.Month.Label();
        var subject = (isTest ? "[Test] " : "") + $"Your FinSight summary for {month}";
        return new RenderedEmail(subject, Html(digest, month, appUrl, unsubscribeUrl, isTest), Text(digest, month, appUrl, unsubscribeUrl, isTest));
    }

    public static string Money(decimal amount, string currency)
    {
        var symbol = currency switch
        {
            "CAD" or "USD" or "AUD" => "$",
            "EUR" => "€",
            "GBP" => "£",
            "PKR" => "Rs ",
            _ => currency + " ",
        };
        var formatted = symbol + Math.Abs(amount).ToString("#,##0.00", Invariant);
        return amount < 0 ? "−" + formatted : formatted;
    }

    private static string Percent(decimal value) => value.ToString("0.#", Invariant) + "%";

    private static string Change(decimal? change) => change switch
    {
        null => "new",
        > 0 => $"up {Percent(change.Value)}",
        < 0 => $"down {Percent(-change.Value)}",
        _ => "no change",
    };

    private static List<string> Waiting(MonthlyDigest digest)
    {
        var lines = new List<string>();
        if (digest.AwaitingUpload > 0)
        {
            lines.Add($"{digest.AwaitingUpload} statement{(digest.AwaitingUpload == 1 ? " is" : "s are")} ready to download from your bank");
        }

        if (digest.ReadyToImport > 0)
        {
            lines.Add($"{digest.ReadyToImport} statement{(digest.ReadyToImport == 1 ? " was" : "s were")} found in Gmail and not imported yet");
        }

        return lines;
    }

    private static string Text(MonthlyDigest digest, string month, Uri appUrl, string unsubscribeUrl, bool isTest)
    {
        var c = digest.Currency;
        var text = new StringBuilder();
        if (isTest)
        {
            text.AppendLine("This is a test of your monthly summary email.").AppendLine();
        }

        text.AppendLine($"FinSight summary for {month}").AppendLine();

        if (!digest.HasData)
        {
            text.AppendLine($"There are no transactions for {month} yet. Once its statements are imported, your summary will appear here.").AppendLine();
        }
        else
        {
            text.AppendLine($"Income: {Money(digest.Income, c)}");
            text.AppendLine($"Spending: {Money(digest.Spending, c)}");
            text.AppendLine($"Net: {Money(digest.Net, c)}");
            text.AppendLine($"Savings rate: {(digest.SavingsRate is { } rate ? Percent(rate) : "not available")}").AppendLine();

            if (digest.AiSummary is not null)
            {
                text.AppendLine(digest.AiSummary).AppendLine();
            }

            if (digest.TopCategories.Count > 0)
            {
                text.AppendLine("Top spending");
                foreach (var category in digest.TopCategories)
                {
                    text.AppendLine($"- {category.Name}: {Money(category.Amount, c)} ({Change(category.ChangePercent)} vs the month before)");
                }

                text.AppendLine();
            }

            foreach (var (title, items) in new[] { ("New recurring payments", digest.NewRecurring), ("Recurring payments that stopped", digest.EndedRecurring) })
            {
                if (items.Count > 0)
                {
                    text.AppendLine(title);
                    foreach (var item in items)
                    {
                        text.AppendLine($"- {item.Merchant}: {Money(item.MonthlyAmount, c)} a month");
                    }

                    text.AppendLine();
                }
            }

            if (digest.BiggestAnomaly is { } anomaly)
            {
                text.AppendLine($"Worth a look: {digest.AnomalyCount} unusual transaction{(digest.AnomalyCount == 1 ? "" : "s")}. " +
                    $"The largest was {Money(anomaly.Amount, c)} at {anomaly.Merchant} on {anomaly.Date.ToString("MMM d", Invariant)}.").AppendLine();
            }
        }

        foreach (var line in Waiting(digest))
        {
            text.AppendLine($"- {line}");
        }

        if (Waiting(digest).Count > 0)
        {
            text.AppendLine();
        }

        text.AppendLine(AmountsOnlyNote);
        text.AppendLine($"Open FinSight: {appUrl.AbsoluteUri}").AppendLine();
        text.AppendLine("You're getting this because monthly summary emails are on in FinSight Settings.");
        text.AppendLine($"Turn them off: {unsubscribeUrl}");
        return text.ToString();
    }

    private static string E(string value) => WebUtility.HtmlEncode(value);

    private const string Font = "font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;";

    private static string Html(MonthlyDigest digest, string month, Uri appUrl, string unsubscribeUrl, bool isTest)
    {
        var c = digest.Currency;
        var html = new StringBuilder();
        html.Append($$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="color-scheme" content="light dark">
            <meta name="supported-color-schemes" content="light dark">
            <title>{{E($"FinSight summary for {month}")}}</title>
            <style>
            @media (prefers-color-scheme: dark) {
              .fs-bg { background-color:#000000 !important; }
              .fs-card { background-color:#1c1c1e !important; }
              .fs-text { color:#f5f5f7 !important; }
              .fs-muted { color:#98989d !important; }
              .fs-rule { border-color:#38383a !important; }
              .fs-button { background-color:#0a84ff !important; }
            }
            </style>
            </head>
            <body class="fs-bg" style="margin:0;padding:0;background-color:#f5f5f7;">
            <table role="presentation" class="fs-bg" width="100%" cellpadding="0" cellspacing="0" border="0" style="background-color:#f5f5f7;">
            <tr><td align="center" style="padding:32px 16px;">
            <table role="presentation" class="fs-card" width="100%" cellpadding="0" cellspacing="0" border="0" style="max-width:520px;background-color:#ffffff;border-radius:18px;">
            <tr><td style="padding:28px 28px 8px 28px;{{Font}}">
            """);

        if (isTest)
        {
            html.Append($"""<p class="fs-muted" style="margin:0 0 12px 0;font-size:13px;color:#6e6e73;{Font}">This is a test of your monthly summary email.</p>""");
        }

        html.Append($"""
            <p class="fs-muted" style="margin:0;font-size:13px;letter-spacing:0.02em;text-transform:uppercase;color:#6e6e73;{Font}">FinSight</p>
            <h1 class="fs-text" style="margin:4px 0 20px 0;font-size:24px;line-height:30px;font-weight:600;color:#1d1d1f;{Font}">{E(month)}</h1>
            </td></tr>
            """);

        if (!digest.HasData)
        {
            Paragraph(html, $"There are no transactions for {month} yet. Once its statements are imported, your summary will appear here.");
        }
        else
        {
            html.Append("""<tr><td style="padding:0 28px;"><table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0">""");
            Metric(html, "Income", Money(digest.Income, c));
            Metric(html, "Spending", Money(digest.Spending, c));
            Metric(html, "Net", Money(digest.Net, c));
            Metric(html, "Savings rate", digest.SavingsRate is { } rate ? Percent(rate) : "—", last: true);
            html.Append("</table></td></tr>");

            if (digest.AiSummary is not null)
            {
                Paragraph(html, digest.AiSummary, top: 20);
            }

            if (digest.TopCategories.Count > 0)
            {
                Section(html, "Top spending", digest.TopCategories.Select(cat => (cat.Name, Money(cat.Amount, c), $"{Change(cat.ChangePercent)} vs the month before")));
            }

            if (digest.NewRecurring.Count > 0)
            {
                Section(html, "New recurring payments", digest.NewRecurring.Select(r => (r.Merchant, Money(r.MonthlyAmount, c), "a month")));
            }

            if (digest.EndedRecurring.Count > 0)
            {
                Section(html, "Recurring payments that stopped", digest.EndedRecurring.Select(r => (r.Merchant, Money(r.MonthlyAmount, c), "a month")));
            }

            if (digest.BiggestAnomaly is { } anomaly)
            {
                Heading(html, "Worth a look");
                Paragraph(html, $"{digest.AnomalyCount} unusual transaction{(digest.AnomalyCount == 1 ? "" : "s")}. " +
                    $"The largest was {Money(anomaly.Amount, c)} at {anomaly.Merchant} on {anomaly.Date.ToString("MMM d", Invariant)}.");
            }
        }

        var waiting = Waiting(digest);
        if (waiting.Count > 0)
        {
            Heading(html, "Statements waiting");
            foreach (var line in waiting)
            {
                Paragraph(html, line + ".");
            }
        }

        html.Append($"""
            <tr><td style="padding:24px 28px 8px 28px;{Font}">
            <table role="presentation" cellpadding="0" cellspacing="0" border="0"><tr>
            <td class="fs-button" style="background-color:#0071e3;border-radius:980px;">
            <a href="{E(appUrl.AbsoluteUri)}" style="display:inline-block;padding:11px 22px;font-size:15px;font-weight:600;color:#ffffff;text-decoration:none;{Font}">Open FinSight</a>
            </td></tr></table>
            </td></tr>
            <tr><td style="padding:12px 28px 28px 28px;{Font}">
            <p class="fs-muted" style="margin:0;font-size:13px;line-height:18px;color:#6e6e73;{Font}">{E(AmountsOnlyNote)}</p>
            </td></tr>
            </table>
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="max-width:520px;">
            <tr><td style="padding:16px 28px;{Font}">
            <p class="fs-muted" style="margin:0;font-size:12px;line-height:17px;color:#86868b;{Font}">You're getting this because monthly summary emails are on in FinSight Settings. <a href="{E(unsubscribeUrl)}" style="color:#86868b;text-decoration:underline;">Turn off these emails</a></p>
            </td></tr>
            </table>
            </td></tr>
            </table>
            </body>
            </html>
            """);
        return html.ToString();
    }

    private static void Metric(StringBuilder html, string label, string value, bool last = false)
    {
        var border = last ? "" : "border-bottom:1px solid #e5e5ea;";
        html.Append($"""
            <tr>
            <td class="fs-muted fs-rule" style="padding:10px 0;{border}font-size:15px;color:#6e6e73;{Font}">{E(label)}</td>
            <td class="fs-text fs-rule" align="right" style="padding:10px 0;{border}font-size:17px;font-weight:600;color:#1d1d1f;{Font}">{E(value)}</td>
            </tr>
            """);
    }

    private static void Heading(StringBuilder html, string title) =>
        html.Append($"""<tr><td style="padding:22px 28px 4px 28px;{Font}"><h2 class="fs-text" style="margin:0;font-size:15px;font-weight:600;color:#1d1d1f;{Font}">{E(title)}</h2></td></tr>""");

    private static void Paragraph(StringBuilder html, string text, int top = 4) =>
        html.Append($"""<tr><td style="padding:{top}px 28px 0 28px;{Font}"><p class="fs-text" style="margin:0;font-size:15px;line-height:21px;color:#1d1d1f;{Font}">{E(text)}</p></td></tr>""");

    private static void Section(StringBuilder html, string title, IEnumerable<(string Name, string Value, string Note)> rows)
    {
        Heading(html, title);
        html.Append("""<tr><td style="padding:0 28px;"><table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0">""");
        foreach (var (name, value, note) in rows)
        {
            html.Append($"""
                <tr>
                <td style="padding:6px 0;{Font}"><span class="fs-text" style="font-size:15px;color:#1d1d1f;">{E(name)}</span><br><span class="fs-muted" style="font-size:13px;color:#6e6e73;">{E(note)}</span></td>
                <td class="fs-text" align="right" valign="top" style="padding:6px 0;font-size:15px;color:#1d1d1f;{Font}">{E(value)}</td>
                </tr>
                """);
        }

        html.Append("</table></td></tr>");
    }
}
