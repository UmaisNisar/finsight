namespace FinSight.Core.Domain;

/// <summary>ISO 4217 currencies the app formats and analyzes. No FX conversion is performed.</summary>
public static class Currencies
{
    public static readonly IReadOnlyList<string> Supported = ["CAD", "USD", "EUR", "GBP"];

    public static bool IsSupported(string? code) =>
        code is not null && Supported.Contains(code.ToUpperInvariant());
}
