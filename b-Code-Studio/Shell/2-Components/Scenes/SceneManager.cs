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
    /// <summary>「全部」：注册表里的每一页。升级后的默认场景，界面与升级前一致。</summary>
    All,

    /// <summary>按模块派生：该 owner 的全部页面，随模块装卸而生灭。</summary>
    Derived,

    /// <summary>用户另存的场景。</summary>
    User,
}

/// <summary>一个场景此刻的样子。页面列表只含当前注册着的页，引用不到的进 <see cref="Broken"/>。</summary>
internal sealed record SceneInfo(
    string Id,
    string Title,
    SceneSource Source,
    IReadOnlyList<string> Pages,
    IReadOnlyList<string> Broken,
    int Uses,
    DateTimeOffset? LastUsed,
    bool Active);

internal readonly record struct SceneResult(bool Ok, string Message)
{
    public static SceneResult Success(string message) => new(true, message);

    public static SceneResult Fail(string message) => new(false, message);
}

/// <summary>
/// 场景（REQ-UI-084 / 085）：主页面的组织单位。切场景就是换掉整个停靠布局，
/// 不在当前场景里的页面不出现。
///
/// <code>场景 = 页面集合 + 停靠布局</code>
///
/// 三种来源进同一本账：「全部」、按模块派生、用户另存（<see cref="SceneSource"/>）。
/// 派生规则已经覆盖 Janus（三页协同）与 Minerva（一页）这两个极端，**不需要任何模块改代码**。
///
/// 两条刻意的取舍：
/// <list type="bullet">
///   <item>派生场景的页面集**每次从注册表重新派生**，不缓存——与页面注册协议 §1.2
///         「宿主不缓存」同一条理由：缓存会在模块改名、卸下之后变成幽灵。
///         落盘的只有用户的改动（增补、剔除、另存）。</item>
///   <item>布局存成**命名布局，名字就是场景 id**；模块场景的 id 就是模块名。
///         于是「命名布局按模块名」与「场景各记各的布局」是同一件事，不另开一种文件。</item>
/// </list>
/// </summary>
internal sealed class SceneManager
{
    public const string AllId = "all";

    public const string SettingsKey = "aurora.scenes";

    internal const string FrameworkOwner = "framework";

    internal const string AuroraSceneId = "HistoryAurora";

    private const string LogSource = "scene";

