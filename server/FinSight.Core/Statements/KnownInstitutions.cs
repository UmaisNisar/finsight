using System.Text.RegularExpressions;

namespace FinSight.Core.Statements;

public sealed record Institution(string Name, string Pattern, params string[] SenderDomains)
{
    internal Regex Regex { get; } = new(Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

/// <summary>
/// Institutions we can name. Used only to label statements and to strengthen email detection;
/// parsing never depends on the bank, and unlisted banks are handled the same way.
/// </summary>
public static class KnownInstitutions
{
    public static readonly IReadOnlyList<Institution> All =
    [
        // Canada
        new("TD Bank", @"\bTD (CANADA TRUST|BANK)\b|\bTORONTO-DOMINION\b|\bTD AEROPLAN\b|\bTD VISA\b", "td.com", "tdbank.com"),
        new("RBC Royal Bank", @"\bRBC\b|\bROYAL BANK OF CANADA\b", "rbc.com", "rbcroyalbank.com"),
        new("BMO", @"\bBMO\b|\bBANK OF MONTREAL\b", "bmo.com"),
        new("Scotiabank", @"\bSCOTIABANK\b|\bBANK OF NOVA SCOTIA\b|\bSCOTIA\b", "scotiabank.com"),
        new("CIBC", @"\bCIBC\b|\bCANADIAN IMPERIAL BANK\b", "cibc.com"),
        new("National Bank", @"\bNATIONAL BANK OF CANADA\b|\bBANQUE NATIONALE\b", "nbc.ca", "bnc.ca"),
        new("Desjardins", @"\bDESJARDINS\b", "desjardins.com"),
        new("Tangerine", @"\bTANGERINE\b", "tangerine.ca"),
        new("Simplii Financial", @"\bSIMPLII\b", "simplii.com"),
        new("EQ Bank", @"\bEQ BANK\b|\bEQUITABLE BANK\b", "eqbank.ca"),
        new("Wealthsimple", @"\bWEALTHSIMPLE\b", "wealthsimple.com"),
        new("Koho", @"\bKOHO\b", "koho.ca"),
        new("Neo Financial", @"\bNEO FINANCIAL\b", "neofinancial.com"),
        new("PC Financial", @"\bPC FINANCIAL\b|\bPRESIDENT'?S CHOICE FINANCIAL\b", "pcfinancial.ca"),
        new("ATB Financial", @"\bATB FINANCIAL\b", "atb.com"),
        new("Rogers Bank", @"\bROGERS BANK\b", "rogersbank.com"),
        new("Canadian Tire Bank", @"\bCANADIAN TIRE BANK\b|\bTRIANGLE\b", "ctfs.com"),
        new("MBNA", @"\bMBNA\b", "mbna.ca"),
        new("HSBC", @"\bHSBC\b", "hsbc.com", "hsbc.ca", "hsbc.co.uk"),
        // United States
        new("Chase", @"\bJPMORGAN CHASE\b|\bCHASE BANK\b|\bCHASE\b", "chase.com"),
        new("Bank of America", @"\bBANK OF AMERICA\b", "bankofamerica.com", "bofa.com"),
        new("Wells Fargo", @"\bWELLS FARGO\b", "wellsfargo.com"),
        new("Citi", @"\bCITIBANK\b|\bCITI\b", "citi.com", "citibank.com"),
        new("Capital One", @"\bCAPITAL ONE\b", "capitalone.com"),
        new("American Express", @"\bAMERICAN EXPRESS\b|\bAMEX\b", "americanexpress.com", "aexp.com"),
        new("Discover", @"\bDISCOVER (BANK|CARD|IT)\b", "discover.com"),
        new("U.S. Bank", @"\bU\.?S\.? BANK\b", "usbank.com"),
        new("PNC", @"\bPNC\b", "pnc.com"),
        new("Truist", @"\bTRUIST\b", "truist.com"),
        new("Ally Bank", @"\bALLY BANK\b", "ally.com"),
        new("Charles Schwab", @"\bSCHWAB\b", "schwab.com"),
        new("Navy Federal", @"\bNAVY FEDERAL\b", "navyfederal.org"),
        new("SoFi", @"\bSOFI\b", "sofi.com"),
        // United Kingdom and Europe
        new("Barclays", @"\bBARCLAYS\b", "barclays.co.uk", "barclays.com"),
        new("Lloyds Bank", @"\bLLOYDS\b", "lloydsbank.co.uk"),
        new("NatWest", @"\bNATWEST\b", "natwest.com"),
        new("Santander", @"\bSANTANDER\b", "santander.co.uk", "santander.com"),
        new("Nationwide", @"\bNATIONWIDE BUILDING SOCIETY\b", "nationwide.co.uk"),
        new("Halifax", @"\bHALIFAX\b", "halifax.co.uk"),
        new("Monzo", @"\bMONZO\b", "monzo.com"),
        new("Revolut", @"\bREVOLUT\b", "revolut.com"),
        new("Starling Bank", @"\bSTARLING BANK\b", "starlingbank.com"),
        new("N26", @"\bN26\b", "n26.com"),
        new("ING", @"\bING (BANK|DIRECT|-DIBA)\b", "ing.com", "ing.de", "ing.nl"),
        new("Deutsche Bank", @"\bDEUTSCHE BANK\b", "db.com"),
        new("BNP Paribas", @"\bBNP PARIBAS\b", "bnpparibas.com"),
        new("Wise", @"\bWISE PAYMENTS\b|\bTRANSFERWISE\b", "wise.com"),
    ];

    public static Institution? FindInText(string text) =>
        All.Select(i => (Institution: i, Match: i.Regex.Match(text)))
            .Where(x => x.Match.Success)
            .OrderBy(x => x.Match.Index)
            .Select(x => x.Institution)
            .FirstOrDefault();

    public static Institution? FindBySenderDomain(string senderAddress)
    {
        var at = senderAddress.LastIndexOf('@');
        if (at < 0)
        {
            return null;
        }

        var domain = senderAddress[(at + 1)..].Trim('>', ' ').ToLowerInvariant();
        return All.FirstOrDefault(i => i.SenderDomains.Any(d => domain == d || domain.EndsWith("." + d, StringComparison.Ordinal)));
    }
}
