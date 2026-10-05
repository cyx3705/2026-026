using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using HistoryAurora.Shell.Base.Docking;
using HistoryVulcan.Core.Logging;
using HistoryAurora.Shell.Neutral.Storage;
using HistoryAurora.Shell.Neutral.Logging;

namespace HistoryAurora.Shell.Components.Scenes;

/// <summary>场景从哪里来。</summary>
internal enum SceneSource
{
    // 1.30.0 删除内置场景「全部」（REQ-UI-134）：每个场景都是一个模块的或另存的。

    /// <summary>按模块派生：第一次进入时露出该 owner 的页，随模块装卸而生灭。</summary>
    Derived,

    /// <summary>用户另存的场景。</summary>
    User,
}

/// <summary>
/// 一个场景此刻的样子。场景不含页面集合（REQ-UI-094），因此这里也没有页数与断链。
/// <see cref="Hidden"/>：不在右栏列出，收在页面调整时浮出的小栏里（REQ-UI-135）；搜索照样搜得到。
/// </summary>
internal sealed record SceneInfo(
    string Id,
    string Title,
    SceneSource Source,
    int Uses,
    DateTimeOffset? LastUsed,
    bool Active,
    bool Hidden = false);

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
/// 之后它完全按你离开时的样子恢复，与另存场景没有区别。
///
/// 布局存成**命名布局，名字就是场景 id**；模块场景的 id 就是模块名（DEC-031）。
///
/// 1.30.0（REQ-UI-134 / 135）：内置场景「全部」删除，缺省场景改为 Aurora 自己的场景；
/// 场景的先后由人拖着排（<see cref="Move"/>），可以收进小栏（<see cref="SetHidden"/>），
/// 模块场景也能删——删掉的记一笔墓碑，模块再装回来也不复活，要 <c>aurora.scene.reset</c> 点名恢复。
/// </summary>
internal sealed class SceneManager
{
    /// <summary>1.29 及以前的内置场景「全部」。只用来认旧设置：读到它就换成缺省场景。</summary>
    internal const string LegacyAllId = "all";

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

    /// <summary>当前场景。没有记录时是 Aurora 自己的场景——它的页（模块管理、组件测试）由界面自持，开机就在。</summary>
    public string ActiveId => string.IsNullOrWhiteSpace(_state.Active) ? AuroraSceneId : _state.Active;

    /// <summary>初次发现完成才恢复完整布局：启动前半轮还没有模块的停靠节点。</summary>
    public void RestoreActiveLayout()
    {
        if (Find(ActiveId) is { } active)
            Apply(active, rebuild: false);
    }

    // ---------------------------------------------------------------- 查询

