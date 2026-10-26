using LocalAudiobookStudio.WorkerHost.Pipe;

namespace LocalAudiobookStudio.WorkerHost.Engine;

public static class VoxCpmOutputPolicy
{
    public static void EnsureBelowDecodeLimit(int steps, long sampleCount)
    {
        if (steps is < 1 or > 200 || sampleCount <= 0) throw new WorkerException("worker.arguments");
        // Pinned VoxCPM2: each decode step adds patch_size=4 latent frames;
        // decoder rates 8*6*5*2*2*2 give 1920 mono 48 kHz samples/frame.
        // At the ceiling the stop predictor may have finished, but completion
        // cannot be distinguished from exhausted steps. Below it is no quality proof.
        if (sampleCount >= (long)steps * 7680) throw new WorkerException("worker.output.limit");
    }
}
