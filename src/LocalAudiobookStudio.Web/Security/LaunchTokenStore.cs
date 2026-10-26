using System.Security.Cryptography;

namespace LocalAudiobookStudio.Web.Security;

public sealed record LaunchTicket(string Plaintext, DateTimeOffset ExpiresUtc);

public sealed class LaunchTokenStore(TimeProvider timeProvider, LocalSessionStore sessions)
{
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromSeconds(60);
    private readonly Lock _gate = new();
    private readonly List<PendingLaunch> _pending = [];

    public LaunchTicket Issue(TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Launch ticket lifetime must be positive.");
        }

        var effectiveLifetime = lifetime <= MaximumLifetime ? lifetime : MaximumLifetime;
        var plaintext = SecureToken.Create(out var digest);
        var now = timeProvider.GetUtcNow();
        var expiresUtc = now + effectiveLifetime;
        lock (_gate)
        {
            RemoveExpired(now);
            _pending.Add(new PendingLaunch(digest, expiresUtc));
        }

        return new LaunchTicket(plaintext, expiresUtc);
    }

    public bool TryRedeem(string plaintext, out LocalSession session)
    {
        session = null!;
        if (!SecureToken.TryDigest(plaintext, out var candidate))
        {
            return false;
        }

        PendingLaunch? matched = null;
        try
        {
            var now = timeProvider.GetUtcNow();
            lock (_gate)
            {
                for (var index = _pending.Count - 1; index >= 0; index--)
                {
                    var pending = _pending[index];
                    if (matched is null
                        && CryptographicOperations.FixedTimeEquals(pending.Digest, candidate))
                    {
                        matched = pending;
                        _pending.RemoveAt(index);
                        continue;
                    }

                    if (pending.ExpiresUtc <= now)
                    {
                        CryptographicOperations.ZeroMemory(pending.Digest);
                        _pending.RemoveAt(index);
                    }
                }
            }

            if (matched is null || matched.ExpiresUtc <= now)
            {
                return false;
            }

            session = sessions.Issue();
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidate);
            if (matched is not null)
            {
                CryptographicOperations.ZeroMemory(matched.Digest);
            }
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        for (var index = _pending.Count - 1; index >= 0; index--)
        {
            var pending = _pending[index];
            if (pending.ExpiresUtc <= now)
            {
                CryptographicOperations.ZeroMemory(pending.Digest);
                _pending.RemoveAt(index);
            }
        }
    }

    private sealed record PendingLaunch(byte[] Digest, DateTimeOffset ExpiresUtc);
}
