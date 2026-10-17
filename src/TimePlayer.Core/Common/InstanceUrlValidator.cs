using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace TimePlayer.Core.Common;

/// <summary>
/// Security and privacy validator for public instance URLs.
/// Enforces HTTPS, rejects malformed, unsafe, or credential-bearing URLs.
/// </summary>
public static class InstanceUrlValidator
{
    public static bool AllowLocalhostForTesting { get; set; } = false;

    public static bool IsValidInstanceUri([NotNullWhen(true)] Uri? uri, [NotNullWhen(false)] out string? rejectionReason)
    {
        if (uri == null)
        {
            rejectionReason = "URI is null.";
            return false;
        }

        if (!uri.IsAbsoluteUri)
        {
            rejectionReason = "URI is not absolute.";
            return false;
        }

        // Scheme MUST be HTTPS
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            rejectionReason = $"Insecure scheme '{uri.Scheme}'. Only HTTPS is permitted.";
            return false;
        }

        // Must have non-empty host
        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            rejectionReason = "URI host is empty.";
            return false;
        }

        // Reject credentials in URL for privacy and security
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            rejectionReason = "URI contains user credentials.";
            return false;
        }

        // Reject localhost and loopback unless explicitly enabled for testing
        if (!AllowLocalhostForTesting)
        {
            if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                IPAddress.IsLoopback(IPAddress.TryParse(uri.Host, out var ip) ? ip : IPAddress.Any))
            {
                rejectionReason = "Loopback / localhost addresses are not permitted as public instances.";
                return false;
            }
        }

        rejectionReason = null;
        return true;
    }

    public static bool TrySanitizeBaseUri(string? input, [NotNullWhen(true)] out Uri? cleanUri, [NotNullWhen(false)] out string? error)
    {
        cleanUri = null;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "URL string is empty or whitespace.";
            return false;
        }

        var trimmed = input.Trim();

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
        {
            error = $"'{trimmed}' is not a valid absolute URI.";
            return false;
        }

        if (!IsValidInstanceUri(parsed, out error))
        {
            return false;
        }

        // Create normalized base URI: https://host:port without path or query
        var builder = new UriBuilder(parsed.Scheme, parsed.Host, parsed.Port);
        cleanUri = builder.Uri;
        return true;
    }
}
