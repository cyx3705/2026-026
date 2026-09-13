using System.Text.Json;

namespace HistoryAurora.Shell.Components.Table;

/// <summary>兼容旧行数组，并支持带版本的完整快照与增量。</summary>
internal sealed record TableUpdate(
    bool IsDelta,
    string? Revision,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Rows,
    IReadOnlyList<string> Removes)
{
    internal static TableUpdate Read(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
            return new(false, null, ReadRows(root), []);
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("取数返回的不是合法行集");
        var mode = root.GetProperty("mode").GetString();
        if (mode is not ("snapshot" or "delta"))
            throw new InvalidOperationException("未知表格更新模式");
        var revision = root.GetProperty("revision").GetString();
        if (string.IsNullOrEmpty(revision))
            throw new InvalidOperationException("更新缺少 revision");
        var rows = ReadRows(root.GetProperty(mode == "delta" ? "upserts" : "rows"));
        var removes = mode == "delta" && root.TryGetProperty("removes", out var removed)
            ? removed.Deserialize<List<string>>() ?? throw new InvalidOperationException("removes 必须是数组")
            : new List<string>();
        if (removes.Any(string.IsNullOrEmpty))
            throw new InvalidOperationException("删除行键不能为空");
        return new(mode == "delta", revision, rows, removes);
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> ReadRows(JsonElement value)
    {
        var rows = value.Deserialize<List<Dictionary<string, string>>>()
            ?? throw new InvalidOperationException("行集不能为空值");
        if (rows.Any(row => row == null))
            throw new InvalidOperationException("行不能为空值");
        return rows;
    }

    internal void ValidateKeys(string rowKey)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Rows)
        {
            if (!row.TryGetValue(rowKey, out var key) || string.IsNullOrEmpty(key) || !keys.Add(key))
                throw new InvalidOperationException("行键缺失或重复：" + rowKey);
        }
    }
}
