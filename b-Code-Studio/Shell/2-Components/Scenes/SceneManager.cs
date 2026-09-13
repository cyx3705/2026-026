using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using HistoryAurora.Shell.Base.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;

namespace HistoryAurora.Shell.Components.Scenes;

/// <summary>场景从哪里来（场景与导航方案 §1.1）。</summary>
internal enum SceneSource
{
    /// <summary>「全部」：第一次进入时每一页都露面。升级后的默认场景，界面与升级前一致。</summary>
    All,

    /// <summary>按模块派生：第一次进入时露出该 owner 的页，随模块装卸而生灭。</summary>
    Derived,

    /// <summary>用户另存的场景。</summary>
    User,
}

/// <summary>一个场景此刻的样子。场景不含页面集合（REQ-UI-094），因此这里也没有页数与断链。</summary>
internal sealed record SceneInfo(
    string Id,
    string Title,
    SceneSource Source,
    int Uses,
    DateTimeOffset? LastUsed,
    bool Active);

internal readonly record struct SceneResult(bool Ok, string Message)
{
    public static SceneResult Success(string message) => new(true, message);

    public static SceneResult Fail(string message) => new(false, message);
}

/// <summary>
/// 场景（REQ-UI-084 / 085 / 094）：主页面的组织单位。
///
/// <code>场景 = 一份命名布局（哪些页露面、停在哪）</code>
///
/// **场景不拥有页面**（1.20.0 用户拍板）：场景之间的区别只是页面的显示与隐藏，
/// 不存在「这一页属于哪个场景」这件事。1.19.0 的页面集合、增补、剔除与断链账因此整套删掉，
/// <c>aurora.scene.add / remove</c> 一并退役——显隐就是 <c>aurora.ui.show / hide</c>。
///
/// 按模块派生的场景第一次进入时露出该模块的页与常驻页，那只是**初值**；
/// 之后它完全按你离开时的样子恢复，与「全部」、另存场景没有区别。
///
/// 布局存成**命名布局，名字就是场景 id**；模块场景的 id 就是模块名（DEC-031）。
/// </summary>
internal sealed class SceneManager
{
    public const string AllId = "all";

    public const string SettingsKey = "aurora.scenes";

    internal const string FrameworkOwner = "framework";

    internal const string AuroraSceneId = "HistoryAurora";

    private const string LogSource = "scene";

    /// <summary>
    /// 常驻页：每个场景第一次进入时都露面。之后它们的显隐同样只记在场景布局里——
    /// 1.20.0 起命令集不再是主文档区的锚点（REQ-UI-095），可以被顶栏的新页顶掉，也可以拖出隐藏。
    /// </summary>
    public static readonly IReadOnlySet<string> Resident = new HashSet<string>(
        [StandardWindowIds.Console, StandardWindowIds.Mcp],
        StringComparer.OrdinalIgnoreCase);

    private readonly ISceneDocking _docking;
    private readonly ISettingsService _settings;
    private readonly UsageLedger _usage;
    private readonly IShellLog _log;
    private readonly SceneState _state;
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private bool _applying;

    public SceneManager(ISceneDocking docking, ISettingsService settings, UsageLedger usage, IShellLog log)
    {
        _docking = docking;
        _settings = settings;
        _usage = usage;
        _log = log;
        _state = SceneState.Parse(settings.Get(SettingsKey), log);
        _docking.SetRegistrationScene(ActiveId);
        RefreshKnown();
    }

    /// <summary>场景集合或当前场景变了：右栏与菜单据此重画。</summary>
    public event EventHandler? Changed;

    public string ActiveId => _state.Active ?? AllId;

    /// <summary>初次发现完成才恢复完整布局：启动前半轮还没有模块的停靠节点。</summary>
    public void RestoreActiveLayout()
    {
        if (Find(ActiveId) is { } active)
            Apply(active, rebuild: false);
    }

    // ---------------------------------------------------------------- 查询

