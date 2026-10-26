using LocalAudiobookStudio.WorkerHost.Engine;
using LocalAudiobookStudio.WorkerHost.Pipe;

namespace LocalAudiobookStudio.WorkerHost.Tests;

public class VoxCpmOutputPolicyTests
{
    [Theory]
    [InlineData(70, 537600)]
    [InlineData(70, 537601)]
    [InlineData(1, 7680)]
    [InlineData(200, 1536000)]
    public void Exact_or_exceeded_decode_budget_refuses_unconfirmed_completion(int steps, long samples)
    {
        var error = Assert.Throws<WorkerException>(() => VoxCpmOutputPolicy.EnsureBelowDecodeLimit(steps, samples));
        Assert.Equal("worker.output.limit", error.Code);
    }

    [Theory]
    [InlineData(70, 537599)]
    [InlineData(1, 7679)]
    [InlineData(200, 1535999)]
    [InlineData(1, 1)]
    public void Positive_output_below_decode_budget_is_not_refused_by_limit_policy(int steps, long samples)
    {
        VoxCpmOutputPolicy.EnsureBelowDecodeLimit(steps, samples);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(201, 1)]
    [InlineData(-1, 1)]
    [InlineData(70, 0)]
    [InlineData(70, -1)]
    public void Invalid_budget_or_sample_count_cannot_bypass_policy(int steps, long samples)
    {
        var error = Assert.Throws<WorkerException>(() => VoxCpmOutputPolicy.EnsureBelowDecodeLimit(steps, samples));
        Assert.Equal("worker.arguments", error.Code);
    }
}
