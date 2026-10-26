using System.Security.Cryptography;

namespace LocalAudiobookStudio.Web.Security;

public sealed record LocalSession(string Plaintext);

public sealed class LocalSessionStore
{
    private readonly Lock _gate = new();
    private readonly List<byte[]> _sessionDigests = [];

    internal LocalSession Issue()
    {
        var token = SecureToken.Create(out var digest);
        lock (_gate)
        {
            _sessionDigests.Add(digest);
        }

        return new LocalSession(token);
    }

    public bool IsValid(string? plaintext)
    {
        if (!SecureToken.TryDigest(plaintext, out var candidate))
        {
            return false;
        }

        try
        {
            var valid = false;
            lock (_gate)
            {
                foreach (var digest in _sessionDigests)
                {
                    valid |= CryptographicOperations.FixedTimeEquals(digest, candidate);
                }
            }

            return valid;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidate);
        }
    }
}

internal static class SecureToken
{
    private const int TokenBytes = 32;
    private const int EncodedTokenLength = 43;

    public static string Create(out byte[] digest)
    {
        digest = [];
        Span<byte> bytes = stackalloc byte[TokenBytes];
        try
        {
            RandomNumberGenerator.Fill(bytes);
            var plaintext = Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            digest = SHA256.HashData(bytes);
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static bool TryDigest(string? plaintext, out byte[] digest)
    {
        digest = [];
        if (plaintext is null || plaintext.Length != EncodedTokenLength)
        {
            return false;
        }

        Span<byte> decoded = stackalloc byte[TokenBytes];
        try
        {
            if (!Convert.TryFromBase64String(ToPaddedBase64(plaintext), decoded, out var written)
                || written != TokenBytes)
            {
                return false;
            }

            digest = SHA256.HashData(decoded);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    private static string ToPaddedBase64(string plaintext) =>
        string.Concat(plaintext.Replace('-', '+').Replace('_', '/'), "=");
}
