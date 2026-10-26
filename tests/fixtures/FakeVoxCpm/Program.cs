using System.Diagnostics;
using System.Text.Json;

// Test-only synthetic native stand-in. Never distributed by the product resolver.
if (args.Length > 0 && args[0] == "--grandchild")
{
    Console.WriteLine(Environment.ProcessId); Console.Out.Flush();
    await Task.Delay(Timeout.Infinite); return;
}
if (args.Length > 0 && args[0] == "--child")
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true };
    start.ArgumentList.Add("--grandchild");
    using var grandchild = Process.Start(start)!;
    string grandchildId = (await grandchild.StandardOutput.ReadLineAsync())!;
    Console.WriteLine($"{Environment.ProcessId},{grandchildId}"); Console.Out.Flush();
    await Task.Delay(Timeout.Infinite); return;
}
int textIndex = Array.IndexOf(args, "-t"), outputIndex = Array.IndexOf(args, "-o");
string mode = textIndex < 0 ? "argv" : args[textIndex + 1];
Console.WriteLine(JsonSerializer.Serialize(args)); Console.Out.Flush();
if (mode.StartsWith("tree", StringComparison.Ordinal))
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true };
    start.ArgumentList.Add("--child"); using var child = Process.Start(start)!;
    string ids = (await child.StandardOutput.ReadLineAsync())!;
    Console.WriteLine(ids); Console.Out.Flush();
    if (outputIndex >= 0) File.WriteAllText(args[outputIndex + 1] + ".pids", $"{Environment.ProcessId},{ids}");
    if (mode.StartsWith("tree-", StringComparison.Ordinal) && OperatingSystem.IsWindows())
        using (var ready = EventWaitHandle.OpenExisting(mode[5..])) ready.Set();
    await Task.Delay(Timeout.Infinite); return;
}
if (mode == "missing" || outputIndex < 0) return;
if (mode == "oom") { Console.Error.WriteLine("cudaMalloc failed: out of memory PRIVATE-NATIVE-SENTINEL"); Environment.ExitCode = 7; return; }
if (mode == "crash") { Environment.ExitCode = 9; return; }
if (mode == "flood")
{
    string line = new('x', 8192);
    await Task.WhenAll(Task.Run(() => { for (int i = 0; i < 256; i++) Console.WriteLine(line); }), Task.Run(() => { for (int i = 0; i < 256; i++) Console.Error.WriteLine(line); }));
}
string path = args[outputIndex + 1];
File.WriteAllText(path + ".argv", JsonSerializer.Serialize(args));
if (mode == "corrupt") { File.WriteAllText(path, "PRIVATE-NATIVE-SENTINEL"); return; }
using var output = File.Create(path); using var writer = new BinaryWriter(output);
writer.Write("RIFF"u8); writer.Write(40); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((ushort)1);
writer.Write((ushort)(mode == "stereo" ? 2 : 1)); writer.Write(mode == "rate" ? 44100 : 48000); writer.Write(96000);
writer.Write((ushort)2); writer.Write((ushort)16); writer.Write("data"u8); writer.Write(4);
writer.Write((short)(mode == "clipped" ? 32767 : 123)); writer.Write((short)-123);
