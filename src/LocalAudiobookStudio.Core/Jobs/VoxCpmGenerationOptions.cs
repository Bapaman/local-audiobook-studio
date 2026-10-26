namespace LocalAudiobookStudio.Core.Jobs;

/// <summary>Requested immutable inference/export settings. Null clip ID retains automatic selection intent.</summary>
public sealed record VoxCpmGenerationOptions(long Seed = 42, int Timesteps = 10, double Cfg = 2,
    int TempoPercent = 100, bool UseReference = true, string? ReferenceClipId = null)
{
    public bool IsValid() => Seed is >= 0 and <= int.MaxValue && Timesteps is >= 4 and <= 20
        && double.IsFinite(Cfg) && Cfg is >= 1 and <= 3 && TempoPercent is >= 80 and <= 120
        && (ReferenceClipId == null || JobValidation.Id(ReferenceClipId)) && (UseReference || ReferenceClipId == null);
}
