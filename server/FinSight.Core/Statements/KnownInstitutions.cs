using System.Text.RegularExpressions;

namespace FinSight.Core.Statements;

public sealed record Institution(string Name, string Pattern, params string[] SenderDomains)
{
    internal Regex Regex { get; } = new(Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Stable slug of the name, e.g. "td-bank" or "us-bank".</summary>
    public string Id { get; } = Slug(Name);

    /// <summary>
    /// The bank's official sign-in page (https). Hard-coded here and never taken from an email, so a statement
    /// alert can never lead the user to a phishing link. Null when we aren't sure of the official address.
    /// </summary>
    public string? SignInUrl { get; init; }

    /// <summary>One sentence on where to download statement PDFs once signed in.</summary>
    public string? DownloadHint { get; init; }

    private static string Slug(string name)
    {
        var letters = name.ToLowerInvariant().Where(c => c is not ('.' or '\''))
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-');
        return string.Join('-', new string(letters.ToArray()).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }
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
        new("TD Bank", @"\bTD (CANADA TRUST|BANK)\b|\bTORONTO-DOMINION\b|\bTD AEROPLAN\b|\bTD VISA\b", "td.com", "tdbank.com")
            { SignInUrl = "https://easyweb.td.com", DownloadHint = "Sign in to TD EasyWeb, open your account's statements, and download each statement as a PDF." },
        new("RBC Royal Bank", @"\bRBC\b|\bROYAL BANK OF CANADA\b", "rbc.com", "rbcroyalbank.com")
            { SignInUrl = "https://www.rbcroyalbank.com", DownloadHint = "Sign in to RBC Online Banking, open your account's statements, and download each statement as a PDF." },
        new("BMO", @"\bBMO\b|\bBANK OF MONTREAL\b", "bmo.com")
            { SignInUrl = "https://www.bmo.com", DownloadHint = "Sign in to BMO Online Banking, open your account's statements, and download each statement as a PDF." },
        new("Scotiabank", @"\bSCOTIABANK\b|\bBANK OF NOVA SCOTIA\b|\bSCOTIA\b", "scotiabank.com")
            { SignInUrl = "https://www.scotiabank.com", DownloadHint = "Sign in to Scotiabank online banking, open your account's statements, and download each statement as a PDF." },
        new("CIBC", @"\bCIBC\b|\bCANADIAN IMPERIAL BANK\b", "cibc.com")
            { SignInUrl = "https://www.cibconline.cibc.com", DownloadHint = "Sign in to CIBC Online Banking, open My documents, and download each month's statement as a PDF." },
        new("National Bank", @"\bNATIONAL BANK OF CANADA\b|\bBANQUE NATIONALE\b", "nbc.ca", "bnc.ca")
            { SignInUrl = "https://www.nbc.ca", DownloadHint = "Sign in to National Bank online banking, open your account's statements, and download each statement as a PDF." },
        new("Desjardins", @"\bDESJARDINS\b", "desjardins.com")
            { SignInUrl = "https://www.desjardins.com", DownloadHint = "Sign in to AccèsD, open your account's statements, and download each statement as a PDF." },
        new("Tangerine", @"\bTANGERINE\b", "tangerine.ca")
            { SignInUrl = "https://www.tangerine.ca", DownloadHint = "Sign in to Tangerine, open your account's eStatements, and download each statement as a PDF." },
        new("Simplii Financial", @"\bSIMPLII\b", "simplii.com")
            { SignInUrl = "https://www.simplii.com", DownloadHint = "Sign in to Simplii online banking, open your account's statements, and download each statement as a PDF." },
        new("EQ Bank", @"\bEQ BANK\b|\bEQUITABLE BANK\b", "eqbank.ca")
            { SignInUrl = "https://www.eqbank.ca", DownloadHint = "Sign in to EQ Bank, open your account's statements, and download each statement as a PDF." },
        new("Wealthsimple", @"\bWEALTHSIMPLE\b", "wealthsimple.com"),
        new("Koho", @"\bKOHO\b", "koho.ca"),
        new("Neo Financial", @"\bNEO FINANCIAL\b", "neofinancial.com"),
        new("PC Financial", @"\bPC FINANCIAL\b|\bPRESIDENT'?S CHOICE FINANCIAL\b", "pcfinancial.ca")
            { SignInUrl = "https://www.pcfinancial.ca", DownloadHint = "Sign in to PC Financial, open your account's statements, and download each statement as a PDF." },
        new("ATB Financial", @"\bATB FINANCIAL\b", "atb.com"),
        new("Rogers Bank", @"\bROGERS BANK\b", "rogersbank.com")
            { SignInUrl = "https://www.rogersbank.com", DownloadHint = "Sign in to Rogers Bank, open your card's statements, and download each statement as a PDF." },
        new("Canadian Tire Bank", @"\bCANADIAN TIRE BANK\b|\bTRIANGLE\b", "ctfs.com")
            { SignInUrl = "https://www.ctfs.com", DownloadHint = "Sign in to your Triangle account, open your card's statements, and download each statement as a PDF." },
        new("MBNA", @"\bMBNA\b", "mbna.ca")
            { SignInUrl = "https://www.mbna.ca", DownloadHint = "Sign in to MBNA online banking, open your card's statements, and download each statement as a PDF." },
        new("HSBC", @"\bHSBC\b", "hsbc.com", "hsbc.ca", "hsbc.co.uk"),
        // United States
        new("Chase", @"\bJPMORGAN CHASE\b|\bCHASE BANK\b|\bCHASE\b", "chase.com"),
        new("Bank of America", @"\bBANK OF AMERICA\b", "bankofamerica.com", "bofa.com"),
        new("Wells Fargo", @"\bWELLS FARGO\b", "wellsfargo.com"),
        new("Citi", @"\bCITIBANK\b|\bCITI\b", "citi.com", "citibank.com"),
        new("Capital One", @"\bCAPITAL ONE\b", "capitalone.com"),
        new("American Express", @"\bAMERICAN EXPRESS\b|\bAMEX\b", "americanexpress.com", "aexp.com")
            { SignInUrl = "https://www.americanexpress.com", DownloadHint = "Sign in to your American Express account, open Statements & Activity, and download each statement as a PDF." },
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

    public static Institution? FindByName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : All.FirstOrDefault(i => string.Equals(i.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Institution names compared loosely: case, spacing and punctuation are ignored ("C.I.B.C." equals "cibc").</summary>
    public static string NormalizeName(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

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
