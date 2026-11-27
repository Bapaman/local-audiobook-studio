using System.Security.Cryptography;
using LocalAudiobookStudio.Audio;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Core.Voices;

public sealed record UploadVoiceRecording(string FileName, bool RightsConfirmed);
public sealed record ConfirmVoiceAnchor(long StartMilliseconds, long EndMilliseconds, string Transcript, bool PairListenedAndConfirmed);

public sealed class VoiceRecordingPolicy
{
    private const int SamplesPerMillisecond = 48;
    private const int MinimumRecordingMilliseconds = 5_000;
    private const int MinimumSelectableAnchorMilliseconds = 10_000;
    private const int MaximumAnchorMilliseconds = 20_000;

    public long MinimumSamples { get; }
    public long MaximumSamples { get; }
    public long MaximumBytes { get; }
    public VoiceRecordingPolicy(long minimumSamples = MinimumRecordingMilliseconds * SamplesPerMillisecond, long maximumSamples = 57600000, long maximumBytes = 1L << 30)
    {
        if (minimumSamples <= 0 || maximumSamples < minimumSamples || maximumSamples > 57600000 || maximumBytes <= 0 || maximumBytes > 1L << 30)
            throw new ArgumentOutOfRangeException(nameof(minimumSamples));
        MinimumSamples = minimumSamples; MaximumSamples = maximumSamples; MaximumBytes = maximumBytes;
    }
    public void Validate(WavInspection inspection)
    {
        if (inspection.Channels != 1 || inspection.SampleRate != 48000 || inspection.BitsPerSample != 16 || inspection.BlockAlignment != 2) throw new ProjectException("wav.format");
        if (inspection.SampleCount < MinimumSamples) throw new ProjectException("voice.recording.too_short");
        if (inspection.SampleCount > MaximumSamples) throw new ProjectException("voice.recording.too_long");
    }

    internal static bool IsAnchorRangeValid(long recordingSamples, long start, long end)
    {
        long duration = recordingSamples / SamplesPerMillisecond;
        if (start < 0 || end <= start || end > duration) return false;
        return duration < MinimumSelectableAnchorMilliseconds
            ? duration >= MinimumRecordingMilliseconds && start == 0 && end == duration
            : end - start is >= MinimumSelectableAnchorMilliseconds and <= MaximumAnchorMilliseconds;
    }
}

/// <summary>Accepts one immutable full recording, then one user-confirmed anchor derived from its samples.</summary>
public sealed class VoiceRecordingService(VoiceRecordingPolicy? policy = null)
{
    private readonly VoiceRecordingPolicy policy = policy ?? new();

    public Task<VoiceRecordingSnapshot> ImportAsync(ProjectDatabase project, UploadVoiceRecording command, Stream content, CancellationToken cancellationToken = default)
        => ImportAsync(project, command, content, null, cancellationToken);

