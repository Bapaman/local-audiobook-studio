using System.Security.Cryptography;
using System.Text.Json;
using LocalAudiobookStudio.Application.Training;

namespace LocalAudiobookStudio.Application.Tests;

public sealed class WorkflowAcceptanceTests
{
    [Fact]
    public async Task Changed_checkpoint_payload_cannot_be_accepted_with_unchanged_manifest_and_receipt()
    {
        string root = Path.Combine(Path.GetTempPath(), "las-workflow-proof-" + Guid.NewGuid().ToString("N"));
        string output = Path.Combine(root, "training", "actions", new string('1', 32)), pointRoot = Path.Combine(output, "point"), stateRoot = Path.Combine(output, "state");
        Directory.CreateDirectory(pointRoot); Directory.CreateDirectory(stateRoot);
        try
        {
            var files = new Dictionary<string, object>();
            foreach (string name in new[] { "generator.pt", "discriminator.pt", "training.pt" })
            { byte[] payload = [1, 2, 3]; await File.WriteAllBytesAsync(Path.Combine(pointRoot, name), payload, TestContext.Current.CancellationToken); files[name] = new { bytes = 3, sha256 = Hash(payload) }; }
            string run = new string('2', 32), admission = new string('a', 64);
            string manifestHash = await WriteJson(Path.Combine(pointRoot, "manifest.json"), new { format = 2, purpose = "complete-update-checkpoint", identity = new { run_id = run },
                position = new { global_step = 1, completed_epoch = 0, epoch_size = 2, next_ordinal = 1, admission_sha256 = admission }, files });
            await File.WriteAllTextAsync(Path.Combine(pointRoot, "READY"), manifestHash + "\n", TestContext.Current.CancellationToken);
            string receiptPath = Path.Combine(output, "receipt.json");
            string receiptHash = await WriteJson(receiptPath, new { purpose = "supervisor-accepted-personal-native-update", native_worker_exit_code = 0,
                native_worker_timed_out = false, native_worker_deadline_ms = 60000, accepted_after_native_worker_exit = true, quality_established = false, auto_activate_voice = false,
                point = new { run_id = run, checkpoint_root = pointRoot, checkpoint_manifest_sha256 = manifestHash,
                    global_step = 1, completed_epoch = 0, epoch_size = 2, next_ordinal = 1, admission_sha256 = admission } });
            string source = Path.Combine(root, "recording.wav"), sourceHash = new string('b', 64), requestPath = Path.Combine(output, "request.json"), requestHash = new string('c', 64);
            string statePath = Path.Combine(stateRoot, "workflow_state.json");
            string stateHash = await WriteJson(statePath, new { format = 1, purpose = "local-personal-training-workflow-state", request = new { path = requestPath, sha256 = requestHash },
                source = new { path = source, sha256 = sourceHash, consent_confirmed = true }, run_id = run, inputs = new { admission = new { sha256 = admission } },
                accepted = new { checkpoint = new { root = pointRoot, sha256 = manifestHash }, receipt = new { path = receiptPath, sha256 = receiptHash } },
                global_step = 1, completed_epoch = 0, epoch_size = 2, stop_reason = "initial_update" });
            await File.WriteAllTextAsync(Path.Combine(stateRoot, "READY"), stateHash + "\n", TestContext.Current.CancellationToken);
            var execution = new TrainingExecution(root, output, source, sourceHash, null);
            var reference = new WorkflowReference(statePath, stateHash, 1, 0, 2, "initial_update");
            await RvcWorkflowExecutor.ValidateReferenceAsync(execution, reference, requestPath, requestHash, TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(pointRoot, "generator.pt"), [3, 2, 1], TestContext.Current.CancellationToken);
            var error = await Assert.ThrowsAsync<TrainingExecutionException>(() => RvcWorkflowExecutor.ValidateReferenceAsync(execution, reference, requestPath, requestHash, TestContext.Current.CancellationToken));
            Assert.Equal("training.control.invalid", error.Code);
        }
        finally { Directory.Delete(root, true); }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static async Task<string> WriteJson(string path, object value)
    { byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value); await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken); return Hash(bytes); }
}
