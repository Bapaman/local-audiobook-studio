using System.Security.Cryptography;
using System.Text;
using LocalAudiobookStudio.Audio;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Core.Voices;

public sealed record ImportPromptRequest(string SourceWavPath, string Transcript, bool RightsConfirmed);

public sealed class VoiceProfileService
{
    public async Task<VoiceSnapshot> ImportPromptAsync(ProjectDatabase project, ImportPromptRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.RightsConfirmed) throw new ProjectException("voice.rights");
        if (string.IsNullOrWhiteSpace(request.Transcript)) throw new ProjectException("voice.transcript");
        await using var lease = await project.AcquireArtifactLeaseAsync(cancellationToken: cancellationToken);
        await lease.RefreshAsync(cancellationToken);
        return await ImportUnderLeaseAsync(project, lease, request, null, null, cancellationToken);
    }

    internal async Task<VoiceSnapshot> ImportUnderLeaseAsync(ProjectDatabase project, ProjectArtifactLease lease, ImportPromptRequest request,
        long? startMilliseconds, long? endMilliseconds, CancellationToken cancellationToken)
    {
        lease.ValidateOwner(project);
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.RightsConfirmed) throw new ProjectException("voice.rights");
        if (string.IsNullOrWhiteSpace(request.Transcript)) throw new ProjectException("voice.transcript");
        if (project.State.Voice != null) throw new ProjectException("voice.exists");
        string id = Guid.NewGuid().ToString("N");
        string relative = $"voice-snapshot/{id}.wav";
        string stagingRelative = relative + ".part";
        string staging = project.ResolveOwnedPath(stagingRelative), final = project.ResolveOwnedPath(relative);
        bool moved = false, accepted = false;
        await lease.RegisterPendingArtifactAsync(stagingRelative, relative, cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
            string copiedHash;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = new FileStream(request.SourceWavPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(project.ResolveOwnedPath(stagingRelative), FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                byte[] buffer = new byte[65536]; int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
                { hash.AppendData(buffer, 0, count); await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken); }
                await destination.FlushAsync(cancellationToken); destination.Flush(true);
                copiedHash = Convert.ToHexString(hash.GetHashAndReset());
            }
            var inspection = await WavInspector.InspectAsync(project.ResolveOwnedPath(stagingRelative), cancellationToken);
            if (inspection.Sha256 != copiedHash) throw new ProjectException("voice.copy_changed");
            if (inspection.SampleCount < 144000) throw new ProjectException("voice.too_short");
            if (inspection.IsClipped) throw new ProjectException("voice.clipped");
            byte[] transcript = new UTF8Encoding(false, true).GetBytes(request.Transcript);
            string transcriptHash = Convert.ToHexString(SHA256.HashData(transcript));
            using var identity = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            identity.AppendData(Convert.FromHexString(copiedHash)); identity.AppendData(transcript);
            string identityHash = Convert.ToHexString(identity.GetHashAndReset());
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(project.ResolveOwnedPath(stagingRelative), project.ResolveOwnedPath(relative), false); moved = true;
            var voice = new VoiceSnapshot(id, relative, request.Transcript, copiedHash, transcriptHash, identityHash, true, DateTimeOffset.UtcNow, startMilliseconds, endMilliseconds);
            await lease.SaveAsync(project.State with { Voice = voice }, cancellationToken); accepted = true;
            return voice;
        }
        catch (WavInspectionException e) { throw new ProjectException(e.Code); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { throw new ProjectException("voice.io"); }
        finally
        {
            try
            {
                try { File.Delete(project.ResolveOwnedPath(stagingRelative)); }
                catch (DirectoryNotFoundException) { /* The import may fail before creating its directory. */ }
                if (moved && !accepted) File.Delete(project.ResolveOwnedPath(relative));
                await lease.RemoveStagingRegistrationAsync(stagingRelative, CancellationToken.None);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ProjectException("voice.io"); }
        }
    }
}
