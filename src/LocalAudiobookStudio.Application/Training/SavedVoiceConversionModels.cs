using LocalAudiobookStudio.Core.Jobs;

namespace LocalAudiobookStudio.Application.Training;

public sealed record SavedVoiceConversionExecution(string ProjectRoot, string OutputRoot, SavedVoiceBinding Binding, string SourcePath, string SourceSha256);
public sealed record ConvertedVoiceCandidate(string Path, string Sha256, long Length, long SampleCount);
public interface ISavedVoiceConverter
{
    // Return only a fully validated candidate after confirmed owned native exit.
    Task<ConvertedVoiceCandidate> ConvertAsync(SavedVoiceConversionExecution execution, Action<string> progress, CancellationToken token);
}
