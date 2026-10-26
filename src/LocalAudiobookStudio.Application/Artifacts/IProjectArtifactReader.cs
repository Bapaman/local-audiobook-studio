namespace LocalAudiobookStudio.Application.Artifacts;

public interface IProjectArtifactReader
{
    Task<ArtifactReadHandle> OpenAsync(string projectId, string artifactId, CancellationToken cancellationToken = default);
}

/// <summary>An accepted artifact with its actual file/ancestry lifetime guards. No storage path crosses this boundary.</summary>
public sealed class ArtifactReadHandle(Stream content, long length, string contentType, string downloadName) : IAsyncDisposable
{
    private IDisposable? pins;
    private int disposed;
    public Stream Content { get; } = content;
    public long Length { get; } = length;
    public string ContentType { get; } = contentType;
    public string DownloadName { get; } = downloadName;
    internal ArtifactReadHandle(Stream content, long length, string contentType, string downloadName, IDisposable pins)
        : this(content, length, contentType, downloadName) => this.pins = pins;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { await Content.DisposeAsync(); }
        finally { pins?.Dispose(); }
    }
}
