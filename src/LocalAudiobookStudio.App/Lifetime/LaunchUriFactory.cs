using LocalAudiobookStudio.Web.Security;

namespace LocalAudiobookStudio.App.Lifetime;

public sealed class LaunchUriFactory
{
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(60);
    private readonly Uri _baseAddress;
    private readonly LaunchTokenStore _launches;

    public LaunchUriFactory(Uri baseAddress, LaunchTokenStore launches)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(launches);
        ValidateBaseAddress(baseAddress);
        _baseAddress = baseAddress;
        _launches = launches;
    }

    public Uri Create()
    {
        var ticket = _launches.Issue(TicketLifetime);
        var builder = new UriBuilder(_baseAddress)
        {
            Path = "/_session/launch",
            Query = $"token={Uri.EscapeDataString(ticket.Plaintext)}",
        };
        var result = builder.Uri;
        ValidateLaunchUri(result);
        return result;
    }

    internal static void ValidateLaunchUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!IsExactLoopbackAuthority(uri)
            || !string.Equals(uri.AbsolutePath, "/_session/launch", StringComparison.Ordinal)
            || uri.Query.Length <= "?token=".Length
            || !uri.Query.StartsWith("?token=", StringComparison.Ordinal)
            || uri.Query.Contains('&', StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("The launch URI is not an exact IPv4-loopback launch URL.", nameof(uri));
        }
    }

    private static void ValidateBaseAddress(Uri uri)
    {
        if (!IsExactLoopbackAuthority(uri)
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("The base address must be an exact IPv4-loopback HTTP origin.", nameof(uri));
        }
    }

    private static bool IsExactLoopbackAuthority(Uri uri) =>
        uri.IsAbsoluteUri
        && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
        && string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal)
        && uri.Port is > 0 and <= 65_535
        && string.IsNullOrEmpty(uri.UserInfo);
}
