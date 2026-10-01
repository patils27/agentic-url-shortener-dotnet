// Shared input validation helpers.
//
// Extracted into a dedicated module (brownfield refactor) so that URL/alias
// validation lives in one place and can be unit-tested independently of HTTP.

using System.Text.RegularExpressions;

namespace AgenticUrlShortener.Service;

public static class Validators
{
    private static readonly Regex AliasRegex =
        new(@"^[A-Za-z0-9_-]{3,32}$", RegexOptions.Compiled);

    /// <summary>Validate and normalize a destination URL. Throws <see cref="ArgumentException"/>.</summary>
    public static string ValidateUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("url must be a non-empty string");
        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host))
            throw new ArgumentException("url must be an absolute http(s) URL");
        return url;
    }

    /// <summary>Validate a custom alias. Throws <see cref="ArgumentException"/>.</summary>
    public static string ValidateAlias(string? alias)
    {
        if (string.IsNullOrEmpty(alias) || !AliasRegex.IsMatch(alias))
            throw new ArgumentException("custom_alias must be 3-32 chars of [A-Za-z0-9_-]");
        if (alias.Equals("health", StringComparison.OrdinalIgnoreCase) ||
            alias.Equals("ready", StringComparison.OrdinalIgnoreCase) ||
            alias.Equals("api", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("custom_alias is reserved for a service endpoint");
        return alias;
    }
}