    /// <summary>全部场景，按使用频次、最近使用、标题排序。</summary>
    public IReadOnlyList<SceneInfo> List()
    {
        var scenes = new List<SceneInfo> { Describe(AllId, "全部", SceneSource.All) };

        foreach (var owner in _docking.ListWindows()
                     .Where(w => !Resident.Contains(w.Id))
                     .Select(w => w.Owner)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            scenes.Add(Describe(SceneIdFor(owner), TitleFor(owner), SceneSource.Derived));
        }

        foreach (var (id, record) in _state.Scenes)
        {
            if (record.Saved && !scenes.Any(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                scenes.Add(Describe(id, record.Title ?? id, SceneSource.User));
        }

        return scenes
            .OrderByDescending(s => s.Uses)
            .ThenByDescending(s => s.LastUsed ?? DateTimeOffset.MinValue)
            .ThenBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>按 id、标题、或去掉 History 的简称找场景（「Janus」「janus」「HistoryJanus」都认）。</summary>
    public SceneInfo? Find(string? idOrTitle)
    {
        if (string.IsNullOrWhiteSpace(idOrTitle))
            return null;
        var key = idOrTitle.Trim();
        var scenes = List();
        return scenes.FirstOrDefault(s => s.Id.Equals(key, StringComparison.OrdinalIgnoreCase))
               ?? scenes.FirstOrDefault(s => s.Title.Equals(key, StringComparison.OrdinalIgnoreCase))
               ?? scenes.FirstOrDefault(s => s.Id.Equals("History" + key, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- 切换

    public SceneResult Go(string? idOrTitle)
    {
        var target = Find(idOrTitle);
        if (target == null)
            return SceneResult.Fail($"没有场景 [{idOrTitle}]（aurora.scene.list 可查）");

        SaveActiveLayout();
        var record = _state.Find(target.Id);
        var rebuild = record?.ResetPending == true;
        Apply(target, rebuild);
        if (record != null)
            record.ResetPending = false;

        _state.Active = target.Id;
        Persist();
        _usage.Record(UsageKey(target.Id));
        Changed?.Invoke(this, EventArgs.Empty);
        return SceneResult.Success($"已切到场景 [{target.Title}]：{VisibleCount()} 页露面");
    }

    /// <summary>在当前场景里打开一页。场景不拥有页面，所以不存在「切到含它的场景」。</summary>
    public SceneResult Open(string? page)
    {
        var window = ResolvePage(page);
        if (window == null)
            return SceneResult.Fail($"没有页面 [{page}]（aurora.ui.windows 可查）");

        _docking.Show(window.Id);
        return SceneResult.Success($"[{window.Title}] 已在当前场景打开");
    }

    /// <summary>把当前布局另存为用户场景并切到它。</summary>
    public SceneResult Save(string? id, string? title)
    {
        if (string.IsNullOrWhiteSpace(id))
            return SceneResult.Fail("场景 id 不能为空");
        id = id.Trim();
        if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return SceneResult.Fail($"场景 id [{id}] 含文件名不允许的字符（它同时是命名布局的文件名）");

        var clash = List().FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (clash is { Source: not SceneSource.User })
        {
            var kind = clash.Source == SceneSource.All ? "内置" : "按模块派生的";
            return SceneResult.Fail($"[{id}] 是{kind}场景，不能覆盖；换一个 id");
        }

        // 先把当前场景记下来：每个场景都记着你离开时的样子，另存不例外。
        SaveActiveLayout();

        var record = _state.GetOrAdd(id);
        record.Saved = true;
        record.ResetPending = false;
        record.Title = string.IsNullOrWhiteSpace(title) ? record.Title ?? id : title.Trim();

        try
        {
            _docking.SaveLayout(LayoutName(id));
        }
        catch (Exception ex)
        {
            return SceneResult.Fail($"场景 [{id}] 的布局写入失败: {ex.Message}");
        }

        _state.Active = id;
        Persist();
        _usage.Record(UsageKey(id));
        Changed?.Invoke(this, EventArgs.Empty);
        return SceneResult.Success($"已另存为场景 [{record.Title}]：{VisibleCount()} 页露面");
    }

    /// <summary>
    /// 回到初值：「全部」与模块场景按各页 placement 重建，露面的页回到第一次进入时的样子。
    /// 不是当前场景的，下次切进去时再重建。
    ///
    /// 另存场景没有初值可回，只能在它是当前场景时重排：此刻露面的页按各自默认位置摆回去。
    /// </summary>
    public SceneResult Reset(string? sceneIdOrTitle = null)
    {
        var scene = Find(sceneIdOrTitle ?? ActiveId);
        if (scene == null)
            return SceneResult.Fail($"没有场景 [{sceneIdOrTitle}]（aurora.scene.list 可查）");

        if (scene.Source == SceneSource.User && !scene.Active)
            return SceneResult.Fail($"[{scene.Title}] 是另存的场景，没有初值可回；先切到它，重置会把此刻露面的页摆回默认位置");

        var record = _state.GetOrAdd(scene.Id);
        if (scene.Active)
        {
            Apply(scene, rebuild: true);
            record.ResetPending = false;
        }
        else
        {
            record.ResetPending = true;
        }

        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
        return SceneResult.Success(scene.Active
            ? $"场景 [{scene.Title}] 已回到默认形态"
            : $"场景 [{scene.Title}] 已标记重置，下次切进去时按默认形态重建");
    }

    public SceneResult Delete(string? id)
    {
        var scene = List().FirstOrDefault(s => s.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (scene == null)
            return SceneResult.Fail($"没有场景 [{id}]（aurora.scene.list 可查）");
        if (scene.Source != SceneSource.User)
            return SceneResult.Fail($"[{scene.Title}] 不是另存的场景；按模块派生的场景随模块装卸，「全部」不能删");

        _state.Scenes.Remove(scene.Id);
        _usage.Forget(UsageKey(scene.Id));
        if (scene.Active)
        {
            // 被删的场景不再写回布局，直接切走。
            _state.Active = AllId;
            Apply(Find(AllId)!, rebuild: false);
        }

        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
        return SceneResult.Success($"场景 [{scene.Title}] 已删除");
    }

    // ---------------------------------------------------------------- 生命周期

    /// <summary>把当前布局写回当前场景。切走前与退出前各调一次。</summary>
    public void SaveActiveLayout()
    {
        try
        {
            _docking.SaveLayout(LayoutName(ActiveId));
        }
        catch (Exception ex)
        {
            _log.Warn(LogSource, $"场景 {ActiveId} 的布局保存失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 注册表里的窗口变了。新登记的页按当前场景的**初值规则**决定露不露面——
    /// 否则在 Minerva 场景里，Janus 热重载一次，它的三页就会挤进来。
    /// 由界面在 <c>WindowsChanged</c> 之后调用；切场景自己引起的那一次不处理。
    /// </summary>
    public void OnWindowsChanged()
    {
        if (_applying)
            return;

        var windows = _docking.ListWindows();
        var fresh = windows.Where(w => !_known.Contains(w.Id)).ToList();
        RefreshKnown(windows);
        if (fresh.Count == 0)
            return;

        // 注册时同步决定显隐；延迟回调只更新导航，不能复活用户隐藏的页。
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------- 命名

    /// <summary>页面 owner → 派生场景 id。框架自持的页归「Aurora」场景。</summary>
    internal static string SceneIdFor(string owner)
        => owner.Equals(FrameworkOwner, StringComparison.OrdinalIgnoreCase) ? AuroraSceneId : owner;

    /// <summary>页签上本来就叫 Janus、Minerva，场景名跟着去掉 History 前缀。</summary>
    internal static string TitleFor(string owner)
    {
        var id = SceneIdFor(owner);
        return id.StartsWith("History", StringComparison.Ordinal) && id.Length > "History".Length
            ? id["History".Length..]
            : id;
    }

    /// <summary>场景的布局存成同名命名布局（用户拍板：命名布局按模块名）。</summary>
    internal static string LayoutName(string sceneId) => sceneId;

    private static string UsageKey(string sceneId) => "scene:" + sceneId;

    /// <summary>
    /// 初值规则：第一次进入（或重置）时哪些页露面。只在场景没有布局可恢复时用得上。
    /// 「全部」是每一页；模块场景是该模块的页 + 常驻页；另存场景一定存过布局，
    /// 走到这里只剩重置，初值就是此刻露面的页。
    /// </summary>
    private IReadOnlyCollection<string> SeedFor(SceneInfo scene)
    {
        var windows = _docking.ListWindows();
        return scene.Source switch
        {
            SceneSource.All => windows.Select(w => w.Id).ToList(),
            SceneSource.User => windows.Where(w => w.IsVisible).Select(w => w.Id).ToList(),
            _ => windows.Where(w => IsSeeded(scene, w)).Select(w => w.Id).ToList(),
        };
    }

    private static bool IsSeeded(SceneInfo scene, ToolWindowInfo window)
        => Resident.Contains(window.Id)
           || scene.Source == SceneSource.All
           || scene.Source == SceneSource.Derived &&
              SceneIdFor(window.Owner).Equals(scene.Id, StringComparison.OrdinalIgnoreCase);

    private int VisibleCount() => _docking.ListWindows().Count(w => w.IsVisible);

    private ToolWindowInfo? ResolvePage(string? page)
    {
        if (string.IsNullOrWhiteSpace(page))
            return null;
        var key = page.Trim();
        var windows = _docking.ListWindows();
        return windows.FirstOrDefault(w => w.Id.Equals(key, StringComparison.OrdinalIgnoreCase))
               ?? windows.FirstOrDefault(w => w.Title.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    private SceneInfo Describe(string id, string title, SceneSource source)
    {
        var usage = _usage.Get(UsageKey(id));
        return new SceneInfo(
            id, title, source,
            usage?.Count ?? 0,
            usage?.LastUsed,
            id.Equals(ActiveId, StringComparison.OrdinalIgnoreCase));
    }

    private void Apply(SceneInfo scene, bool rebuild)
    {
        var seed = SeedFor(scene);

        // 每格只留一页（REQ-UI-100）：模块场景里，本模块的页与常驻页挤在同一格时留本模块的页。
        var own = scene.Source == SceneSource.Derived
            ? _docking.ListWindows()
                .Where(w => !Resident.Contains(w.Id) && IsSeeded(scene, w))
                .Select(w => w.Id)
                .ToList()
            : [];
        _applying = true;
        try
        {
            _docking.ApplyScene(LayoutName(scene.Id), seed, rebuild, own);
        }
        finally
        {
            _applying = false;
            RefreshKnown();
        }
    }

    private void RefreshKnown(IReadOnlyList<ToolWindowInfo>? windows = null)
    {
        _known.Clear();
        foreach (var window in windows ?? _docking.ListWindows())
            _known.Add(window.Id);
    }

    private void Persist()
    {
        try
        {
            _settings.Set(SettingsKey, _state.Serialize());
        }
        catch (Exception ex)
        {
            _log.Warn(LogSource, $"场景设置写入失败: {ex.Message}");
        }
    }
}

/// <summary>场景设置的落盘形状（设置键 <see cref="SceneManager.SettingsKey"/>）。</summary>
internal sealed class SceneState
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public int SchemaVersion { get; set; } = 1;

    public string? Active { get; set; }

    public Dictionary<string, SceneRecord> Scenes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public SceneRecord? Find(string id) => Scenes.GetValueOrDefault(id);

    public SceneRecord GetOrAdd(string id)
    {
        if (!Scenes.TryGetValue(id, out var record))
            Scenes[id] = record = new SceneRecord();
        return record;
    }

    public string Serialize() => JsonSerializer.Serialize(this, Options);

    public static SceneState Parse(string? json, IShellLog log)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new SceneState();

        try
        {
            var state = JsonSerializer.Deserialize<SceneState>(json, Options) ?? new SceneState();
            state.Scenes = new Dictionary<string, SceneRecord>(
                state.Scenes ?? new Dictionary<string, SceneRecord>(),
                StringComparer.OrdinalIgnoreCase);

            // 1.19.0 的另存场景靠「有 pages」来认；页面集合退役后改记 saved，旧账读一次就换掉。
            // 同一版里的 added / removed 没有去处，反序列化时直接丢弃。
            foreach (var record in state.Scenes.Values)
            {
                if (record.LegacyPages == null)
                    continue;
                record.Saved = true;
                record.LegacyPages = null;
            }

            return state;
        }
        catch (Exception ex)
        {
            log.Warn("scene", $"场景设置无法解析，按空账重建（布局文件不受影响）: {ex.Message}");
            return new SceneState();
        }
    }
}

/// <summary>一个场景的用户改动：标题、是不是另存的、待重置标记。场景的样子本身在同名命名布局里。</summary>
internal sealed class SceneRecord
{
    public string? Title { get; set; }

    /// <summary>用户另存的场景。派生场景与「全部」的记录只用来挂待重置标记。</summary>
    public bool Saved { get; set; }

    /// <summary>1.19.0 另存场景的页面集。只读不写，见 <see cref="SceneState.Parse"/>。</summary>
    [JsonPropertyName("pages")]
    public List<string>? LegacyPages { get; set; }

    /// <summary>重置时它不是当前场景：下次切进去按默认形态重建布局。</summary>
    public bool ResetPending { get; set; }
}
