using System.Text.Json;
using LocalAudiobookStudio.Web.Endpoints;
using LocalAudiobookStudio.Web.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace LocalAudiobookStudio.Web.Uploads;

internal sealed record MultipartUpload<T>(T Metadata, Stream Content);

internal static class MultipartUploadReader
{
    private const int MetadataLimit = 16 * 1024;
    private const int OverheadLimit = 1024 * 1024;

    internal static async Task<MultipartUpload<T>> ReadAsync<T>(HttpContext context, long fileLimit)
    {
        ApiRequest.Limit(context, checked(fileLimit + OverheadLimit));
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var media)
            || !media.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw new ApiException(415, "request.media_type");
        string boundary = HeaderUtilities.RemoveQuotes(media.Boundary).Value ?? "";
        if (boundary.Length is 0 or > 128 || boundary.Any(char.IsControl)) throw Invalid();
        var body = new BoundedReadStream(context.Request.Body, fileLimit + OverheadLimit);
        var reader = new MultipartReader(boundary, body)
        { HeadersCountLimit = 16, HeadersLengthLimit = 16 * 1024 };
        var metadata = await NextAsync(reader, context.RequestAborted) ?? throw Invalid();
        ValidatePart(metadata, "metadata", file: false);
        if (!MediaTypeHeaderValue.TryParse(metadata.ContentType, out var metadataType)
            || !metadataType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)) throw Invalid();
        T command;
        using (var boundedMetadata = new BoundedReadStream(metadata.Body, MetadataLimit))
        {
            command = await JsonSerializer.DeserializeAsync<T>(boundedMetadata, ApiRequest.JsonOptions, context.RequestAborted)
                ?? throw Invalid();
        }
        var section = await NextAsync(reader, context.RequestAborted) ?? throw Invalid();
        ValidatePart(section, "file", file: true);
        async Task ValidateTerminalAsync(CancellationToken token)
        {
            if (await NextAsync(reader, token) != null) throw Invalid();
            // Require completion of the HTTP body too; a disconnect/oversized epilogue
            // must fail while the importer still owns its unaccepted staging file.
            byte[] remainder = new byte[8192];
            while (await body.ReadAsync(remainder, token) != 0) { }
        }
        return new(command, new BoundedReadStream(section.Body, fileLimit, ValidateTerminalAsync));
    }

    private static async Task<MultipartSection?> NextAsync(MultipartReader reader, CancellationToken token)
    {
        try { return await reader.ReadNextSectionAsync(token); }
        catch (InvalidDataException) { throw Invalid(); }
    }

    private static void ValidatePart(MultipartSection section, string name, bool file)
    {
        if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
            || !disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase)
            || !HeaderUtilities.RemoveQuotes(disposition.Name).Equals(name, StringComparison.Ordinal)
            || (file ? !disposition.FileName.HasValue && !disposition.FileNameStar.HasValue
                : disposition.FileName.HasValue || disposition.FileNameStar.HasValue)) throw Invalid();
    }

    private static ApiException Invalid() => new(400, "request.multipart.invalid");
}

// Leave the HTTP/section stream open. Actual bytes, rather than advertised length,
// are authoritative. Terminal validation happens before EOF reaches a consumer.
internal sealed class BoundedReadStream(Stream inner, long limit, Func<CancellationToken, Task>? terminal = null) : Stream
{
    private long read;
    private bool complete;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0 || complete) return 0;
        int count;
        try { count = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, limit - read + 1)], cancellationToken); }
        catch (InvalidDataException) { throw new ApiException(400, "request.multipart.invalid"); }
        read += count;
        if (read > limit) throw new ApiException(413, "request.too_large");
        if (count == 0)
        {
            if (terminal != null) await terminal(cancellationToken);
            complete = true;
        }
        return count;
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
