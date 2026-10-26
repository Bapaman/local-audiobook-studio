using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using static LocalAudiobookStudio.App.VoxCpmTraining.VoxCpmTrainingJson;

namespace LocalAudiobookStudio.App.VoxCpmTraining;

/// <summary>Bounded symbolic reader. No deserialization callbacks, imports or tensor runtime.</summary>
internal static class VoxCpmTrainingTensorPreflight
{
    private sealed record Global(string Module, string Name);
    private sealed record Storage(string Key, string Dtype, int Count)
    { internal int Bytes => checked(Count * (Dtype == "Float" ? 4 : Dtype == "Long" ? 8 : 1)); }
    private sealed record Tensor(Storage Storage, int[] Shape);
    private sealed record Tuple(object?[] Items);
    private sealed record Archive(object? Value, Dictionary<string, ReadOnlyMemory<byte>> Data);
    private static readonly string[] Services = ["data.pkl", ".format_version", ".storage_alignment", "byteorder", "version", ".data/serialization_id"];
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i =>
    { uint v = (uint)i; for (int k = 0; k < 8; k++) v = (v & 1) == 0 ? v >> 1 : (v >> 1) ^ 0xedb88320; return v; }).ToArray();

    internal static void Validate(string name, byte[] payload, JsonElement manifest)
    {
        try
        {
            Require(VoxCpmTrainingProtocol.PayloadLimits.TryGetValue(name, out int budget) && payload.Length is > 0 && payload.Length <= budget);
            var layout = VoxCpmTrainingProtocol.Layout(manifest.GetProperty("parameterLayout"));
            int step = (int)Integer(manifest.GetProperty("afterCursor").GetProperty("globalStep"), 1, 512);
            switch (name)
            {
                case "lora_weights.safetensors": Safetensors(payload, layout); break;
                case "optimizer.pth": Optimizer(ReadArchive(payload, budget), layout, step); break;
                case "scheduler.pth": Scheduler(ReadArchive(payload, budget), step); break;
                case "rng.pth": Rng(ReadArchive(payload, budget)); break;
                case "metadata.json":
                    using (var json = Parse(payload))
                    {
                        var m = json.RootElement; Fields(m, "format", "optimizerNames", "recipeSha256", "adapterLayoutSha256");
                        Integer(m.GetProperty("format"), 1, 1);
                        var names = m.GetProperty("optimizerNames"); Require(names.ValueKind == JsonValueKind.Array);
                        Require(names.EnumerateArray().Select(Text).SequenceEqual(layout.Select(p => p.Name), StringComparer.Ordinal));
                        foreach (string key in new[] { "recipeSha256", "adapterLayoutSha256" })
                            Require(Hash(m.GetProperty(key)) == Hash(manifest.GetProperty("identity").GetProperty(key)));
                    }
                    break;
                default: throw Invalid();
            }
        }
        catch (LocalAudiobookStudio.Application.VoxCpmTraining.VoxCpmTrainingExecutionException) { throw; }
        catch { throw Invalid(); }
    }
    private static void Safetensors(byte[] payload, IReadOnlyList<VoxCpmTrainingAdapterLayout> layout)
    {
        Require(payload.Length >= 8); ulong announced = BinaryPrimitives.ReadUInt64LittleEndian(payload);
        Require(announced is > 0 and <= 1048576 && announced <= (ulong)(payload.Length - 8)); int size = (int)announced;
        using var document = Parse(payload.AsSpan(8, size).ToArray()); var header = document.RootElement;
        Fields(header, layout.Select(e => e.Name).ToArray()); var ranges = new List<(int Begin, int End)>();
        foreach (var item in layout)
        {
            var tensor = header.GetProperty(item.Name); Fields(tensor, "dtype", "shape", "data_offsets");
            Require(Text(tensor.GetProperty("dtype")) == "F32"); var shape = tensor.GetProperty("shape");
            Require(shape.ValueKind == JsonValueKind.Array && shape.EnumerateArray().Select(n => (int)Integer(n, 1, 32768)).SequenceEqual(item.Shape));
            var offsets = tensor.GetProperty("data_offsets"); Require(offsets.ValueKind == JsonValueKind.Array && offsets.GetArrayLength() == 2);
            int begin = (int)Integer(offsets[0], 0, payload.Length), end = (int)Integer(offsets[1], 0, payload.Length);
            Require(end - begin == checked(item.Shape[0] * item.Shape[1] * 4) && end <= payload.Length - 8 - size);
            ranges.Add((begin, end)); Floats(payload.AsSpan(8 + size + begin, end - begin), false);
        }
        ranges.Sort(); Require(ranges[0].Begin == 0 && ranges[^1].End == payload.Length - 8 - size);
        for (int i = 1; i < ranges.Count; i++) Require(ranges[i - 1].End == ranges[i].Begin);
    }
    private static double Lr(int step)
    { double value = .0001; for (int i = 0; i < step / 100; i++) value *= .99; return value; }
    private static void Optimizer(Archive archive, IReadOnlyList<VoxCpmTrainingAdapterLayout> layout, int step)
    {
        var value = Dict(archive.Value, "state", "param_groups"); var groups = List(value["param_groups"]); Require(groups.Count == 1);
        var group = Dict(groups[0], "lr", "betas", "eps", "weight_decay", "amsgrad", "maximize", "foreach", "capturable", "differentiable", "fused", "decoupled_weight_decay", "initial_lr", "params");
        Exact(group["lr"], Lr(step)); Exact(group["eps"], 1e-8); Exact(group["weight_decay"], .01); Exact(group["initial_lr"], .0001);
        var betas = Tup(group["betas"], 2); Exact(betas[0], .9); Exact(betas[1], .999);
        foreach (string key in new[] { "amsgrad", "maximize", "foreach", "capturable", "differentiable" }) Exact(group[key], false);
        Require(group["fused"] == null); Exact(group["decoupled_weight_decay"], true);
        var parameters = List(group["params"]); Require(parameters.Count == layout.Count);
        for (int i = 0; i < parameters.Count; i++) Require(Int(parameters[i], i, i) == i);
        var states = Dict(value["state"]); Require(states.Count >= 1 && states.Count <= layout.Count);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in states)
        {
            int index = (int)Int(pair.Key, 0, layout.Count - 1); var item = Dict(pair.Value, "step", "exp_avg", "exp_avg_sq");
            var counter = TensorData(item["step"], archive, "Float", [], true);
            float number = BinaryPrimitives.ReadSingleLittleEndian(counter.Span);
            Require(number >= 1 && number <= step && number == MathF.Truncate(number));
            TensorData(item["exp_avg"], archive, "Float", layout[index].Shape);
            TensorData(item["exp_avg_sq"], archive, "Float", layout[index].Shape, true);
            foreach (object? tensor in item.Values) Require(tensor is Tensor t && used.Add(t.Storage.Key));
        }
        Require(used.SetEquals(archive.Data.Keys));
    }
    private static void Scheduler(Archive archive, int step)
    {
        Require(archive.Data.Count == 0);
        var v = Dict(archive.Value, "step_size", "gamma", "base_lrs", "last_epoch", "_step_count", "_is_initial", "_get_lr_called_within_step", "_last_lr");
        Int(v["step_size"], 100, 100); Exact(v["gamma"], .99); Int(v["last_epoch"], step, step); Int(v["_step_count"], step + 1, step + 1);
        Exact(v["_is_initial"], false); Exact(v["_get_lr_called_within_step"], false);
        foreach (var pair in new Dictionary<string, double> { ["base_lrs"] = .0001, ["_last_lr"] = Lr(step) })
        { var list = List(v[pair.Key]); Require(list.Count == 1); Exact(list[0], pair.Value); }
    }
    private static void Rng(Archive archive)
    {
        var v = Dict(archive.Value, "python", "torch", "numpy_keys", "numpy_kind", "numpy_pos", "numpy_has_gauss", "numpy_cached_gaussian");
        var py = Tup(v["python"], 3); Int(py[0], 3, 3); var state = Tup(py[1], 625);
        for (int i = 0; i < 624; i++) Int(state[i], 0, uint.MaxValue); Int(state[624], 0, 624);
        Require(py[2] == null || py[2] is double d && double.IsFinite(d));
        Require(v["numpy_kind"] is string kind && kind == "MT19937"); Int(v["numpy_pos"], 0, 624); Int(v["numpy_has_gauss"], 0, 1);
        Finite(v["numpy_cached_gaussian"]);
        TensorData(v["torch"], archive, "Byte", [5056]); TensorData(v["numpy_keys"], archive, "Long", [624]);
        Require(archive.Data.Count == 2 && ((Tensor)v["torch"]!).Storage.Key != ((Tensor)v["numpy_keys"]!).Storage.Key);
    }
    private static ReadOnlyMemory<byte> TensorData(object? value, Archive archive, string dtype, int[] shape, bool nonnegative = false)
    {
        Require(value is Tensor); var tensor = (Tensor)value;
        Require(tensor.Storage.Dtype == dtype && tensor.Shape.SequenceEqual(shape)); var data = archive.Data[tensor.Storage.Key];
        if (dtype == "Float") Floats(data.Span, nonnegative);
        else if (dtype == "Long")
            for (int i = 0; i < data.Length; i += 8) Require(BinaryPrimitives.ReadInt64LittleEndian(data.Span[i..]) is >= 0 and <= uint.MaxValue);
        return data;
    }
    private static void Floats(ReadOnlySpan<byte> data, bool nonnegative)
    {
        Require(data.Length % 4 == 0);
        for (int i = 0; i < data.Length; i += 4)
        { float v = BinaryPrimitives.ReadSingleLittleEndian(data[i..]); Require(float.IsFinite(v) && (!nonnegative || v >= 0)); }
    }
    private static Dictionary<object, object?> Dict(object? value, params string[] keys)
    {
        Require(value is Dictionary<object, object?>); var result = (Dictionary<object, object?>)value;
        if (keys.Length > 0) Require(result.Count == keys.Length && keys.All(k => result.ContainsKey(k)));
        return result;
    }
    private static List<object?> List(object? value) { Require(value is List<object?>); return (List<object?>)value; }
    private static object?[] Tup(object? value, int count) { Require(value is Tuple t && t.Items.Length == count); return ((Tuple)value).Items; }
    private static long Int(object? value, long minimum, long maximum)
    { Require(value is BigInteger n && n >= minimum && n <= maximum); return (long)(BigInteger)value; }
    private static void Exact(object? value, double expected) => Require(value is double d && d == expected);
    private static void Exact(object? value, bool expected) => Require(value is bool b && b == expected);
    private static void Finite(object? value)
    { Require(value is double or BigInteger); double v = value is double d ? d : (double)(BigInteger)value; Require(double.IsFinite(v) && v is >= -1e100 and <= 1e100); }

    private static Archive ReadArchive(byte[] payload, int budget)
    {
        var files = Zip(payload, budget);
        foreach (var pair in new Dictionary<string, byte[]> { [".format_version"] = "1"u8.ToArray(), [".storage_alignment"] = "64"u8.ToArray(), ["byteorder"] = "little"u8.ToArray(), ["version"] = "3\n"u8.ToArray() })
            Require(files.TryGetValue(pair.Key, out var data) && data.Span.SequenceEqual(pair.Value));
        var serial = files[".data/serialization_id"]; Require(serial.Length is >= 1 and <= 128 && serial.Span.IndexOfAnyExceptInRange((byte)'0', (byte)'9') < 0);
        var (value, storages) = Pickle(files["data.pkl"], budget);
        Require(files.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(Services.Concat(storages.Keys.Select(k => "data/" + k))));
        var storageData = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        foreach (var pair in storages)
        { var data = files["data/" + pair.Key]; Require(data.Length == pair.Value.Bytes); storageData.Add(pair.Key, data); }
        return new(value, storageData);
    }
    private static Dictionary<string, ReadOnlyMemory<byte>> Zip(byte[] payload, int budget)
    {
        Require(payload.Length >= 22 && payload.Length <= budget); int end = payload.Length - 22;
        Require(U32(payload, end) == 0x06054b50 && U16(payload, end + 4) == 0 && U16(payload, end + 6) == 0 && U16(payload, end + 20) == 0);
        int count = U16(payload, end + 10); Require(count is >= 6 and <= 2048 && U16(payload, end + 8) == count);
        int directory = checked((int)U32(payload, end + 16)), size = checked((int)U32(payload, end + 12)), footer = end;
        // torch writes an optional ZIP64 footer even when every classic field fits.
        if (end >= 20 && U32(payload, end - 20) == 0x07064b50)
        {
            int locator = end - 20; Require(U32(payload, locator + 4) == 0 && U32(payload, locator + 16) == 1);
            ulong recordOffset = U64(payload, locator + 8); Require(recordOffset <= (ulong)locator && locator - (long)recordOffset == 56);
            footer = (int)recordOffset; Require(U32(payload, footer) == 0x06064b50 && U64(payload, footer + 4) == 44
                && U32(payload, footer + 16) == 0 && U32(payload, footer + 20) == 0
                && U64(payload, footer + 24) == (ulong)count && U64(payload, footer + 32) == (ulong)count
                && U64(payload, footer + 40) == (ulong)size && U64(payload, footer + 48) == (ulong)directory);
        }
        Require(directory >= 0 && size >= 0 && (long)directory + size == footer);
        var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal); var ranges = new List<(int Begin, int End)>();
        int position = directory; long total = 0;
        for (int i = 0; i < count; i++)
        {
            Require(position + 46 <= footer && U32(payload, position) == 0x02014b50);
            int flags = U16(payload, position + 8); Require((flags & ~0x808) == 0 && U16(payload, position + 10) == 0 && U16(payload, position + 34) == 0);
            uint crc = U32(payload, position + 16), length32 = U32(payload, position + 24);
            Require(length32 == U32(payload, position + 20) && length32 <= budget); int length = (int)length32;
            int nameSize = U16(payload, position + 28), extra = U16(payload, position + 30), comment = U16(payload, position + 32);
            Require(nameSize is > 0 and <= 256 && (long)position + 46 + nameSize + extra + comment <= footer);
            var nameBytes = payload.AsSpan(position + 46, nameSize); Require(nameBytes.IndexOfAnyExceptInRange((byte)0x21, (byte)0x7e) < 0);
            string name = Encoding.ASCII.GetString(nameBytes); Require(name.StartsWith("archive/", StringComparison.Ordinal)); name = name[8..];
            Require(Services.Contains(name, StringComparer.Ordinal) || name.StartsWith("data/", StringComparison.Ordinal) && DecimalKey(name[5..]));
            int local = checked((int)U32(payload, position + 42));
            Require(local >= 0 && (long)local + 30 <= directory && U32(payload, local) == 0x04034b50 && U16(payload, local + 6) == flags && U16(payload, local + 8) == 0
                && U16(payload, local + 26) == nameSize);
            int begin = checked(local + 30 + nameSize + U16(payload, local + 28));
            Require((long)begin + length <= directory && payload.AsSpan(local + 30, nameSize).SequenceEqual(nameBytes));
            int dataEnd = checked(begin + length);
            if ((flags & 8) != 0)
            {
                Require(U32(payload, local + 14) is 0 || U32(payload, local + 14) == crc);
                Require((U32(payload, local + 18) is 0 || U32(payload, local + 18) == length32) && (U32(payload, local + 22) is 0 || U32(payload, local + 22) == length32));
                Require((long)dataEnd + 16 <= directory && U32(payload, dataEnd) == 0x08074b50 && U32(payload, dataEnd + 4) == crc
                    && U32(payload, dataEnd + 8) == length32 && U32(payload, dataEnd + 12) == length32); dataEnd += 16;
            }
            else Require(U32(payload, local + 14) == crc && U32(payload, local + 18) == length32 && U32(payload, local + 22) == length32);
            var bytes = payload.AsMemory(begin, length); uint actual = uint.MaxValue;
            foreach (byte b in bytes.Span) actual = CrcTable[(actual ^ b) & 255] ^ (actual >> 8);
            Require(~actual == crc && files.TryAdd(name, bytes)); total += length; Require(total <= budget);
            ranges.Add((local, dataEnd)); position = checked(position + 46 + nameSize + extra + comment);
        }
        Require(position == footer); ranges.Sort(); Require(ranges[0].Begin == 0);
        for (int i = 1; i < ranges.Count; i++) Require(ranges[i - 1].End <= ranges[i].Begin);
        Require(Services.All(files.ContainsKey)); return files;
    }
    private static ushort U16(byte[] bytes, int offset) { Require(offset >= 0 && offset <= bytes.Length - 2); return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset)); }
    private static uint U32(byte[] bytes, int offset) { Require(offset >= 0 && offset <= bytes.Length - 4); return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)); }
    private static ulong U64(byte[] bytes, int offset) { Require(offset >= 0 && offset <= bytes.Length - 8); return BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset)); }
    private static bool DecimalKey(string value) => value.Length > 0 && value.All(c => c is >= '0' and <= '9') && (value.Length == 1 || value[0] != '0');

    private static (object? Value, Dictionary<string, Storage> Storages) Pickle(ReadOnlyMemory<byte> payload, int budget)
    {
        Require(payload.Length is >= 3 and <= 1048576); var p = new PickleReader(payload);
        Require(p.Byte() == 0x80 && p.Byte() == 2);
        var mark = new object(); var stack = new List<object?>(); var memo = new Dictionary<int, object?>(); var storages = new Dictionary<string, Storage>(StringComparer.Ordinal);
        long storageBytes = 0;
        object? Pop() { Require(stack.Count > 0); object? value = stack[^1]; stack.RemoveAt(stack.Count - 1); return value; }
        object?[] Items()
        {
            int index = stack.FindLastIndex(v => ReferenceEquals(v, mark)); Require(index >= 0);
            var items = stack.Skip(index + 1).ToArray(); stack.RemoveRange(index, stack.Count - index); return items;
        }
        void Set(object? target, object?[] items)
        {
            var dict = Dict(target); Require(items.Length % 2 == 0 && dict.Count + items.Length / 2 <= 8192);
            for (int k = 0; k < items.Length; k += 2)
            { object? key = items[k]; Require(key is string or BigInteger); Require(dict.TryAdd(key, items[k + 1])); }
        }
        for (int counter = 1; counter <= 200000; counter++)
        {
            Require(stack.Count <= 8192 && memo.Count <= 32768);
            byte op = p.Byte();
            switch (op)
            {
                case (byte)'(': stack.Add(mark); break;
                case (byte)'N': stack.Add(null); break;
                case 0x88: stack.Add(true); break;
                case 0x89: stack.Add(false); break;
                case (byte)'J': stack.Add(new BigInteger(p.Int32())); break;
                case (byte)'K': stack.Add(new BigInteger(p.Byte())); break;
                case (byte)'M': stack.Add(new BigInteger(p.UInt16())); break;
                case 0x8a: case 0x8b:
                    int length = op == 0x8a ? p.Byte() : p.Int32(); Require(length is >= 0 and <= 9);
                    var number = new BigInteger(p.Bytes(length).Span, false, false); Require(BigInteger.Abs(number) <= BigInteger.One << 64); stack.Add(number); break;
                case (byte)'G':
                    double floating = BinaryPrimitives.ReadDoubleBigEndian(p.Bytes(8).Span); Finite(floating); stack.Add(floating); break;
                case (byte)'X':
                    int textBytes = p.Int32(); Require(textBytes is >= 0 and <= 16384);
                    string text = Utf8.GetString(p.Bytes(textBytes).Span); Require(text.EnumerateRunes().Count() <= 4096); stack.Add(text); break;
                case (byte)'}': stack.Add(new Dictionary<object, object?>()); break;
                case (byte)']': stack.Add(new List<object?>()); break;
                case (byte)')': stack.Add(new Tuple([])); break;
                case (byte)'q': case (byte)'r':
                    int put = op == (byte)'q' ? p.Byte() : p.Int32(); Require(put is >= 0 and <= 32767 && stack.Count > 0 && memo.TryAdd(put, stack[^1])); break;
                case (byte)'h': case (byte)'j':
                    int get = op == (byte)'h' ? p.Byte() : p.Int32(); Require(memo.TryGetValue(get, out object? found)); stack.Add(found); break;
                case (byte)'c':
                    string module = p.Line(), name = p.Line();
                    Require((module == "torch._utils" && name == "_rebuild_tensor_v2") || (module == "collections" && name == "OrderedDict") || (module == "torch" && name is "FloatStorage" or "LongStorage" or "ByteStorage"));
                    stack.Add(new Global(module, name)); break;
                case (byte)'t': stack.Add(new Tuple(Items())); break;
                case 0x85: case 0x86: case 0x87:
                    int count = op - 0x84; Require(stack.Count >= count); var tuple = stack.Skip(stack.Count - count).ToArray(); stack.RemoveRange(stack.Count - count, count); stack.Add(new Tuple(tuple)); break;
                case (byte)'u': var values = Items(); Require(stack.Count > 0); Set(stack[^1], values); break;
                case (byte)'s': var item = Pop(); var key = Pop(); Require(stack.Count > 0); Set(stack[^1], [key, item]); break;
                case (byte)'e': var appended = Items(); Require(stack.Count > 0); var list = List(stack[^1]); Require(list.Count + appended.Length <= 8192); list.AddRange(appended); break;
                case (byte)'a': var appendedItem = Pop(); Require(stack.Count > 0); var single = List(stack[^1]); Require(single.Count < 8192); single.Add(appendedItem); break;
                case (byte)'Q':
                    var reference = Tup(Pop(), 5); Require(reference[0] is string kind && kind == "storage" && reference[1] is Global);
                    var storageGlobal = (Global)reference[1]!; Require(storageGlobal.Module == "torch" && storageGlobal.Name is "FloatStorage" or "LongStorage" or "ByteStorage");
                    string dtype = storageGlobal.Name[..^7]; Require(reference[2] is string storageKey && DecimalKey(storageKey) && reference[3] is string location && location == "cpu");
                    string storageName = (string)reference[2]!; int itemSize = dtype == "Float" ? 4 : dtype == "Long" ? 8 : 1;
                    int elements = (int)Int(reference[4], 1, budget / itemSize); var storage = new Storage(storageName, dtype, elements);
                    if (storages.TryGetValue(storageName, out var old)) Require(old == storage);
                    else { storages.Add(storageName, storage); storageBytes += storage.Bytes; Require(storageBytes <= budget); }
                    stack.Add(storage); break;
                case (byte)'R':
                    var argsObject = Pop(); var function = Pop(); Require(argsObject is Tuple && function is Global);
                    var args = ((Tuple)argsObject).Items; var global = (Global)function;
                    if (global.Module == "collections" && global.Name == "OrderedDict") { Require(args.Length == 0); stack.Add(new Dictionary<object, object?>()); }
                    else
                    {
                        Require(global.Module == "torch._utils" && global.Name == "_rebuild_tensor_v2" && args.Length == 6 && args[0] is Storage);
                        Int(args[1], 0, 0); Exact(args[4], false); Require(Dict(args[5]).Count == 0);
                        Require(args[2] is Tuple && args[3] is Tuple); var shape = ((Tuple)args[2]!).Items; var stride = ((Tuple)args[3]!).Items;
                        Require(shape.Length <= 2 && shape.Length == stride.Length); var dimensions = new int[shape.Length]; long size = 1;
                        for (int i = shape.Length - 1; i >= 0; i--) { Int(stride[i], size, size); dimensions[i] = (int)Int(shape[i], 1, 32768); size = checked(size * dimensions[i]); }
                        var tensorStorage = (Storage)args[0]!; Require(size == tensorStorage.Count); stack.Add(new Tensor(tensorStorage, dimensions));
                    }
                    break;
                case (byte)'.': Require(p.End && stack.Count == 1); return (stack[0], storages);
                default: throw Invalid();
            }
        }
        throw Invalid();
    }
    private sealed class PickleReader(ReadOnlyMemory<byte> payload)
    {
        private int position;
        internal bool End => position == payload.Length;
        internal ReadOnlyMemory<byte> Bytes(int size) { Require(size >= 0 && size <= payload.Length - position); var value = payload.Slice(position, size); position += size; return value; }
        internal byte Byte() => Bytes(1).Span[0];
        internal int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Bytes(4).Span);
        internal int UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Bytes(2).Span);
        internal string Line()
        {
            int begin = position;
            while (Byte() != (byte)'\n') Require(position - begin <= 128);
            return Encoding.ASCII.GetString(payload.Span[begin..(position - 1)]);
        }
    }
}
