using System.Text.Json;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.Training;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using LocalAudiobookStudio.Web.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace LocalAudiobookStudio.Web.Errors;

internal sealed class ApiException(int status, string code) : Exception
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public sealed class ProblemDetailsMapper(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); }
        catch (Exception) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); }
        catch (Exception error)
        {
            if (context.Response.HasStarted) { context.Abort(); return; }
            var (status, code) = Map(error);
            var extensions = new Dictionary<string, object?> { ["code"] = code };
            if (code == "project.conflict" && context.Request.RouteValues["id"] is string id)
            {
                try
                {
                    var current = await context.RequestServices.GetRequiredService<IStudioProjectService>().GetAsync(id, context.RequestAborted);
                    ApiRequest.ETag(context, current.Revision); extensions["currentSnapshot"] = current; extensions["conflictType"] = "project";
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); return; }
                catch { status = 404; code = "project.unavailable"; extensions["code"] = code; }
            }
            else if (code == "job.conflict" && context.Request.RouteValues["id"] is string projectId
                && context.Request.RouteValues["jobId"] is string jobId)
            {
                try
                {
                    var current = await context.RequestServices.GetRequiredService<IStudioJobService>().GetAsync(projectId, jobId, context.RequestAborted);
                    ApiRequest.ETag(context, current.Revision); extensions["currentSnapshot"] = current; extensions["conflictType"] = "job";
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); return; }
                catch { status = 404; code = "job.missing"; extensions["code"] = code; }
            }
            else if (code == "training.conflict" && context.Request.RouteValues["id"] is string trainingProject)
            {
                try
                {
                    var current = await context.RequestServices.GetRequiredService<IStudioTrainingService>().GetAsync(trainingProject, context.RequestAborted);
                    if (current == null) throw new StudioException("training.missing");
                    ApiRequest.ETag(context, current.Revision); extensions["currentSnapshot"] = current; extensions["conflictType"] = "training";
                }
                catch { status = 404; code = "training.missing"; extensions["code"] = code; }
            }
            else if (code == "tts_corpus.conflict" && context.Request.RouteValues["id"] is string corpusProject)
            {
                try
                {
                    var current = await context.RequestServices.GetRequiredService<IStudioTtsCorpusService>().GetAsync(corpusProject, context.RequestAborted);
                    if (current == null || context.Request.RouteValues["corpusId"] is string corpusId && current.Id != corpusId)
                        throw new StudioException("tts_corpus.missing");
                    ApiRequest.ETag(context, current.Revision); extensions["currentSnapshot"] = current; extensions["conflictType"] = "tts_corpus";
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); return; }
                catch (Exception) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); return; }
                catch { status = 404; code = "tts_corpus.missing"; extensions["code"] = code; }
            }
            else if (code == "voxcpm_training.conflict" && context.Request.RouteValues["id"] is string voxProject)
            {
                try
                {
                    var current = await context.RequestServices.GetRequiredService<IStudioVoxCpmTrainingService>().GetAsync(voxProject, context.RequestAborted);
                    if (current == null || context.Request.RouteValues["trainingId"] is string trainingId && current.Id != trainingId)
                        throw new StudioException("voxcpm_training.missing");
                    ApiRequest.ETag(context, current.Revision); extensions["currentSnapshot"] = current; extensions["conflictType"] = "voxcpm_training";
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); return; }
                catch (Exception) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); return; }
                catch { status = 404; code = "voxcpm_training.missing"; extensions["code"] = code; }
            }
            await Results.Problem(statusCode: status, title: "The request could not be completed.", extensions: extensions).ExecuteAsync(context);
        }
    }

    private static (int Status, string Code) Map(Exception error) => error switch
    {
        ApiException api => (api.Status, api.Code),
        BadHttpRequestException bad when bad.StatusCode == 413 => (413, "request.too_large"),
        BadHttpRequestException or JsonException or InvalidDataException => (400, "request.invalid"),
        StudioException studio => MapCode(studio.Code),
        _ => (409, "storage.unavailable")
    };

    private static (int, string) MapCode(string code) => code switch
    {
        "project.conflict" or "job.conflict" or "training.conflict" or "tts_corpus.conflict" => (412, code),
        "voxcpm_training.conflict" => (412, code),
        "voxcpm_training.missing" => (404, code),
        "voxcpm_training.runtime.not_ready" or "voxcpm_training.runtime.changed" => (503, code),
        "voxcpm_training.confirmation.required" => (403, code),
        "voxcpm_training.input.invalid" => (400, code),
        "voxcpm_training.active" or "voxcpm_training.transition.invalid" or "voxcpm_training.source.changed"
            or "voxcpm_training.corpus.invalid" or "voxcpm_training.corpus.changed" or "voxcpm_training.checkpoint.invalid"
            or "voxcpm_training.idempotency.conflict" or "voxcpm_training.idempotency.expired" or "voxcpm_training.state.invalid"
            or "voxcpm_training.state.limit" or "voxcpm_training.cache.changed" or "voxcpm_training.cache.busy" => (409, code),
        "voxcpm_training.busy" => (409, "storage.busy"),
        "tts_corpus.missing" or "tts_corpus.clip.missing" or "tts_corpus.source.missing" or "tts_corpus.plan.missing" => (404, code),
        "tts_corpus.runtime.not_ready" or "tts_corpus.runtime.changed" => (503, code),
        "tts_corpus.confirmation.required" => (403, code),
        "tts_corpus.input.invalid" or "tts_corpus.text.invalid" => (400, code),
        "tts_corpus.active" or "tts_corpus.transition.invalid" or "tts_corpus.source.changed" or "tts_corpus.payload.invalid"
            or "tts_corpus.audio.invalid" or "tts_corpus.idempotency.conflict" or "tts_corpus.idempotency.expired"
            or "tts_corpus.state.invalid" or "tts_corpus.state.limit" or "tts_corpus.empty" or "tts_corpus.review.required" => (409, code),
        "tts_corpus.busy" => (409, "storage.busy"),
        "training.missing" => (404, code),
        "training.runtime.not_ready" or "training.runtime.changed" => (503, code),
        "training.confirmation.required" => (403, code),
        "training.model.confirmation_required" => (403, code),
        "training.model.unavailable" or "training.model.source_changed" => (409, code),
        "training.input.invalid" or "training.path_too_long" => (400, code),
        "book.incomplete" or "artifact.invalid" => (409, code),
        "book.input_limit" or "book.byte_limit" => (413, code),
        "training.active" or "training.exists" or "training.transition.invalid" or "training.idempotency.conflict" or "training.source.changed" or "training.state.limit" => (409, code),
        "training.busy" => (409, "storage.busy"),
        "project.unavailable" or "artifact.missing" or "text.segment.missing" or "job.missing" => (404, code),
        "worker.runtime.not_ready" => (503, code),
        "voice.recording.too_large" or "text.too_large" => (413, code),
        "project.idempotency.conflict" or "voice.recording.exists" or "voice.exists" or "text.exists" or "text.reimport_required" or "job.active"
            or "job.idempotency.conflict" or "job.input_changed" or "job.transition.invalid" => (409, code),
        "voice.rights" => (403, code),
        "text.encoding.unsupported" => (415, code),
        "project.id.invalid" or "project.title.invalid" or "project.idempotency.invalid" or "text.display_name.invalid" or "job.input.invalid"
            or "text.binary" or "text.empty" or "text.encoding.invalid" or "text.grapheme_boundary" or "text.token_too_long"
            or "wav.empty" or "wav.format" or "wav.samples" or "wav.truncated"
            or "voice.recording.too_short" or "voice.recording.too_long" or "voice.recording.missing"
            or "voice.anchor.confirmation" or "voice.anchor.range" or "voice.transcript" or "voice.clipped" => (400, code),
        "catalog.busy" or "project.busy" => (409, "storage.busy"),
        _ => (409, "storage.unavailable")
    };
}
