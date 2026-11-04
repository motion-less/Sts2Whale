using System.Text.Json;
using System.Text.Json.Nodes;

namespace CombatSolver;

// Only prepares the next process. Never treats an edited file as evidence that
// the current CLR has switched GC, and never writes through a game-file hardlink.
internal static class RuntimeGcStartupConfig
{
    internal const string ProfileKey = "CombatSolver.RuntimeProfile";
    internal const string PreviousKey = "CombatSolver.PreviousServerGc";
    internal const string PreviousHeapCountKey = "CombatSolver.PreviousHeapCount";
    private const string ServerKey = "System.GC.Server";
    private const string HeapCountKey = "System.GC.HeapCount";

    /// <summary>
    /// Server GC 开几个堆。
    ///
    /// 默认一核一个堆，但这台机器是 4 性能核 + 6 能效核：多出来的 6 个堆既在能效核上跑 GC，
    /// 又各自留一份 Gen2/LOH 缓冲。实测（同一场 Boss、同一速率 ≈1,620 条/秒）：
    ///   10 个堆 : Gen2 665 MB、LOH 551–668 MB、GC 堆 1,218–2,497 MB、WS 3.0–3.3 GB
    ///   4 个堆  : Gen2 109 MB、LOH 75–87 MB、GC 堆 506 MB、WS 2.3 GB
    ///   5 个堆  : Gen2 84 MB、LOH 56–72 MB、GC 堆 261 MB、WS 2.3 GB
    /// 所以按「核数的一半」取，既能给搜索线程各留一个堆，又不给能效核开空堆；
    /// 可用 STS2_WHALE_HEAP_COUNT 覆盖。
    /// </summary>
    internal static int ResolveHeapCount()
    {
        string? raw = Environment.GetEnvironmentVariable("STS2_WHALE_HEAP_COUNT");
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, out int configured) && configured > 0)
        {
            return Math.Clamp(configured, 1, 64);
        }
        return Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
    }

    internal static string Apply(string path, bool enabled)
    {
        byte[] original = File.ReadAllBytes(path);
        JsonObject document = JsonNode.Parse(original) as JsonObject
            ?? throw new InvalidDataException("Empty runtime configuration.");
        if (document["runtimeOptions"] is not JsonObject options)
            throw new InvalidDataException("Missing runtimeOptions.");
        JsonObject properties;
        if (options["configProperties"] is null)
        {
            if (!enabled) return "Unchanged";
            properties = new JsonObject();
            options["configProperties"] = properties;
        }
        else if (options["configProperties"] is JsonObject existing)
            properties = existing;
        else
            throw new InvalidDataException("Invalid configProperties.");

        bool owned = properties.ContainsKey(ProfileKey) || properties.ContainsKey(PreviousKey);
        string? previous = null;
        if (owned)
        {
            if (properties[ProfileKey] is not JsonValue profile
                || !profile.TryGetValue<string>(out string? value)
                || value != RuntimeGcProfile.ServerGenerational
                || properties[PreviousKey] is not JsonValue saved
                || !saved.TryGetValue<string>(out previous)
                || previous is not ("absent" or "true" or "false"))
                throw new InvalidDataException("Unknown or incomplete GC configuration ownership.");
            if (properties[ServerKey] is not JsonValue current
                || !current.TryGetValue<bool>(out bool server) || !server)
                throw new InvalidDataException("Owned GC configuration was changed externally.");
            if (enabled)
            {
                // 老版本只写了 Server，没有 HeapCount；这里补上，并把它当时的值记进
                // PreviousHeapCountKey，保证以后关掉时能还原。
                string previousHeap = "absent";
                if (properties.ContainsKey(PreviousHeapCountKey))
                {
                    previousHeap = ReadString(properties[PreviousHeapCountKey], PreviousHeapCountKey);
                }
                else if (properties[HeapCountKey] is JsonValue existingHeap
                    && existingHeap.TryGetValue<int>(out int existingCount))
                {
                    previousHeap = existingCount.ToString();
                }

                int desired = ResolveHeapCount();
                bool changed = !properties.ContainsKey(PreviousHeapCountKey)
                    || properties[HeapCountKey] is not JsonValue heapNode
                    || !heapNode.TryGetValue<int>(out int heapNow)
                    || heapNow != desired;
                // 已经是最新形态：什么都不用写，直接返回（写盘会白白改一次文件）。
                if (!changed) return "AlreadyConfigured";
                properties[PreviousHeapCountKey] = previousHeap;
                properties[HeapCountKey] = desired;
            }
            if (previous == "absent") properties.Remove(ServerKey);
            else properties[ServerKey] = previous == "true";
            RestoreHeapCount(properties);
            properties.Remove(ProfileKey);
            properties.Remove(PreviousKey);
        }
        else
        {
            if (!enabled) return "Unchanged";
            previous = "absent";
            string previousHeap = "absent";
            if (properties[HeapCountKey] is JsonValue previousHeapValue
                && previousHeapValue.TryGetValue<int>(out int previousHeapCount))
            {
                previousHeap = previousHeapCount.ToString();
            }
            if (properties.ContainsKey(ServerKey))
            {
                if (properties[ServerKey] is not JsonValue current
                    || !current.TryGetValue<bool>(out bool server))
                    throw new InvalidDataException("Invalid System.GC.Server value.");
                previous = server ? "true" : "false";
            }
            properties[ServerKey] = true;
            properties[HeapCountKey] = ResolveHeapCount();
            properties[ProfileKey] = RuntimeGcProfile.ServerGenerational;
            properties[PreviousKey] = previous;
            properties[PreviousHeapCountKey] = previousHeap;
        }

        // Same-directory rename is atomic; a crash cannot leave half a JSON file.
        // Preserve unrelated fields and decline a detected concurrent edit.
        string temporary = path + ".combatsolver-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document,
                new JsonSerializerOptions { WriteIndented = true });
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(original))
                throw new IOException("Runtime configuration changed while preparing GC mode.");
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return enabled ? "Prepared" : "Restored";
    }

    private static string ReadString(JsonNode node, string key)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out string? text) && text != null)
            return text;
        throw new InvalidDataException($"Invalid {key} value.");
    }

    /// <summary>把 HeapCount 还原成「我们改之前」的样子；记录的旧值不合法就直接删掉这个键。</summary>
    private static void RestoreHeapCount(JsonObject properties)
    {
        string previous = "absent";
        if (properties[PreviousHeapCountKey] is JsonValue stored
            && stored.TryGetValue<string>(out string? text) && text != null)
        {
            previous = text;
        }
        properties.Remove(PreviousHeapCountKey);
        if (previous == "absent")
        {
            properties.Remove(HeapCountKey);
            return;
        }
        if (int.TryParse(previous, out int count) && count > 0)
            properties[HeapCountKey] = count;
        else
            properties.Remove(HeapCountKey);
    }
}
