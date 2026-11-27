using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Jobs;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LocalAudiobookStudio.Core.Storage;

/// <summary>Cross-process artifact ownership. Hold through registration, publication, acceptance and failure cleanup.</summary>
public sealed class ProjectArtifactLease : IAsyncDisposable
{
    private readonly ProjectDatabase project;
    private FileStream? ownership;
    private string? ownedClaimToken;
    private readonly Dictionary<string, GenerationPathPins> outputParents = new(StringComparer.Ordinal);
    internal ProjectArtifactLease(ProjectDatabase project, FileStream ownership) { this.project = project; this.ownership = ownership; }
    internal void ValidateOwner(ProjectDatabase expected)
    {
        if (ownership == null || !ReferenceEquals(project, expected)) throw new ProjectException("project.lease.invalid");
    }
    internal void RememberClaim(GenerationUnitClaim claim) => ownedClaimToken = claim.Token;
    internal void ValidateClaim(ProjectDatabase expected, GenerationUnitClaim claim)
    {
        ValidateOwner(expected);
        if (claim == null || ownedClaimToken == null || ownedClaimToken != claim.Token) throw new ProjectException("job.conflict");
    }
    internal void ReleaseClaim() => ownedClaimToken = null;
    public Task RefreshAsync(CancellationToken cancellationToken = default) => project.RefreshUnderLeaseAsync(this, cancellationToken);
    /// <summary>Reject accepted-input edits while any job is nonterminal; retain this lease through the edit.</summary>
    public Task RequireNoActiveJobsAsync(CancellationToken cancellationToken = default) => project.Jobs.RequireNoActiveJobsAsync(this, cancellationToken);
    public Task SaveAsync(ProjectState state, CancellationToken cancellationToken = default) => project.SaveUnderLeaseAsync(this, state, cancellationToken);
    public Task SetAudioReviewAsync(long expectedRevision, string segmentId, string artifactId, bool reviewed, CancellationToken cancellationToken = default)
        => project.SetAudioReviewAsync(this, expectedRevision, segmentId, artifactId, reviewed, cancellationToken);
    public Task SetAudioCorrectionAsync(long expectedRevision, string segmentId, string? note, AudioCorrectionBaseline expected,
        bool rejectCurrent, CancellationToken cancellationToken = default)
        => project.SetAudioCorrectionAsync(this, expectedRevision, segmentId, note, expected, rejectCurrent, cancellationToken);
    /// <summary>Append a separate immutable text while preserving all existing project inputs and history.</summary>
    public Task AppendTextAsync(long expectedRevision, ChapterRecord chapter, IReadOnlyList<SegmentRecord> segments, CancellationToken cancellationToken = default)
        => project.AppendTextUnderLeaseAsync(this, expectedRevision, chapter, segments, cancellationToken);
    public Task RegisterPendingArtifactAsync(string part, string? final, CancellationToken cancellationToken = default) => project.RegisterUnderLeaseAsync(this, part, final, cancellationToken);
    public Task RemoveStagingRegistrationAsync(string part, CancellationToken cancellationToken = default) => project.RemoveRegistrationUnderLeaseAsync(this, part, cancellationToken);
    /// <summary>Clean registered, unaccepted leaves only after the caller proves native tree exit.</summary>
    public Task CleanPendingArtifactsAsync(CancellationToken token = default) => project.CleanStagingAsync(this, token);
    public Task<GenerationUnitClaim?> BeginGenerationUnitAsync(string jobId, string segmentId, GenerationPreset effectivePreset, CancellationToken token)
        => project.BeginGenerationUnitAsync(this, jobId, segmentId, effectivePreset, token);
    public Task<JobRecord> AcceptGenerationUnitAsync(GenerationUnitClaim claim, AcceptedJobArtifact result, CancellationToken token)
        => project.AcceptGenerationUnitAsync(this, claim, result, token);
    public Task<JobRecord> ReportGenerationProgressAsync(GenerationUnitClaim claim, string phase, CancellationToken token)
        => project.ReportGenerationProgressAsync(this, claim, phase, token);
    public Task<JobRecord> ReportGenerationUnavailableAsync(string jobId, CancellationToken token)
        => project.ReportGenerationUnavailableAsync(this, jobId, token);
    public Task<JobRecord> InterruptGenerationUnitAsync(GenerationUnitClaim claim, string code, CancellationToken token)
        => project.EndGenerationUnitAsync(this, claim, code, true, GenerationFailureDisposition.Terminal, token);
    /// <summary>
    /// Fences the claim without touching pending files when owned tree exit is not yet confirmed.
    /// Caller MUST retain ownership/input pins and forbid inference reuse until actual exit.
    /// Registration is removed only by a later safe owned recovery/cleanup.
    /// </summary>
    public Task<JobRecord> InterruptGenerationUnitRetainingStagingAsync(GenerationUnitClaim claim, string code, CancellationToken token)
        => project.EndGenerationUnitAsync(this, claim, code, true, GenerationFailureDisposition.Terminal, token, retainStaging: true);
    public Task<JobRecord> FailGenerationUnitAsync(GenerationUnitClaim claim, string code, CancellationToken token)
        => FailGenerationUnitAsync(claim, code, GenerationFailureDisposition.Terminal, token);
    public Task<JobRecord> FailGenerationUnitAsync(GenerationUnitClaim claim, string code, GenerationFailureDisposition disposition, CancellationToken token)
        => project.EndGenerationUnitAsync(this, claim, code, false, disposition, token);
    internal void PinOutputParent(string jobId)
    {
        ValidateOwner(project);
        if (outputParents.ContainsKey(jobId)) return;
        var pins = new GenerationPathPins();
        try
        {
            pins.PinDirectories(project.Root);
            string segments = project.ResolveOwnedPath("segments"); Directory.CreateDirectory(segments); pins.PinDirectory(segments);
            string parent = project.ResolveOwnedPath("segments/" + jobId); Directory.CreateDirectory(parent); pins.PinDirectory(parent);
            outputParents.Add(jobId, pins);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { pins.Dispose(); throw new ProjectException("project.storage"); }
        catch { pins.Dispose(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        var stream = Interlocked.Exchange(ref ownership, null);
        ownedClaimToken = null;
        try { foreach (var pins in outputParents.Values) pins.Dispose(); outputParents.Clear(); }
        finally { if (stream != null) await stream.DisposeAsync(); }
    }
}

/// <summary>Native name/identity pins shared by current-open and fenced output acceptance.</summary>
internal sealed class GenerationPathPins : IDisposable
{
    private readonly List<SafeFileHandle> handles = [];
    internal void PinDirectories(string path)
    {
        var ancestors = new Stack<string>();
        for (string? current = path; current != null; current = Path.GetDirectoryName(current)) ancestors.Push(current);
        foreach (string current in ancestors) PinDirectory(current);
    }
    internal void PinDirectory(string path) => handles.Add(Open(path, true, 3));
    internal SafeFileHandle PinFile(string path, bool allowWrites)
    { var handle = Open(path, false, allowWrites ? 3u : 1u); handles.Add(handle); return handle; }
    internal bool PinFileIfPresent(string path, bool allowWrites)
    {
        try { PinFile(path, allowWrites); return true; }
        catch (ProjectException e) when (e.Code == "project.missing") { return false; }
    }
    internal void ReserveFile(string path) => handles.Add(Open(path, false, 3, 4));
    internal static SafeFileHandle OpenOwnership(string path) => Open(path, false, 0, 4, 0xc0000000, sharingIsBusy: true);
    private static SafeFileHandle Open(string path, bool directory, uint share, uint disposition = 3, uint? access = null, bool sharingIsBusy = false)
    {
        if (!OperatingSystem.IsWindows()) throw new ProjectException("project.platform.unsupported");
        string extended = path.StartsWith("\\\\?\\", StringComparison.Ordinal) ? path : "\\\\?\\" + path;
        var handle = CreateFileW(extended, access ?? (directory ? 1u : 0x80000000u), share, 0, disposition, directory ? 0x02200000u : 0x00200000u, 0);
        try
        {
            if (handle.IsInvalid) throw new ProjectException(Marshal.GetLastWin32Error() switch
            { 2 or 3 => "project.missing", 32 or 33 when sharingIsBusy => "project.busy", _ => "project.storage" });
            Check(handle, directory); return handle;
        }
        catch { handle.Dispose(); throw; }
    }
    internal static void Check(SafeFileHandle handle, bool directory = false)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new ProjectException("project.storage");
        if ((info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory || !directory && info.NumberOfLinks != 1)
            throw new ProjectException("project.path");
    }
    public void Dispose() { for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); handles.Clear(); }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
}
