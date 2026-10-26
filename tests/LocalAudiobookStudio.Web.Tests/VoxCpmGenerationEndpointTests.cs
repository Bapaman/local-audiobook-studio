using System.Net;
using System.Net.Http.Json;
using LocalAudiobookStudio.Application.VoxCpmInference;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using Microsoft.Extensions.DependencyInjection;

namespace LocalAudiobookStudio.Web.Tests;

public sealed class VoxCpmGenerationEndpointTests
{
    [Fact]
    public async Task Inference_readiness_reports_actual_inference_provider()
    {
        await using var host = await ApiTestHost.StartAsync(configureServices: s => s.AddSingleton<IVoxCpmInferenceRuntime>(new InferenceRuntime()));
        using var response = await host.SendAsync(HttpMethod.Get, "/api/voxcpm-inference/readiness");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ApiTestHost.JsonAsync(response); Assert.True(json.GetProperty("ready").GetBoolean());
        Assert.Equal("voxcpm2-saved-adapter-cpu-v1", json.GetProperty("profileId").GetString());
    }
    private sealed class InferenceRuntime : IVoxCpmInferenceRuntime
    {
        public Task<RuntimeReadiness> GetReadinessAsync(CancellationToken token = default)
            => Task.FromResult(new RuntimeReadiness(true, true, null, "voxcpm2-saved-adapter-cpu-v1"));
        public Task<VoxCpmInferenceIdentity> GetIdentityAsync(CancellationToken token) => throw new InvalidOperationException("Readiness must not admit identity");
    }

    [Fact]
    public async Task Catalog_is_model_bound_and_does_not_admit_inference_runtime()
    {
        await using var host = await VoxCpmTrainingApiFixture.HostAsync();
        var project = await VoxCpmTrainingApiFixture.ApprovedAsync(host);
        var training = host.Services.GetRequiredService<IStudioVoxCpmTrainingService>();
        await training.StartAsync(project.Id, long.Parse(project.ETag.Trim('"')), "train-reference", project.VersionId, true, ApiTestHost.Token);
        Assert.True(await host.Services.GetRequiredService<VoxCpmTrainingRunner>().RunNextAsync(ApiTestHost.Token));
        var model = Assert.Single((await training.GetAsync(project.Id, ApiTestHost.Token))!.Models);
        await host.Services.GetRequiredService<IStudioTtsCorpusService>().StartAsync(project.Id, long.Parse(project.ETag.Trim('"')), "replace-draft", true, ApiTestHost.Token);
        using var response = await host.SendAsync(HttpMethod.Get, $"/api/projects/{project.Id}/voxcpm-generation/models/{model.Id}/references");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ApiTestHost.JsonAsync(response); Assert.Equal(model.Id, json.GetProperty("modelId").GetString());
        Assert.Equal(project.VersionId, json.GetProperty("corpusVersionId").GetString());
        var clip = Assert.Single(json.GetProperty("clips").EnumerateArray());
        Assert.Equal(10, clip.GetProperty("seconds").GetDouble());
        Assert.Equal(clip.GetProperty("id").GetString(), json.GetProperty("defaultClipId").GetString());
        Assert.DoesNotContain(host.Root, json.ToString());
        using var request = host.Request(HttpMethod.Get, $"/api/projects/{project.Id}/voxcpm-generation/models/{model.Id}/references/{clip.GetProperty("id").GetString()}/audio");
        request.Headers.Range = new(0, 43);
        using var audio = await host.Client.SendAsync(request, ApiTestHost.Token);
        Assert.Equal(HttpStatusCode.PartialContent, audio.StatusCode); Assert.Equal("audio/wav", audio.Content.Headers.ContentType!.MediaType);
        byte[] header = await audio.Content.ReadAsByteArrayAsync(ApiTestHost.Token);
        Assert.Equal(44, header.Length); Assert.Equal("RIFF"u8.ToArray(), header[..4]);
    }

    [Theory]
    [InlineData("{\"tempoPercent\":90}", "job.input.invalid")]
    [InlineData("{\"tempoPercent\":90,\"privatePath\":\"C:/private\"}", "request.invalid")]
    public async Task Http_options_reject_wrong_provider_or_unknown_nested_fields(string options, string code)
    {
        await using var host = await ApiTestHost.StartAsync(); var project = await host.CreateAsync();
        string body = "{\"kind\":\"preview\",\"segmentIds\":[\"" + new string('1', 32) + "\"],\"voxCpmOptions\":" + options + "}";
        using var response = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/jobs",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"), project.ETag, "wrong-provider");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, (await ApiTestHost.JsonAsync(response)).GetProperty("code").GetString());
    }
}
