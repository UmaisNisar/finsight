using Microsoft.AspNetCore.WebUtilities;

namespace FinSight.Api.Auth;

public static class ReturnUrls
{
    /// <summary>
    /// Only same-site relative paths, so the sign-in flow can't be used as an open redirect. Browsers ignore tabs and
    /// newlines in URLs and treat a backslash like a slash, so "/\t/evil.example" or "/\evil.example" would leave the site.
    /// </summary>
    /// <param name="fallback">Returned for missing or unsafe values.</param>
    public static string Safe(string? returnUrl, string fallback = "/")
    {
        if (string.IsNullOrEmpty(returnUrl) || returnUrl.Length > 2048 || returnUrl[0] != '/')
        {
            return fallback;
        }

        if (returnUrl.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == '\\'))
        {
            return fallback;
        }

        return returnUrl.StartsWith("//", StringComparison.Ordinal) ? fallback : returnUrl;
    }

    /// <summary>Appends one query parameter, with "?" or "&amp;" as the URL needs, before any fragment.</summary>
    public static string WithQuery(string url, string name, string value) => QueryHelpers.AddQueryString(url, name, value);
}