    public async Task<VoiceRecordingSnapshot> ImportAsync(ProjectDatabase project, UploadVoiceRecording command, Stream content, long? expectedRevision, CancellationToken cancellationToken = default, bool requireNoActiveJob = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!requireNoActiveJob && !command.RightsConfirmed) throw new ProjectException("voice.rights");
        await using var lease = requireNoActiveJob
            ? await project.AcquireInputMutationLeaseAsync(cancellationToken)
            : await project.AcquireArtifactLeaseAsync(cancellationToken: cancellationToken);
        if (!command.RightsConfirmed) throw new ProjectException("voice.rights");
        await lease.RefreshAsync(cancellationToken); CheckRevision(project, expectedRevision);
        if (project.State.VoiceRecording != null) throw new ProjectException("voice.recording.exists");
        string id = Guid.NewGuid().ToString("N"), relative = $"voice-recording/{id}.wav";
        string part = relative + ".part"; bool moved = false, accepted = false;
        await lease.RegisterPendingArtifactAsync(part, relative, cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(project.ResolveOwnedPath(part))!);
            long length = 0; string copiedHash;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(project.ResolveOwnedPath(part), FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                byte[] buffer = new byte[65536]; int count;
                // Read at most one byte beyond the limit to detect a growing/nonseekable stream; never write that byte.
                while ((count = await content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, policy.MaximumBytes - length + 1)), cancellationToken)) > 0)
                {
                    if (count > policy.MaximumBytes - length) throw new ProjectException("voice.recording.too_large");
                    hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken); length += count;
                }
                await output.FlushAsync(cancellationToken); output.Flush(true); copiedHash = Convert.ToHexString(hash.GetHashAndReset());
            }
            var inspection = await WavInspector.InspectAsync(project.ResolveOwnedPath(part), cancellationToken);
            if (inspection.Sha256 != copiedHash) throw new ProjectException("voice.copy_changed");
            policy.Validate(inspection);
            // Recheck against durable state after streaming, under the same acceptance lease.
            await lease.RefreshAsync(cancellationToken); CheckRevision(project, expectedRevision);
            if (project.State.VoiceRecording != null) throw new ProjectException("voice.recording.exists");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(project.ResolveOwnedPath(part), project.ResolveOwnedPath(relative), false); moved = true;
            var recording = new VoiceRecordingSnapshot(id, relative, copiedHash, length, inspection.SampleCount, inspection.IsClipped, true, DateTimeOffset.UtcNow);
            await lease.SaveAsync(project.State with { VoiceRecording = recording }, cancellationToken); accepted = true;
            return recording;
        }
        catch (WavInspectionException e) { throw new ProjectException(e.Code); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { throw new ProjectException("voice.io"); }
        finally
        {
            try
            {
                DeleteIfPresent(project.ResolveOwnedPath(part));
                if (moved && !accepted) DeleteIfPresent(project.ResolveOwnedPath(relative));
                await lease.RemoveStagingRegistrationAsync(part, CancellationToken.None);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ProjectException("voice.io"); }
        }
    }

    public Task<VoiceSnapshot> ConfirmAnchorAsync(ProjectDatabase project, ConfirmVoiceAnchor command, CancellationToken cancellationToken = default)
        => ConfirmAnchorAsync(project, command, null, cancellationToken);

    public async Task<VoiceSnapshot> ConfirmAnchorAsync(ProjectDatabase project, ConfirmVoiceAnchor command, long? expectedRevision, CancellationToken cancellationToken = default, bool requireNoActiveJob = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!requireNoActiveJob)
        {
            if (!command.PairListenedAndConfirmed) throw new ProjectException("voice.anchor.confirmation");
            if (string.IsNullOrWhiteSpace(command.Transcript)) throw new ProjectException("voice.transcript");
        }
        await using var lease = requireNoActiveJob
            ? await project.AcquireInputMutationLeaseAsync(cancellationToken)
            : await project.AcquireArtifactLeaseAsync(cancellationToken: cancellationToken);
        if (!command.PairListenedAndConfirmed) throw new ProjectException("voice.anchor.confirmation");
        if (string.IsNullOrWhiteSpace(command.Transcript)) throw new ProjectException("voice.transcript");
        await lease.RefreshAsync(cancellationToken); CheckRevision(project, expectedRevision);
        if (project.State.Voice != null) throw new ProjectException("voice.exists");
        var recording = project.State.VoiceRecording ?? throw new ProjectException("voice.recording.missing");
        long start = command.StartMilliseconds, end = command.EndMilliseconds;
        if (!VoiceRecordingPolicy.IsAnchorRangeValid(recording.SampleCount, start, end)) throw new ProjectException("voice.anchor.range");
        string part = $"voice-anchor-staging/{Guid.NewGuid():N}.wav.part";
        await lease.RegisterPendingArtifactAsync(part, null, cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(project.ResolveOwnedPath(part))!);
            string source = project.ResolveOwnedPath(recording.WavRelativePath);
            await using var sourcePin = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            var inspection = await WavInspector.InspectAsync(source, cancellationToken);
            if (inspection.Sha256 != recording.Sha256 || sourcePin.Length != recording.ByteLength || inspection.SampleCount != recording.SampleCount) throw new ProjectException("voice.recording.changed");
            await PcmWavClipper.ExtractAsync(source, project.ResolveOwnedPath(part), TimeSpan.FromMilliseconds(start), TimeSpan.FromMilliseconds(end), cancellationToken);
            var selection = await WavInspector.InspectAsync(project.ResolveOwnedPath(part), cancellationToken);
            if (selection.IsClipped) throw new ProjectException("voice.clipped");
            await lease.RefreshAsync(cancellationToken); CheckRevision(project, expectedRevision);
            if (project.State.VoiceRecording != recording) throw new ProjectException("voice.recording.changed");
            return await new VoiceProfileService().ImportUnderLeaseAsync(project, lease, new(project.ResolveOwnedPath(part), command.Transcript, recording.RightsConfirmed), start, end, cancellationToken);
        }
        catch (WavInspectionException e) { throw new ProjectException(e.Code); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { throw new ProjectException("voice.io"); }
        finally
        {
            try { DeleteIfPresent(project.ResolveOwnedPath(part)); await lease.RemoveStagingRegistrationAsync(part, CancellationToken.None); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ProjectException("voice.io"); }
        }
    }

    private static void CheckRevision(ProjectDatabase project, long? revision)
    { if (revision.HasValue && project.State.Revision != revision) throw new ProjectException("project.conflict"); }
    private static void DeleteIfPresent(string path)
    { try { File.Delete(path); } catch (DirectoryNotFoundException) { } }
}
