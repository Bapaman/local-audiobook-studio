using System.Security.Cryptography;
using System.Text;
using LocalAudiobookStudio.Application.Models;

namespace LocalAudiobookStudio.Application.Projects;

public sealed record ProjectCatalogOptions(string DataRoot)
{
    // Aggregate actual bytes copied from database, WAL and rollback journal.
    // Stage 1 metadata is bounded; accepted audio/artifacts are never copied.
    public long MaximumSnapshotBytes { get; init; } = 1024L * 1024 * 1024;
}
public sealed record CreateProjectCommand(string Title);
public sealed record CreateProjectResult(ProjectSnapshotDto Snapshot, bool Created);
public sealed record CatalogProject(string Id, DateTimeOffset LastOpenedUtc);
public sealed record CatalogCandidate(string Id, bool Pending);
public sealed record IdempotencyReceipt(string KeyHash, string RequestFingerprint, string ProjectId, bool Completed)
{
    public static IdempotencyReceipt Pending(string key, string canonicalTitle, string projectId)
        => new(Hash(key), Hash(canonicalTitle), projectId, false);
    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