    /// <summary>
    /// 常驻页：每个场景自动带上，不进任何场景的页面集。
    ///
    /// 控制台是真常驻。命令集是**被迫**常驻：它是主文档区的锚点，窗口按钮栏也挂在那个窗格上，
    /// 藏掉它会被中央区修复当场放回去。要到拆顶栏那一阶段（REQ-UI-091）才解得开。
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
        RefreshKnown();
    }

    /// <summary>场景集合或当前场景变了：左栏与菜单据此重画。</summary>
    public event EventHandler? Changed;

    public string ActiveId => _state.Active ?? AllId;

    // ---------------------------------------------------------------- 查询

    /// <summary>全部场景，按使用频次、最近使用、标题排序。</summary>
    public IReadOnlyList<SceneInfo> List()
    {
        var windows = _docking.ListWindows();
        var scenes = new List<SceneInfo>
        {
            Describe(AllId, "全部", SceneSource.All,
                windows.Where(w => !Resident.Contains(w.Id)).Select(w => w.Id).ToList(), []),
        };

        foreach (var group in windows
                     .Where(w => !Resident.Contains(w.Id))
                     .GroupBy(w => w.Owner, StringComparer.OrdinalIgnoreCase))
        {
            var id = SceneIdFor(group.Key);
            var pages = group.Select(w => w.Id).ToList();
            var broken = new List<string>();
            if (_state.Find(id) is { } record)
            {
                foreach (var reference in record.Added)
                {
                    var hit = Resolve(windows, reference);
                    if (hit == null)
                        broken.Add(reference);
                    else if (!Resident.Contains(hit.Id) && !pages.Contains(hit.Id, StringComparer.OrdinalIgnoreCase))
                        pages.Add(hit.Id);
                }

                pages.RemoveAll(page => record.Removed.Any(reference => MatchesId(reference, page)));
            }

            scenes.Add(Describe(id, TitleFor(group.Key), SceneSource.Derived, pages, broken));
        }

        foreach (var (id, record) in _state.Scenes)
        {
            // 派生与「全部」的记录只存增补、剔除，没有 Pages；有 Pages 的才是另存场景。
            if (record.Pages == null || scenes.Any(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                continue;

            var pages = new List<string>();
            var broken = new List<string>();
            foreach (var reference in record.Pages)
            {
                var hit = Resolve(windows, reference);
                if (hit == null)
                    broken.Add(reference);
                else if (!Resident.Contains(hit.Id))
                    pages.Add(hit.Id);
            }

            scenes.Add(Describe(id, record.Title ?? id, SceneSource.User, pages, broken));
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
        return SceneResult.Success(Summarize($"已切到场景 [{target.Title}]", target));
    }

    /// <summary>
    /// 打开一页：当前场景里有就直接露面；没有就切到含它的、用得最多的那个场景。
    /// 哪个场景都没有它（被各处剔除了）就退回「全部」。
    /// </summary>
    public SceneResult Open(string? page)
    {
        var window = ResolvePage(page);
        if (window == null)
            return SceneResult.Fail($"没有页面 [{page}]（aurora.ui.windows 可查）");

        var active = Find(ActiveId);
        if (Resident.Contains(window.Id) || active?.Pages.Contains(window.Id, StringComparer.OrdinalIgnoreCase) == true)
        {
            _docking.Show(window.Id);
            return SceneResult.Success($"[{window.Title}] 在当前场景里，已打开");
        }

        var scenes = List();
        var best = scenes.FirstOrDefault(s => s.Source != SceneSource.All &&
                                              s.Pages.Contains(window.Id, StringComparer.OrdinalIgnoreCase))
                   ?? scenes.First(s => s.Source == SceneSource.All);
        var result = Go(best.Id);
        if (!result.Ok)
            return result;

        _docking.Show(window.Id);
        return SceneResult.Success($"已切到场景 [{best.Title}] 并打开 [{window.Title}]");
    }

    /// <summary>把当前布局另存为用户场景，页面集取此刻露面的页。</summary>
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

        var pages = _docking.ListWindows()
            .Where(w => w.IsVisible && !Resident.Contains(w.Id))
            .Select(RefOf)
            .ToList();
        var record = _state.GetOrAdd(id);
        record.Pages = pages;
        record.Added.Clear();
        record.Removed.Clear();
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
        return SceneResult.Success($"已另存为场景 [{record.Title}]：{pages.Count} 页");
    }

    // ---------------------------------------------------------------- 增删

    public SceneResult Add(string? page, string? sceneIdOrTitle = null)
    {
        if (!TryTarget(sceneIdOrTitle, page, out var scene, out var window, out var error))
            return SceneResult.Fail(error);

        var record = _state.GetOrAdd(scene.Id);
        if (scene.Source == SceneSource.User)
        {
            record.Pages ??= [];
            if (!record.Pages.Any(reference => MatchesId(reference, window.Id)))
                record.Pages.Add(RefOf(window));
        }
        else
        {
            record.Removed.RemoveAll(reference => MatchesId(reference, window.Id));
            if (!SceneIdFor(window.Owner).Equals(scene.Id, StringComparison.OrdinalIgnoreCase) &&
                !record.Added.Any(reference => MatchesId(reference, window.Id)))
            {
                record.Added.Add(RefOf(window));
            }
        }

        Persist();
        if (scene.Active)
            _docking.Show(window.Id);
        Changed?.Invoke(this, EventArgs.Empty);
        return SceneResult.Success($"[{window.Title}] 已加入场景 [{scene.Title}]");
    }

    public SceneResult Remove(string? page, string? sceneIdOrTitle = null)
    {
        if (!TryTarget(sceneIdOrTitle, page, out var scene, out var window, out var error))
            return SceneResult.Fail(error);

        var record = _state.GetOrAdd(scene.Id);
        if (scene.Source == SceneSource.User)
        {
            record.Pages?.RemoveAll(reference => MatchesId(reference, window.Id));
        }
        else
        {
            record.Added.RemoveAll(reference => MatchesId(reference, window.Id));
            if (SceneIdFor(window.Owner).Equals(scene.Id, StringComparison.OrdinalIgnoreCase) &&
                !record.Removed.Any(reference => MatchesId(reference, window.Id)))
            {
                record.Removed.Add(RefOf(window));
            }
        }

        Persist();
        if (scene.Active && window.IsVisible)
            _docking.Hide(window.Id);
        Changed?.Invoke(this, EventArgs.Empty);
        return SceneResult.Success($"[{window.Title}] 已移出场景 [{scene.Title}]");
    }

    /// <summary>
    /// 回到默认形态：派生场景丢掉增补与剔除，布局按各页 placement 重建。
    /// 另存场景的页面集是用户定的，只重建布局。不是当前场景的，下次切进去时再重建。
    /// </summary>
    public SceneResult Reset(string? sceneIdOrTitle = null)
    {
        var scene = Find(sceneIdOrTitle ?? ActiveId);
        if (scene == null)
            return SceneResult.Fail($"没有场景 [{sceneIdOrTitle}]（aurora.scene.list 可查）");

        var record = _state.GetOrAdd(scene.Id);
        if (scene.Source == SceneSource.Derived)
        {
            record.Added.Clear();
            record.Removed.Clear();
        }

        if (scene.Active)
        {
            Apply(Find(scene.Id)!, rebuild: true);
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
    /// 注册表里的窗口变了。新登记的页若不属于当前场景就藏起来——
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

        var active = Find(ActiveId);
        if (active is { Source: not SceneSource.All })
        {
            var keep = new HashSet<string>(active.Pages.Concat(Resident), StringComparer.OrdinalIgnoreCase);
            foreach (var window in fresh.Where(w => w.IsVisible && !keep.Contains(w.Id)))
            {
                try
                {
                    _docking.Hide(window.Id);
                }
                catch (Exception ex)
                {
                    _log.Warn(LogSource, $"新登记的页面 {window.Id} 不属于当前场景，隐藏失败: {ex.Message}");
                }
            }
        }

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

    /// <summary>页面引用写 owner/id：页面 id 只是「进程内唯一」，跨时间可能被别的模块占用。</summary>
    private static string RefOf(ToolWindowInfo window) => window.Owner + "/" + window.Id;

    private static bool MatchesId(string reference, string id)
    {
        var slash = reference.LastIndexOf('/');
        var tail = slash >= 0 ? reference[(slash + 1)..] : reference;
        return tail.Equals(id, StringComparison.OrdinalIgnoreCase);
    }

    private static ToolWindowInfo? Resolve(IReadOnlyList<ToolWindowInfo> windows, string reference)
    {
        var slash = reference.LastIndexOf('/');
        if (slash < 0)
            return windows.FirstOrDefault(w => w.Id.Equals(reference, StringComparison.OrdinalIgnoreCase));

        var owner = reference[..slash];
        var id = reference[(slash + 1)..];
        return windows.FirstOrDefault(w => w.Id.Equals(id, StringComparison.OrdinalIgnoreCase) &&
                                           w.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase));
    }

    private ToolWindowInfo? ResolvePage(string? page)
    {
        if (string.IsNullOrWhiteSpace(page))
            return null;
        var key = page.Trim();
        var windows = _docking.ListWindows();
        return windows.FirstOrDefault(w => w.Id.Equals(key, StringComparison.OrdinalIgnoreCase))
               ?? windows.FirstOrDefault(w => w.Title.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    private bool TryTarget(
        string? sceneIdOrTitle,
        string? page,
        out SceneInfo scene,
        out ToolWindowInfo window,
        out string error)
    {
        scene = null!;
        window = null!;
        var found = Find(sceneIdOrTitle ?? ActiveId);
        if (found == null)
        {
            error = $"没有场景 [{sceneIdOrTitle}]（aurora.scene.list 可查）";
            return false;
        }

        if (found.Source == SceneSource.All)
        {
            error = "「全部」本来就含每一页；要单独组合，先 aurora.scene.save 另存一个场景";
            return false;
        }

        var hit = ResolvePage(page);
        if (hit == null)
        {
            error = $"没有页面 [{page}]（aurora.ui.windows 可查）";
            return false;
        }

        if (Resident.Contains(hit.Id))
        {
            error = $"[{hit.Title}] 是常驻页，每个场景都有";
            return false;
        }

        scene = found;
        window = hit;
        error = "";
        return true;
    }

    private SceneInfo Describe(
        string id,
        string title,
        SceneSource source,
        IReadOnlyList<string> pages,
        IReadOnlyList<string> broken)
    {
        var usage = _usage.Get(UsageKey(id));
        return new SceneInfo(
            id, title, source, pages, broken,
            usage?.Count ?? 0,
            usage?.LastUsed,
            id.Equals(ActiveId, StringComparison.OrdinalIgnoreCase));
    }

    private static string Summarize(string head, SceneInfo scene)
        => scene.Broken.Count == 0
            ? $"{head}：{scene.Pages.Count} 页"
            : $"{head}：{scene.Pages.Count} 页；{scene.Broken.Count} 页未注册（{string.Join("、", scene.Broken)}）";

    private void Apply(SceneInfo scene, bool rebuild)
    {
        _applying = true;
        try
        {
            _docking.ApplyScene(LayoutName(scene.Id), scene.Pages.Concat(Resident).ToList(), rebuild);
        }
        finally
        {
            _applying = false;
            RefreshKnown();
        }

        // 断链不静默（REQ-UI-087）：场景照切，缺的页记一笔 Warn。
        if (scene.Broken.Count > 0)
            _log.Warn(LogSource, $"场景 [{scene.Title}] 引用了未注册的页面: {string.Join("、", scene.Broken)}");
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
            foreach (var record in state.Scenes.Values)
            {
                record.Added ??= [];
                record.Removed ??= [];
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

/// <summary>
/// 一个场景的用户改动。另存场景有 <see cref="Pages"/>；派生场景与「全部」只记增补、剔除与待重置。
/// </summary>
internal sealed class SceneRecord
{
    public string? Title { get; set; }

    /// <summary>另存场景的页面集，元素写 owner/id。派生场景为 null。</summary>
    public List<string>? Pages { get; set; }

    /// <summary>派生场景：从别的模块借来的页（owner/id）。</summary>
    public List<string> Added { get; set; } = [];

    /// <summary>派生场景：从自己模块里剔掉的页（owner/id）。</summary>
    public List<string> Removed { get; set; } = [];

    /// <summary>重置时它不是当前场景：下次切进去按默认形态重建布局。</summary>
    public bool ResetPending { get; set; }
}
