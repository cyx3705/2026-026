using System.Text.Json;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;

namespace HistoryAurora.Shell.Pages;

/// <summary>一条组件申请。</summary>
public sealed class ComponentRequest
{
    public string Component { get; set; } = "";

    /// <summary>提出方，模块名或 explicit 调用者。</summary>
    public string RequestedBy { get; set; } = "";

    /// <summary>用到它的页面（`模块/页面 id`），自动申请时填，显式申请时可为空。</summary>
    public List<string> Pages { get; set; } = [];

    public string Reason { get; set; } = "";

    public string FirstSeen { get; set; } = "";

    public string LastSeen { get; set; } = "";
}

/// <summary>
/// 组件申请台账（页面注册协议 §2.5 的落地）。
///
/// 「模块不得自建组件」是硬禁令，因此模块被缺件卡住时必须有一条**非自建的出口**，
/// 否则禁令只会被绕过——自建版本一旦上线就不会有人回来删。
///
/// 申请有两个来源，都进同一本账：
/// <list type="bullet">
///   <item><b>用出来的</b>：页面描述引用了未支持的组件，渲染成占位的同时自动记一笔。
///         这类申请最可信——它来自真实使用，不是设想。</item>
///   <item><b>提出来的</b>：`aurora.ui.request`，用于还没写进页面、但已知需要的组件。</item>
/// </list>
///
/// 台账**落盘**：一次会话内的缺件清单（`aurora.ui.missing`）重启即失，
/// 那样的"通道"没人能据以排期。
///
/// 已交付的组件在列举时自动出账，不需要谁去销账——判据是渲染器是否已支持该类型，
/// 而那是唯一权威，避免台账与实现各说各话。
/// </summary>
public sealed class ComponentRequestStore(ISettingsService settings, IShellLog log)
{
    public const string SettingKey = "aurora.ui.componentrequests";

    private const string Source = "page";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>记一笔申请；同一组件重复提交只更新 LastSeen 与页面清单。</summary>
    public void Record(string component, string requestedBy, string? page, string? reason)
    {
        if (string.IsNullOrWhiteSpace(component))
            return;

        var now = DateTime.Now.ToString("O");
        var all = Load();
        var existing = all.FirstOrDefault(r =>
            string.Equals(r.Component, component, StringComparison.OrdinalIgnoreCase));

        if (existing == null)
        {
            existing = new ComponentRequest
            {
                Component = component,
                RequestedBy = requestedBy,
                Reason = reason ?? "",
                FirstSeen = now,
            };
            all.Add(existing);
        }
        else if (!string.IsNullOrWhiteSpace(reason) && string.IsNullOrWhiteSpace(existing.Reason))
        {
            existing.Reason = reason;
        }

        existing.LastSeen = now;
        if (!string.IsNullOrWhiteSpace(page) && !existing.Pages.Contains(page, StringComparer.OrdinalIgnoreCase))
            existing.Pages.Add(page);

        Save(all);
    }

    /// <summary>
    /// 列出仍未交付的申请，并顺手把已交付的出账。
    /// </summary>
    public IReadOnlyList<ComponentRequest> ListOpen()
    {
        var all = Load();
        var open = all
            .Where(r => !PageRenderer.SupportedComponents.Contains(r.Component)
                        && !PageRenderer.SupportedCapabilities.Contains(r.Component))
            .OrderBy(r => r.FirstSeen, StringComparer.Ordinal)
            .ToList();

        if (open.Count != all.Count)
        {
            var delivered = all.Count - open.Count;
            log.Log(ShellLogLevel.Info, Source, $"组件申请出账 {delivered} 条：渲染器已支持");
            Save(open);
        }

        return open;
    }

    private List<ComponentRequest> Load()
    {
        var json = settings.Get(SettingKey);
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<ComponentRequest>>(json, Options) ?? [];
        }
        catch (JsonException ex)
        {
            // 台账损坏不该让界面起不来：记一笔并从空账重新开始。
            log.Log(ShellLogLevel.Warn, Source, $"组件申请台账无效，已重置: {ex.Message}");
            return [];
        }
    }

    private void Save(List<ComponentRequest> requests)
        => settings.Set(SettingKey, JsonSerializer.Serialize(requests, Options));
}
