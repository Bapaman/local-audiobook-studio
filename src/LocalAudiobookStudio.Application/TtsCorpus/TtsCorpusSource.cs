using System.Buffers.Binary;
using System.Security.Cryptography;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Audio;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Application.TtsCorpus;

/// <summary>Actual source identity and ancestry pins, retained through the owned operation.</summary>
internal sealed class TtsCorpusSource(FileStream content, CatalogPaths.Pins pins) : IDisposable
{
    public void Dispose() { try { content.Dispose(); } finally { pins.Dispose(); } }
    internal static async Task<TtsCorpusSource> VerifyAsync(ProjectDatabase db, VoiceRecordingSnapshot expected, CancellationToken token)
    {
        if (db.State.VoiceRecording != expected || !expected.RightsConfirmed) throw new StudioException("tts_corpus.source.changed");
        var pins = new CatalogPaths.Pins(); FileStream? content = null;
        try
        {
            string path = db.ResolveOwnedPath(expected.WavRelativePath); CatalogPaths.PinDirectories(Path.GetDirectoryName(path)!, pins);
            content = OpenRead(path); var wav = await WavInspector.InspectAsync(path, token);
            if (content.Length != expected.ByteLength || wav.Sha256 != expected.Sha256 || wav.SampleCount != expected.SampleCount)
                throw new StudioException("tts_corpus.source.changed");
            return new(content, pins);
        }
        catch (Exception e) when (e is IOException or WavInspectionException or UnauthorizedAccessException)
        { content?.Dispose(); pins.Dispose(); throw new StudioException("tts_corpus.source.changed"); }
        catch { content?.Dispose(); pins.Dispose(); throw; }
    }
    internal static FileStream OpenRead(string path)
    {
        var handle = CatalogPaths.OpenFile(path, 0x80000000, 1, 3);
        try { return new FileStream(handle, FileAccess.Read, 65536); } catch { handle.Dispose(); throw; }
    }
    internal static async Task VerifyHashAsync(ProjectDatabase db, string relative, string sha, int maximumBytes, CancellationToken token)
    {
        using var pins = new CatalogPaths.Pins(); string path = db.ResolveOwnedPath(relative);
        CatalogPaths.PinDirectories(Path.GetDirectoryName(path)!, pins); await using var file = OpenRead(path);
        if (file.Length <= 0 || file.Length > maximumBytes || !TtsCorpusRecordStore.SameHash(Convert.ToHexString(await SHA256.HashDataAsync(file, token)), sha))
            throw new StudioException("tts_corpus.payload.invalid");
    }
    /// <summary>Canonical PCM16 mono16k, at most sixteen seconds, parsed directly from the pinned stream.</summary>
    internal static async Task ValidateAudioAsync(Stream file, TtsCorpusPlannedClip clip, CancellationToken token)
    {
        try
        {
            long samples = clip.EndSample - clip.StartSample;
            if (samples is <= 0 or > 256000 || file.Length != 44 + samples * 2) throw new StudioException("tts_corpus.audio.invalid");
            file.Position = 0; byte[] h = new byte[44]; await file.ReadExactlyAsync(h, token);
            if (!h.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !h.AsSpan(8, 8).SequenceEqual("WAVEfmt "u8)
                || BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4)) != file.Length - 8 || BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(16)) != 16
                || BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(20)) != 1 || BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(22)) != 1
                || BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(24)) != 16000 || BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(28)) != 32000
                || BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(32)) != 2 || BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(34)) != 16
                || !h.AsSpan(36, 4).SequenceEqual("data"u8) || BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(40)) != samples * 2)
                throw new StudioException("tts_corpus.audio.invalid");
            file.Position = 0; string hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
            if (!TtsCorpusRecordStore.SameHash(hash, clip.AudioSha256)) throw new StudioException("tts_corpus.audio.invalid");
            file.Position = 0;
        }
        catch (EndOfStreamException) { throw new StudioException("tts_corpus.audio.invalid"); }
    }
}