    /// <summary>
    /// 全部场景（含收进小栏的，不含删掉的），按人排的先后（REQ-UI-135）。
    /// 没排过的场景——新装的模块、刚另存的——按标题接在最后，人拖一下就进了排序。
    /// </summary>
    public IReadOnlyList<SceneInfo> List()
    {
        var scenes = new List<SceneInfo>();

        foreach (var owner in _docking.ListWindows()
                     .Where(w => !Resident.Contains(w.Id))
                     .Select(w => w.Owner)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var id = SceneIdFor(owner);
            if (!_state.IsDeleted(id))
                scenes.Add(Describe(id, TitleFor(owner), SceneSource.Derived));
        }

        foreach (var (id, record) in _state.Scenes)
        {
            if (record.Saved && !scenes.Any(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                scenes.Add(Describe(id, record.Title ?? id, SceneSource.User));
        }

        return scenes
            .OrderBy(s => _state.OrderOf(s.Id))
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
            return SceneResult.Fail($"[{id}] 是按模块派生的场景，不能覆盖；换一个 id");

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
    /// 回到初值：模块场景按各页 placement 重建，露面的页回到第一次进入时的样子。
    /// 不是当前场景的，下次切进去时再重建。
    ///
    /// 另存场景没有初值可回，只能在它是当前场景时重排：此刻露面的页按各自默认位置摆回去。
    ///
    /// 点名一个删掉的模块场景就是把它恢复（REQ-UI-135）：墓碑撤掉、排到最后、下次切进去按默认形态重建。
    /// </summary>
    public SceneResult Reset(string? sceneIdOrTitle = null)
    {
        var scene = Find(sceneIdOrTitle ?? ActiveId);
        if (scene == null && sceneIdOrTitle != null && _state.Undelete(sceneIdOrTitle.Trim()) is { } revived)
        {
            var record = _state.GetOrAdd(revived);
            record.ResetPending = true;
            Persist();
            Changed?.Invoke(this, EventArgs.Empty);
            return SceneResult.Success($"场景 [{TitleFor(revived)}] 已恢复，下次切进去时按默认形态重建");
        }

        if (scene == null)
            return SceneResult.Fail($"没有场景 [{sceneIdOrTitle}]（aurora.scene.list 可查）");

        if (scene.Source == SceneSource.User && !scene.Active)
            return SceneResult.Fail($"[{scene.Title}] 是另存的场景，没有初值可回；先切到它，重置会把此刻露面的页摆回默认位置");

        var current = _state.GetOrAdd(scene.Id);
        if (scene.Active)
        {
            Apply(scene, rebuild: true);
            current.ResetPending = false;
        }
        else
        {
            current.ResetPending = true;
        }

        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
        return SceneResult.Success(scene.Active
            ? $"场景 [{scene.Title}] 已回到默认形态"
            : $"场景 [{scene.Title}] 已标记重置，下次切进去时按默认形态重建");
    }

    /// <summary>
    /// 删除一个场景（REQ-UI-135）。另存的连同记录一起删；模块场景随模块装卸、删了也会再派生出来，
    /// 所以记一笔墓碑——它从此不再列出，<c>aurora.scene.reset id=&lt;模块&gt;</c> 点名才恢复。
    /// 删的是当前场景就切到排在最前、没收进小栏的那个。最后一个场景不能删：总得有地方落脚。
    /// </summary>
    public SceneResult Delete(string? id)
    {
        var scenes = List();
        var scene = scenes.FirstOrDefault(s => s.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (scene == null)
            return SceneResult.Fail($"没有场景 [{id}]（aurora.scene.list 可查）");
        if (scenes.Count <= 1)
            return SceneResult.Fail($"[{scene.Title}] 是最后一个场景，不能删");

        if (scene.Source == SceneSource.User)
        {
            _state.Scenes.Remove(scene.Id);
        }
        else
        {
            _state.Delete(scene.Id);
            // 恢复时按默认形态重建，不带回删除前的布局。
            _state.GetOrAdd(scene.Id).ResetPending = true;
        }

        _state.Forget(scene.Id);
        _usage.Forget(UsageKey(scene.Id));
        if (scene.Active)
        {
            // 被删的场景不再写回布局，直接切走。
            var next = List().FirstOrDefault(s => !s.Hidden) ?? List()[0];
            var record = _state.Find(next.Id);
            _state.Active = next.Id;
            Apply(next, rebuild: record?.ResetPending == true);
            if (record != null)
                record.ResetPending = false;
        }

        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
        return SceneResult.Success($"场景 [{scene.Title}] 已删除");
    }

    // ---------------------------------------------------------------- 排序与收起（REQ-UI-135）

    /// <summary>
    /// 把场景挪到 <paramref name="before"/> 前面；<paramref name="before"/> 为空则挪到最后。
    /// 用「挪到谁前面」而不是序号：右栏只列没收起的场景，序号在右栏和完整清单里不是一回事。
    /// </summary>
    public SceneResult Move(string? id, string? before)
    {
        var scenes = List();
        var scene = scenes.FirstOrDefault(s => s.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (scene == null)
            return SceneResult.Fail($"没有场景 [{id}]（aurora.scene.list 可查）");

        SceneInfo? anchor = null;
        if (!string.IsNullOrWhiteSpace(before))
        {
            anchor = scenes.FirstOrDefault(s => s.Id.Equals(before.Trim(), StringComparison.OrdinalIgnoreCase));
            if (anchor == null)
                return SceneResult.Fail($"没有场景 [{before}]（aurora.scene.list 可查）");
        }

        // 挪到自己前面就是原地不动。
        if (anchor != null && anchor.Id.Equals(scene.Id, StringComparison.OrdinalIgnoreCase))
            return SceneResult.Success($"场景 [{scene.Title}] 位置未变");

        var order = scenes.Select(s => s.Id).ToList();
        order.RemoveAll(s => s.Equals(scene.Id, StringComparison.OrdinalIgnoreCase));
        var at = anchor == null
            ? -1
            : order.FindIndex(s => s.Equals(anchor.Id, StringComparison.OrdinalIgnoreCase));
        if (at < 0)
            order.Add(scene.Id);
        else
            order.Insert(at, scene.Id);

        _state.Order = order;
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
        return SceneResult.Success(anchor == null
            ? $"场景 [{scene.Title}] 已排到最后"
            : $"场景 [{scene.Title}] 已排到 [{anchor.Title}] 前面");
    }

    /// <summary>收进小栏 / 放回右栏。只影响右栏列不列它，切换、搜索、菜单照旧。</summary>
    public SceneResult SetHidden(string? id, bool hidden)
    {
        var scene = List().FirstOrDefault(s => s.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (scene == null)
            return SceneResult.Fail($"没有场景 [{id}]（aurora.scene.list 可查）");

        _state.SetHidden(scene.Id, hidden);
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
        return SceneResult.Success(hidden
            ? $"场景 [{scene.Title}] 已收进小栏"
            : $"场景 [{scene.Title}] 已放回右栏");
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
    /// 模块场景是该模块的页 + 常驻页；另存场景一定存过布局，
    /// 走到这里只剩重置，初值就是此刻露面的页。
    /// </summary>
    private IReadOnlyCollection<string> SeedFor(SceneInfo scene)
    {
        var windows = _docking.ListWindows();
        return scene.Source switch
        {
            SceneSource.User => windows.Where(w => w.IsVisible).Select(w => w.Id).ToList(),
            _ => windows.Where(w => IsSeeded(scene, w)).Select(w => w.Id).ToList(),
        };
    }

    private static bool IsSeeded(SceneInfo scene, ToolWindowInfo window)
        => Resident.Contains(window.Id)
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
            id.Equals(ActiveId, StringComparison.OrdinalIgnoreCase),
            _state.IsHidden(id));
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

    /// <summary>人排的先后（REQ-UI-135）。不在表里的场景按标题接在最后。</summary>
    public List<string> Order { get; set; } = [];

    /// <summary>收进小栏的场景：右栏不列，搜索与菜单照旧。</summary>
    public List<string> Hidden { get; set; } = [];

    /// <summary>删掉的模块场景（墓碑）。模块再装回来也不派生，<c>aurora.scene.reset</c> 点名恢复。</summary>
    public List<string> Deleted { get; set; } = [];

    public SceneRecord? Find(string id) => Scenes.GetValueOrDefault(id);

    public int OrderOf(string id)
    {
        var index = Order.FindIndex(s => s.Equals(id, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? int.MaxValue : index;
    }

    public bool IsHidden(string id) => Hidden.Contains(id, StringComparer.OrdinalIgnoreCase);

    public bool IsDeleted(string id) => Deleted.Contains(id, StringComparer.OrdinalIgnoreCase);

    public void SetHidden(string id, bool hidden)
    {
        Hidden.RemoveAll(s => s.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (hidden)
            Hidden.Add(id);
    }

    public void Delete(string id)
    {
        if (!IsDeleted(id))
            Deleted.Add(id);
    }

    /// <summary>按 id、或去掉 History 的简称撤掉墓碑；返回撤掉的那个 id，没有则 null。</summary>
    public string? Undelete(string key)
    {
        var id = Deleted.FirstOrDefault(s => s.Equals(key, StringComparison.OrdinalIgnoreCase))
                 ?? Deleted.FirstOrDefault(s => s.Equals("History" + key, StringComparison.OrdinalIgnoreCase));
        if (id != null)
            Deleted.Remove(id);
        return id;
    }

    /// <summary>场景没了：从排序与小栏里抹掉。恢复或重新另存时按新场景接在最后。</summary>
    public void Forget(string id)
    {
        Order.RemoveAll(s => s.Equals(id, StringComparison.OrdinalIgnoreCase));
        Hidden.RemoveAll(s => s.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

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
            state.Order ??= [];
            state.Hidden ??= [];
            state.Deleted ??= [];

            // 1.30.0 删除内置场景「全部」：停在它上面的换成缺省场景，它的待重置记录一并丢掉。
            // 布局文件 all.layout 不动——它只是不再被读。
            if (SceneManager.LegacyAllId.Equals(state.Active, StringComparison.OrdinalIgnoreCase))
                state.Active = null;
            state.Scenes.Remove(SceneManager.LegacyAllId);

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

    /// <summary>用户另存的场景。派生场景的记录只用来挂待重置标记。</summary>
    public bool Saved { get; set; }

    /// <summary>1.19.0 另存场景的页面集。只读不写，见 <see cref="SceneState.Parse"/>。</summary>
    [JsonPropertyName("pages")]
    public List<string>? LegacyPages { get; set; }

    /// <summary>重置时它不是当前场景：下次切进去按默认形态重建布局。</summary>
    public bool ResetPending { get; set; }
}
