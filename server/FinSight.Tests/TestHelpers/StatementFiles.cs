using System.Globalization;
using System.Text;

namespace FinSight.Tests.TestHelpers;

/// <summary>Synthetic CSV and OFX downloads. Account numbers, names and amounts are invented.</summary>
internal static class StatementFiles
{
    public sealed record OfxRow(string Posted, decimal Amount, string Name, string? FitId = null, string Type = "DEBIT", string? Memo = null);

    /// <summary>CIBC's credit card CSV: no header, <c>date,description,debit,credit,card number</c>.</summary>
    public static byte[] CibcCreditCardCsv(string card = "4500********5190", string? note = null) => Encoding.UTF8.GetBytes(
        $"""
        2026-08-03,LOBLAWS #221 TORONTO ON,84.10,,{card}
        2026-08-05,PAYMENT THANK YOU/PAIEMENT MERCI,,500.00,{card}
        2026-08-09,NETFLIX.COM{note},20.99,,{card}
        2026-08-12,SHELL C12345 TORONTO,61.25,,{card}

        """);

    /// <summary>OFX 1.x: SGML headers, and value elements without closing tags.</summary>
    public static byte[] Ofx1(string accountId, string accountType, IEnumerable<OfxRow> rows, string start = "20260801", string end = "20260831",
        decimal? ledger = null, string org = "TD Bank", bool creditCard = false)
    {
        var culture = CultureInfo.InvariantCulture;
        var builder = new StringBuilder();
        builder.Append("OFXHEADER:100\r\nDATA:OFXSGML\r\nVERSION:102\r\nSECURITY:NONE\r\nENCODING:USASCII\r\nCHARSET:1252\r\nCOMPRESSION:NONE\r\nOLDFILEUID:NONE\r\nNEWFILEUID:NONE\r\n\r\n");
        builder.Append("<OFX>\r\n<SIGNONMSGSRSV1>\r\n<SONRS>\r\n<STATUS>\r\n<CODE>0\r\n<SEVERITY>INFO\r\n</STATUS>\r\n<DTSERVER>20260901120000[-5:EST]\r\n<LANGUAGE>ENG\r\n");
        builder.Append(CultureInfo.InvariantCulture, $"<FI>\r\n<ORG>{org}\r\n<FID>1001\r\n</FI>\r\n</SONRS>\r\n</SIGNONMSGSRSV1>\r\n");
        builder.Append(creditCard ? "<CREDITCARDMSGSRSV1>\r\n<CCSTMTTRNRS>\r\n" : "<BANKMSGSRSV1>\r\n<STMTTRNRS>\r\n");
        builder.Append("<TRNUID>1\r\n<STATUS>\r\n<CODE>0\r\n<SEVERITY>INFO\r\n</STATUS>\r\n");
        builder.Append(creditCard ? "<CCSTMTRS>\r\n" : "<STMTRS>\r\n");
        builder.Append("<CURDEF>CAD\r\n");
        builder.Append(creditCard
            ? $"<CCACCTFROM>\r\n<ACCTID>{accountId}\r\n</CCACCTFROM>\r\n"
            : $"<BANKACCTFROM>\r\n<BANKID>000412345\r\n<ACCTID>{accountId}\r\n<ACCTTYPE>{accountType}\r\n</BANKACCTFROM>\r\n");
        builder.Append(CultureInfo.InvariantCulture, $"<BANKTRANLIST>\r\n<DTSTART>{start}000000[-5:EST]\r\n<DTEND>{end}235959[-5:EST]\r\n");
        foreach (var row in rows)
        {
            builder.Append(CultureInfo.InvariantCulture, $"<STMTTRN>\r\n<TRNTYPE>{row.Type}\r\n<DTPOSTED>{row.Posted}\r\n<TRNAMT>{row.Amount.ToString("0.00", culture)}\r\n");
            if (row.FitId is not null)
            {
                builder.Append(CultureInfo.InvariantCulture, $"<FITID>{row.FitId}\r\n");
            }

            builder.Append(CultureInfo.InvariantCulture, $"<NAME>{row.Name}\r\n");
            if (row.Memo is not null)
            {
                builder.Append(CultureInfo.InvariantCulture, $"<MEMO>{row.Memo}\r\n");
            }

            builder.Append("</STMTTRN>\r\n");
        }

        builder.Append("</BANKTRANLIST>\r\n");
        if (ledger is not null)
        {
            builder.Append(CultureInfo.InvariantCulture, $"<LEDGERBAL>\r\n<BALAMT>{ledger.Value.ToString("0.00", culture)}\r\n<DTASOF>{end}\r\n</LEDGERBAL>\r\n");
        }

        builder.Append(creditCard ? "</CCSTMTRS>\r\n</CCSTMTTRNRS>\r\n</CREDITCARDMSGSRSV1>\r\n" : "</STMTRS>\r\n</STMTTRNRS>\r\n</BANKMSGSRSV1>\r\n");
        builder.Append("</OFX>\r\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    /// <summary>OFX 2.x: an XML credit card statement.</summary>
    public static byte[] Ofx2CreditCard(string accountId, IEnumerable<OfxRow> rows, decimal ledger, bool quicken = false)
    {
        var culture = CultureInfo.InvariantCulture;
        var transactions = string.Concat(rows.Select(r =>
            $"<STMTTRN><TRNTYPE>{r.Type}</TRNTYPE><DTPOSTED>{r.Posted}</DTPOSTED><TRNAMT>{r.Amount.ToString("0.00", culture)}</TRNAMT>"
            + $"<FITID>{r.FitId}</FITID><NAME>{r.Name}</NAME>{(r.Memo is null ? "" : $"<MEMO>{r.Memo}</MEMO>")}</STMTTRN>\n"));

        return Encoding.UTF8.GetBytes($"""
            <?xml version="1.0" encoding="UTF-8" standalone="no"?>
            <?OFX OFXHEADER="200" VERSION="220" SECURITY="NONE" OLDFILEUID="NONE" NEWFILEUID="NONE"?>
            <OFX>
              <SIGNONMSGSRSV1>
                <SONRS>
                  <STATUS><CODE>0</CODE><SEVERITY>INFO</SEVERITY></STATUS>
                  <DTSERVER>20260901</DTSERVER>
                  <LANGUAGE>ENG</LANGUAGE>
                  <FI><ORG>CIBC</ORG><FID>10</FID></FI>
                  {(quicken ? "<INTU.BID>00001</INTU.BID>" : "")}
                </SONRS>
              </SIGNONMSGSRSV1>
              <CREDITCARDMSGSRSV1>
                <CCSTMTTRNRS>
                  <TRNUID>1</TRNUID>
                  <STATUS><CODE>0</CODE><SEVERITY>INFO</SEVERITY></STATUS>
                  <CCSTMTRS>
                    <CURDEF>CAD</CURDEF>
                    <CCACCTFROM><ACCTID>{accountId}</ACCTID></CCACCTFROM>
                    <BANKTRANLIST>
                      <DTSTART>20260801000000.000[-5:EST]</DTSTART>
                      <DTEND>20260831000000.000[-5:EST]</DTEND>
            {transactions}
                    </BANKTRANLIST>
                    <LEDGERBAL><BALAMT>{ledger.ToString("0.00", culture)}</BALAMT><DTASOF>20260831</DTASOF></LEDGERBAL>
                  </CCSTMTRS>
                </CCSTMTTRNRS>
              </CREDITCARDMSGSRSV1>
            </OFX>
            """);
    }
}
