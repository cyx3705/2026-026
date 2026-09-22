using System.Text.Json;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;

namespace HistoryAurora.Shell.Components.Scenes;

/// <summary>一项的使用次数与最近一次使用时间。</summary>
internal sealed record UsageEntry(int Count, DateTimeOffset LastUsed);

/// <summary>
/// 使用频次台账（REQ-UI-089）。场景、页面、动作共用这一本，键带种类前缀：
/// <c>scene:HistoryJanus</c>、<c>page:graph</c>、<c>action:minerva.conversion.run</c>。
///
/// 场景一多，今天「全局页签条太长」的问题会原样搬到场景上。对策是**只靠搜索 + 频次**，
/// 不给场景做专门的管理页——那又是一份要人维护的目录（场景与导航方案 V1.0 §八.5，已归档于 b-Office/history）。
/// 这本账就是「频次」那一半：右栏按它挑出常用的场景、给常用页面胶囊排序，搜索按它给结果加权。
/// </summary>
internal sealed class UsageLedger(ISettingsService settings, IShellLog log)
{
    public const string SettingsKey = "aurora.nav.usage";

    private Dictionary<string, UsageEntry>? _entries;

    public UsageEntry? Get(string key) => Entries.GetValueOrDefault(key);

    public void Record(string key)
    {
        var entries = Entries;
        var count = entries.TryGetValue(key, out var entry) ? entry.Count : 0;
        entries[key] = new UsageEntry(count + 1, DateTimeOffset.Now);
        Save();
    }

    public void Forget(string key)
    {
        if (Entries.Remove(key))
            Save();
    }

    private Dictionary<string, UsageEntry> Entries => _entries ??= Load();

    private Dictionary<string, UsageEntry> Load()
    {
        try
        {
            var raw = settings.Get(SettingsKey);
            if (!string.IsNullOrWhiteSpace(raw) &&
                JsonSerializer.Deserialize<Dictionary<string, UsageEntry>>(raw) is { } parsed)
            {
                return new Dictionary<string, UsageEntry>(parsed, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            // 频次只影响排序，坏了就从零数起，不值得拦住界面。
            log.Warn("nav", $"使用频次台账无法解析，从零开始计数: {ex.Message}");
        }

        return new Dictionary<string, UsageEntry>(StringComparer.OrdinalIgnoreCase);
    }

    private void Save()
    {
        try
        {
            settings.Set(SettingsKey, JsonSerializer.Serialize(_entries));
        }
        catch (Exception ex)
        {
            log.Warn("nav", $"使用频次台账写入失败: {ex.Message}");
        }
    }
}
