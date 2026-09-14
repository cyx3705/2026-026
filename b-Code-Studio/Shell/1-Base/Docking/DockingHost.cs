using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows;
using AvalonDock.Controls;
using AvalonDock.Layout;
using AvalonDock;
using HistoryAurora.Shell.Base.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;

namespace HistoryAurora.Shell.Base.Docking;

/// <summary>
/// AvalonDock 二次封装（§14.2）。Shell 对外只暴露 <see cref="IDockingService"/>，
/// 派生应用与四类标准窗口不接触任何 AvalonDock 类型。
///
/// 职责：窗口注册、默认布局构建、布局持久化（含损坏回退 N-06）、
/// 布局手势 → 等价指令（W-10，含防再入抑制），以及「每一页此刻在哪」的状态计算。
///
/// 1.20.3（REQ-UI-103）从七个 partial 收成三个，各自答一个问题：
/// <list type="bullet">
///   <item>本文件：**对外是什么**——生命周期、<see cref="IDockingService"/> 的每一条、
///         布局差分回吐指令，以及状态与查找；</item>
///   <item><c>DockingHost.Layout.cs</c>：**布局树怎么摆**——默认布局、放置、一格一页、比例、中央区修复；</item>
///   <item><c>DockingHost.Snapshot.cs</c>：**怎么存怎么取**——快照序列化与恢复，以及建在它之上的场景切换。</item>
/// </list>
/// 原来的七份是「一格多页 + 事后挑一页」时代分出来的：等位账、每页在哪一格的历史、
/// 挑选偏好各占一个文件。那条链在 1.20.2 已经删干净（DEC-034），留下的是七个文件、
/// 每个都只剩一两件事，跨文件找一条调用链要开三四个标签页。
/// </summary>
internal sealed partial class DockingHost : IDockingService
{
    private const double MaximumSideAllocation = 0.5;
    private const string LayoutSource = "layout";
    private const double RatioEpsilon = 0.02;
    private const string PlacementSettingsKey = "layout.placements";
    private string _registrationScene = "all";

    public void SetRegistrationScene(string id) => _registrationScene = id;

    private bool IsSceneDefault(ToolWindowDescriptor descriptor, string owner)
        => _registrationScene.Equals("all", StringComparison.OrdinalIgnoreCase)
           || (descriptor.Scene ?? (owner == "framework" ? "HistoryAurora" : owner))
               .Equals(_registrationScene, StringComparison.OrdinalIgnoreCase)
           || owner == "framework" && descriptor.Id is StandardWindowIds.Console or StandardWindowIds.Mcp;

    private readonly DockingManager _manager;
    private readonly ILayoutStore _store;
    private readonly IShellLog _log;
    private readonly ISettingsService? _settings;
    private readonly List<ToolWindowDescriptor> _descriptors = new();
    private readonly Dictionary<string, ToolWindowDescriptor> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object> _contents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _owners = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hiddenCenterIds = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, DockPlacementSnapshot> _orphanPlacements = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DockPlacementSnapshot> _lastVisiblePlacements = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _debounce;

    private Dictionary<string, WinState> _baseline = new(StringComparer.OrdinalIgnoreCase);
    private LayoutRoot? _attachedRoot;
    private int _suppress;
    private string? _maximizedId;
    /// <summary>
    /// 聚焦（最大化某一页）之前的布局树本身。
    ///
    /// 聚焦与还原不经序列化：留住这棵树的引用，还原时装回去。聚焦期间若要关闭或
    /// 保存命名布局，另用纯数据 JSON 快照记录聚焦前状态；两条职责不要合并。
    /// </summary>
    private LayoutRoot? _rootBeforeMaximize;

    /// <summary>聚焦前的纯数据快照，供聚焦期间关闭或保存命名布局时使用。</summary>
    private string? _snapshotBeforeMaximize;
    private bool _centerRepairPending;
    private bool _presentationRefreshPending;

    // W-05 比例语义:AvalonDock 对与文档区同面板的侧窗格采用像素语义
    // (LayoutPanelControl.OnFixChildrenDockLengths 会把星值固化为像素),
    // “占主程序窗体百分比”由封装层维护:记录目标比例,在首次排布后与
    // 窗体缩放后重新按比例施加像素尺寸。
    private readonly Dictionary<string, double> _ratios = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _preserveDefaultRatioOnSeed = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _resizeDebounce;
    private bool _windowResizePending;

    public DockingHost(
        DockingManager manager,
        IEnumerable<ToolWindowDescriptor> windows,
        ILayoutStore store,
        IShellLog log,
        ISettingsService? settings = null)
    {
        _manager = manager;
        _store = store;
        _log = log;
        _settings = settings;

        foreach (var d in windows)
        {
            if (_byId.ContainsKey(d.Id))
                throw new InvalidOperationException($"工具窗口 Id 冲突: {d.Id}(禁止静默覆盖,§5.3)");
            _descriptors.Add(d);
            _byId.Add(d.Id, d);
            _owners[d.Id] = "framework";
            _ratios[d.Id] = NormalizeRatio(d.DefaultRatio, 0.25);
        }

        // W-10:拖拽类连续手势在动作结束时才生成指令 —— 用去抖合并布局事件
        _debounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            EmitLayoutDiffs();
        };

        _manager.Loaded += (_, _) => RebaseSoon();
        _manager.LayoutFloatingWindowControlCreated += OnFloatingWindowControlCreated;

        // 主窗体缩放 → 按记录的百分比重算各停靠区尺寸(W-05)
        _resizeDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _resizeDebounce.Tick += (_, _) =>
        {
            _resizeDebounce.Stop();
            using (Suppress())
            {
                ReapplyRatios();
            }
            _manager.Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                () =>
                {
                    _windowResizePending = false;
                    _baseline = ComputeAllStates();
                });
        };
        _manager.SizeChanged += (_, _) =>
        {
            _windowResizePending = true;
            _debounce.Stop();
            _resizeDebounce.Stop();
            _resizeDebounce.Start();
        };
    }

    /// <summary>当前布局方案名(状态栏显示用)。</summary>
    public string CurrentLayoutName { get; private set; } = "默认";

    public string? MaximizedId => _maximizedId;

    /// <summary>true 表示下一次比例处理应从已恢复的布局反向采集比例,而非施加记录值。</summary>
    private bool _seedRatiosFromLayout;

    public event EventHandler<ShellCommandEventArgs>? CommandGenerated;

    public event EventHandler? WindowsChanged;

    /// <summary>已注册窗口(视图菜单构建用)。</summary>
    public IReadOnlyList<ToolWindowDescriptor> Descriptors => _descriptors;

    // ---------------------------------------------------------------- 启动/退出

    /// <summary>启动时调用:恢复上次布局,失败或不存在则构建默认布局(W-07 / N-06)。</summary>
    public void Initialize()
    {
        // 升级时保留用户已调好的每个场景作为本机注册基线，完整拓扑留在用户数据目录。
        try
        {
            foreach (var name in _store.ListNamed().Where(name => !name.EndsWith(".defaults", StringComparison.Ordinal)))
                if (_store.ReadNamed(name + ".defaults") == null && _store.ReadNamed(name) is { } saved)
                    _store.WriteNamed(name + ".defaults", saved);
        }
        catch (Exception ex)
        {
            _log.Warn(LayoutSource, $"保留场景注册基线失败: {ex.Message}");
        }
        LoadOrphanPlacements();
        var placementFallback = new Dictionary<string, DockPlacementSnapshot>(
            _orphanPlacements, StringComparer.OrdinalIgnoreCase);
        using (Suppress())
        {
            string? payload = null;
            try
            {
                payload = _store.ReadCurrent();
            }
            catch (Exception ex)
            {
                _log.Warn(LayoutSource, $"读取布局文件失败: {ex.Message}");
            }

            var restored = false;
            if (payload != null)
            {
                try
                {
                    ApplyLayoutSnapshot(payload);
                    if (!LayoutHasMainDocumentPane())
                        throw new InvalidOperationException("布局中缺少中央主文档区");
                    EnsureRegisteredWindows();
                    restored = true;
                    _seedRatiosFromLayout = true; // 以文件里的尺寸为准,反向采集比例
                    _log.Info(LayoutSource, "已恢复上次退出时的布局");
                }
                catch (Exception ex)
                {
                    // N-06:坏快照留在磁盘供诊断；恢复独立台账，不让快照里的半成品位置污染兜底。
                    _orphanPlacements = new Dictionary<string, DockPlacementSnapshot>(
                        placementFallback, StringComparer.OrdinalIgnoreCase);
                    _lastVisiblePlacements.Clear();
                    foreach (var (id, placement) in _orphanPlacements)
                        _lastVisiblePlacements[id] = placement;
                    _log.Warn(LayoutSource, $"布局文件损坏,已回退窗口位置台账/默认布局(原因: {ex.Message})");
                }
            }

            if (!restored)
            {
                BuildDefaultLayout();
                ApplyPlacementFallback();
            }

            EnsureCentralWorkspace();
            AttachLayout();
        }

        // 1.20.1 及以前存下的布局里一格可能有好几页（顶栏、侧边标签组）：每格只留选中的那一页。
        EvictExtraPages();
        ScheduleReapplyRatios();
        RebaseSoon();
    }

    /// <summary>退出时调用:自动保存当前布局(W-07)。</summary>
    public void SaveCurrentLayout()
    {
        // 完整快照与位置台账独立保存：任一写入失败都不能阻断另一项。
        try
        {
            var payload = _snapshotBeforeMaximize ?? SerializeLayout();
            _store.WriteCurrent(payload);
            _log.Info(LayoutSource, "退出前已自动保存布局");
        }
        catch (Exception ex)
        {
            _log.Error(LayoutSource, $"保存布局失败: {ex.Message}");
        }

        try
        {
            SavePlacements();
        }
        catch (Exception ex)
        {
            _log.Error(LayoutSource, $"保存窗口位置失败: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- IDockingService

    public IReadOnlyList<ToolWindowInfo> ListWindows()
        => _descriptors
            .Select(d =>
            {
                var s = ComputeState(d.Id);
                return new ToolWindowInfo(
                    d.Id, d.Title, s.Visible, s.Floating, s.Side,
                    s.Visible && !s.Floating && s.Ratio > 0 ? s.Ratio : null,
                    _owners.GetValueOrDefault(d.Id, "framework"));
            })
            .ToList();

    /// <summary>显示一页：它回到它的位置，那一格原来那一页被藏起来（REQ-UI-100）。</summary>
    public void Show(string id)
    {
        RestoreLayoutFromMaximized();
        EnsureRegistered(id);
        using (Suppress())
        {
            var anchorable = FindRequiredAnchorable(id);
            RevealAnchorable(anchorable, id, takeSeat: true);
            anchorable.IsSelected = true;
            anchorable.IsActive = true;
            EnsureCentralWorkspace();
            ReapplyRatios();
        }
    }

    /// <summary>
    /// 显示一页但不顶掉任何一页：它的位置空着才露面，否则保持隐藏。
    /// 场景按初值定显隐时用（REQ-UI-094）：切场景，以及当前场景里新登记的页（外来页先被场景藏掉，位置才空出来）。
    /// 这不是等位：只在调用这一刻看一次位置，之后谁让位都不会把它拽出来。
    /// </summary>
    public void ShowIfSeatFree(string id)
    {
        if (!_byId.TryGetValue(id, out var descriptor) || !descriptor.DefaultVisible)
            return;
        RestoreLayoutFromMaximized();
        using (Suppress())
        {
            EnsureRegistered(id);
            var anchorable = FindRequiredAnchorable(id);
            RevealAnchorable(anchorable, id, takeSeat: false);
            if (anchorable.Parent is ILayoutContainer pane &&
                PagesIn(pane).Any(other => !ReferenceEquals(other, anchorable)))
            {
                HidePage(anchorable);
            }

            EnsureCentralWorkspace();
        }
    }

    /// <summary>明确隐藏一页。它的位置就空着，不会有别的页自己补进来。</summary>
    public void Hide(string id)
    {
        RestoreLayoutFromMaximized();
        EnsureRegistered(id);
        using (Suppress())
        {
            HidePage(FindRequiredAnchorable(id));
            EnsureCentralWorkspace();
        }
    }

    /// <summary>
    /// 把一页浮出，**只给页面拖动当载体**（REQ-UI-120）。浮出之后由系统移动循环带着走，
    /// 落到停靠点上就进那一格，落空由拖动协调器隐藏——不会留下一个独立浮窗。
    /// 不在 <see cref="IDockingService"/> 上：没有指令、菜单或模块能单独把页面浮出来。
    /// </summary>
    internal void FloatForDrag(string id)
    {
        RestoreLayoutFromMaximized();
        EnsureRegistered(id);
        using (Suppress())
        {
            // 浮出不经过任何一格：藏着的页露面时不顶掉它原来位置上的页（REQ-UI-100）。
            var anchorable = FindRequiredAnchorable(id);
            RevealAnchorable(anchorable, id, takeSeat: false);
            if (!IsFloating(anchorable))
                anchorable.Float();
            EnsureCentralWorkspace();
        }
    }

    internal void ToggleAutoHide(string id)
    {
        RestoreLayoutFromMaximized();
        EnsureRegistered(id);
        using (Suppress())
        {
            var anchorable = FindRequiredAnchorable(id);
            RevealAnchorable(anchorable, id, takeSeat: false);
            anchorable.ToggleAutoHide();
            EnsureCentralWorkspace();
        }
    }

    public void Dock(string id, DockSide side, double? ratio = null, string? targetId = null)
    {
        RestoreLayoutFromMaximized();
        if (!_byId.TryGetValue(id, out var descriptor))
            throw new ArgumentException($"未注册的窗口: {id}", nameof(id));
        if (ratio is { } providedRatio &&
            (!double.IsFinite(providedRatio) || providedRatio is <= 0 or >= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(ratio), "比例须严格位于 (0,1)");
        }
        using (Suppress())
        {
            // 明确停到某处的页就是露面的页，那一格原来那一页被藏起来（REQ-UI-100）。
            _hiddenCenterIds.Remove(id);
            var anchorable = MoveToAnchorable(descriptor);
            PlaceAtSide(anchorable, side, ratio ?? descriptor.DefaultRatio, targetId, takeSeat: true);
            anchorable.IsSelected = true;
            EnsureCentralWorkspace();
        }
    }

    public void SetRatio(string id, double ratio)
    {
        RestoreLayoutFromMaximized();
        if (!double.IsFinite(ratio) || ratio is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(ratio), "比例须严格位于 (0,1)");

        var a = FindRequiredAnchorable(id);
        var side = DetectSide(a);
        if (side is null or DockSide.Tab or DockSide.Center)
        {
            _log.Warn(LayoutSource, $"窗口 {id} 当前不在可调整比例的四边停靠区");
            return;
        }

        using (Suppress())
        {
            ApplyRatio(a, side.Value, ratio);
            ReapplyRatios();
        }
    }

    /// <summary>把一页摆回登记时的默认位置并露面，那一格原来那一页被藏起来。</summary>
    public void ResetWindow(string id)
    {
        RestoreLayoutFromMaximized();
        var d = _byId[id];
        using (Suppress())
        {
            _hiddenCenterIds.Remove(id);
            PlaceAtDefault(MoveToAnchorable(d), takeSeat: true);
            EnsureCentralWorkspace();
        }
    }

    public void ResetLayout()
    {
        RestoreLayoutFromMaximized();
        using (Suppress())
        {
            BuildDefaultLayout();
            EnsureCentralWorkspace();
            AttachLayout();
            CurrentLayoutName = "默认";
            _seedRatiosFromLayout = false;
            foreach (var d in _descriptors)
                _ratios[d.Id] = NormalizeRatio(d.DefaultRatio, 0.25);
        }

        ScheduleReapplyRatios();
        RebaseSoon();
    }

    public void SaveLayout(string name)
    {
        _store.WriteNamed(name, _snapshotBeforeMaximize ?? SerializeLayout());
        CurrentLayoutName = name;
        _log.Info(LayoutSource, $"布局方案已保存: {name}");
    }

    public bool LoadLayout(string name)
    {
        RestoreLayoutFromMaximized();
        string? payload;
        try
        {
            payload = _store.ReadNamed(name);
        }
        catch (Exception ex)
        {
            _log.Error(LayoutSource, $"读取布局方案 {name} 失败: {ex.Message}");
            return false;
        }

        if (payload == null)
        {
            _log.Warn(LayoutSource, $"布局方案不存在: {name}");
            return false;
        }

        try
        {
            using (Suppress())
            {
                ApplyLayoutSnapshot(payload);
                if (!LayoutHasMainDocumentPane())
                    throw new InvalidOperationException("布局中缺少中央主文档区");
                EnsureRegisteredWindows();
                EnsureCentralWorkspace();
                AttachLayout();
                CurrentLayoutName = name;
                _seedRatiosFromLayout = true;
            }

            EvictExtraPages();
            RebaseSoon();
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(LayoutSource, $"加载布局方案 {name} 失败: {ex.Message}");
            using (Suppress())
            {
                BuildDefaultLayout();
                EnsureCentralWorkspace();
                AttachLayout();
                CurrentLayoutName = "默认";
            }

            RebaseSoon();
            return false;
        }
    }

    public IReadOnlyList<string> ListLayouts() => _store.ListNamed()
        .Where(name => !name.EndsWith(".defaults", StringComparison.Ordinal)).ToList();

    public void RegisterWindow(ToolWindowDescriptor descriptor, string owner)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (_byId.ContainsKey(descriptor.Id))
        {
            throw new InvalidOperationException(
                $"工具窗口 Id 冲突: {descriptor.Id}(禁止静默覆盖,§5.3)");
        }

        // 新登记的页不抢位（REQ-UI-100）：它的位置上已经有页，就留原来那一页，新来的藏着——
        // 不记账、不等位，要它露面走右栏常用页面或 aurora.ui.show。
        // 不看位置台账里的「上次选中」：台账是全局的、不分场景，拿它去抢会把当前场景的页顶掉
        // （1.20.0 首次热装真机撞到）。
        using (Suppress())
        {
            _descriptors.Add(descriptor);
            _byId.Add(descriptor.Id, descriptor);
            _owners[descriptor.Id] = owner;
            var placement = TakeOrphanPlacement(descriptor.Id);
            var hidden = placement?.Hidden ??
                         (_hiddenCenterIds.Contains(descriptor.Id) || !descriptor.DefaultVisible || !IsSceneDefault(descriptor, owner));
            var anchorable = MoveToAnchorable(descriptor);
            PlaceAtSide(
                anchorable,
                placement?.Side ?? descriptor.DefaultSide,
                placement?.Ratio ?? descriptor.DefaultRatio,
                placement?.TabTarget ?? descriptor.DefaultTabTarget,
                takeSeat: false);
            if (hidden)
                HidePage(anchorable);
            EnsureCentralWorkspace();
        }

        ScheduleReapplyRatios();
        WindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>热重载只换内容，保留停靠节点、隐藏、分栏、浮窗与专注布局。</summary>
    public void ReplaceWindow(ToolWindowDescriptor descriptor, string owner)
    {
        if (!_byId.TryGetValue(descriptor.Id, out var previous))
        {
            RegisterWindow(descriptor, owner);
            return;
        }
        if (!_owners[descriptor.Id].Equals(owner, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"窗口 {descriptor.Id} 已由其他模块注册");
        var content = descriptor.ContentFactory?.Invoke();
        using (Suppress())
        {
            _byId[descriptor.Id] = descriptor;
            _descriptors[_descriptors.IndexOf(previous)] = descriptor;
            var roots = new[] { _manager.Layout, _rootBeforeMaximize }.OfType<LayoutRoot>().Distinct();
            foreach (var root in roots)
            foreach (var node in root.Descendents().OfType<LayoutContent>().Concat(root.Hidden)
                         .Where(node => node.ContentId == descriptor.Id).Distinct())
            {
                node.Title = descriptor.Title;
                node.Content = content;
            }
            if (_contents.Remove(descriptor.Id, out var old) && !ReferenceEquals(old, content))
                TryDispose(old, descriptor.Id);
            if (content != null)
                _contents[descriptor.Id] = content;
        }
        WindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UnregisterWindow(string id)
    {
        if (!_byId.TryGetValue(id, out var descriptor))
            return;

        RestoreLayoutFromMaximized();
        using (Suppress())
        {
            if (CapturePlacements().TryGetValue(id, out var previousPlacement))
                _orphanPlacements[id] = previousPlacement;
            var anchorable = FindAnchorable(id);
            if (anchorable != null)
            {
                Detach(anchorable);
                anchorable.Content = null;
            }

            _descriptors.Remove(descriptor);
            _byId.Remove(id);
            _ratios.Remove(id);
            _baseline.Remove(id);
            _preserveDefaultRatioOnSeed.Remove(id);
            _owners.Remove(id);
            _hiddenCenterIds.Remove(id);
            if (_contents.Remove(id, out var content))
                TryDispose(content, id);
            _manager.Layout.CollectGarbage();
            EnsureCentralWorkspace();
        }

        WindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UnregisterOwner(string owner)
    {
        RestoreLayoutFromMaximized();
        foreach (var id in _owners
                     .Where(pair => pair.Value.Equals(owner, StringComparison.OrdinalIgnoreCase))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            UnregisterWindow(id);
        }
    }

    public void MaximizeWindow(string id)
    {
        if (_maximizedId != null && _maximizedId.Equals(id, StringComparison.OrdinalIgnoreCase))
            return;
        RestoreLayoutFromMaximized();
        if (!_byId.ContainsKey(id))
            throw new ArgumentException($"未注册的工具窗口: {id}", nameof(id));

        using (Suppress())
        {
            // 中途抛出时必须把暂存的布局丢掉：_maximizedId 还是 null，
            // RestoreLayoutFromMaximized 会直接返回，而保存布局与关闭窗口那两条路径
            // 写的是 `_snapshotBeforeMaximize ?? SerializeLayout()`——
            // 留着它就等于把一份**过期的**布局当成当前布局写回磁盘。
            try
            {
                _rootBeforeMaximize = _manager.Layout;
                // 布局持久化是另一件事，失败不该拖垮"聚焦这一页"。
                _snapshotBeforeMaximize = SerializeLayout();
                BuildMaximizedLayout(id);
                AttachLayout();
                _maximizedId = id;
            }
            catch
            {
                _rootBeforeMaximize = null;
                _snapshotBeforeMaximize = null;
                throw;
            }
        }

        WindowsChanged?.Invoke(this, EventArgs.Empty);
        RebaseSoon();
    }

    public void RestoreLayoutFromMaximized()
    {
        if (_maximizedId == null || _rootBeforeMaximize == null)
            return;

        using (Suppress())
        {
            // 聚焦只替换 LayoutRoot；还原时装回原树。1.22（REQ-UI-120）起没有独立浮窗，
            // 不再需要从快照里把浮窗节点重建一遍。
            _manager.Layout = _rootBeforeMaximize;

            if (!LayoutHasMainDocumentPane())
                throw new InvalidOperationException("布局中缺少中央主文档区");
            // 聚焦期间新注册的窗口不在那棵旧树里，补一遍。
            EnsureRegisteredWindows();
            EnsureCentralWorkspace();
            AttachLayout();
            _maximizedId = null;
            _rootBeforeMaximize = null;
            _snapshotBeforeMaximize = null;
            _seedRatiosFromLayout = true;
        }

        EvictExtraPages();
        WindowsChanged?.Invoke(this, EventArgs.Empty);
        RebaseSoon();
    }

    // ---------------------------------------------------------------- 布局事件 → 指令(W-10)

    private void AttachLayout()
    {
        if (_attachedRoot != null)
            _attachedRoot.Updated -= OnLayoutUpdated;

        _attachedRoot = _manager.Layout;
        _attachedRoot.Updated += OnLayoutUpdated;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_suppress > 0 || _windowResizePending)
            return;

        ScheduleCentralWorkspaceRepair();
        ScheduleCenterDocumentPresentation();
        ScheduleSinglePageCheck();

        // 手势进行中持续触发 → 去抖,静默 500ms 后视为动作结束
        _debounce.Stop();
        _debounce.Start();
    }

    private void EmitLayoutDiffs()
    {
        if (_suppress > 0)
            return;

        if (NeedsCentralWorkspaceRepair())
        {
            using (Suppress())
                EnsureCentralWorkspace();
            return;
        }

        var now = ComputeAllStates();
        foreach (var d in _descriptors)
        {
            if (!_baseline.TryGetValue(d.Id, out var was))
                continue;
            var cur = now[d.Id];

            if (was.Visible && !cur.Visible)
            {
                Emit($"aurora.ui.hide name={d.Id}");
                continue;
            }

            if (!was.Visible && cur.Visible)
                Emit($"aurora.ui.show name={d.Id}");

            if (!cur.Visible)
                continue;

            // 浮着只是拖动途中的载体（REQ-UI-120）：不回放成指令——aurora.ui.float 已删除，
            // 落进一格后由下面的停靠差分回放，落空隐藏由上面的 hide 回放。
            if (cur.Floating || cur.Side == null)
                continue;

            var dockChanged = was.Floating || was.Side != cur.Side;
            if (dockChanged)
            {
                Emit(cur.Side == DockSide.Center
                        ? $"aurora.ui.dock name={d.Id} pos=center"
                        : $"aurora.ui.dock name={d.Id} pos={SideText(cur.Side.Value)} ratio={FormatRatio(cur.Ratio)}");
            }
            else if (was.Ratio > 0 && cur.Ratio > 0 && Math.Abs(was.Ratio - cur.Ratio) > RatioEpsilon)
            {
                // was.Ratio == 0 说明基线建立时尚未完成渲染(尺寸未知),不视为用户手势
                Emit($"aurora.ui.ratio name={d.Id} value={FormatRatio(cur.Ratio)}");
            }
        }

        // 用户拖拽分隔条 / 重新停靠后,同步更新比例记录(W-05)
        foreach (var (id, s) in now)
        {
            if (s is { Visible: true, Floating: false, Side: not null and not DockSide.Tab and not DockSide.Center, Ratio: > 0 })
                _ratios[id] = s.Ratio;
        }

        using (Suppress())
            ReapplyRatios();

        _baseline = now;
    }

    private Dictionary<string, WinState> ComputeAllStates()
        => _descriptors.ToDictionary(d => d.Id, d => ComputeState(d.Id), StringComparer.OrdinalIgnoreCase);

    private void Emit(string commandText)
    {
        // 以指令回显类别落管道(L-03):控制台按 "[layout] > ..." 样式渲染,可一键屏蔽(C-04)
        _log.Info(HistoryVulcan.Core.Commands.CommandBus.EchoCategoryPrefix + LayoutSource, commandText);
        CommandGenerated?.Invoke(this, new ShellCommandEventArgs
        {
            CommandText = commandText,
            Source = LayoutSource,
        });
    }

    /// <summary>防再入(§14.2 清单 4):指令/API 驱动的布局变更不回声成新指令。</summary>
    private IDisposable Suppress()
    {
        _suppress++;
        _debounce.Stop();
        return new SuppressScope(this);
    }

    private void RebaseSoon()
        => _manager.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle, // 渲染完成后再取快照,保证比例已可测量
            () =>
            {
                _baseline = ComputeAllStates();
                RecordFloatingPages();
            });

    private sealed class SuppressScope : IDisposable
    {
        private DockingHost? _host;

        public SuppressScope(DockingHost host) => _host = host;

        public void Dispose()
        {
            if (_host == null)
                return;
            var host = _host;
            _host = null;
            host._suppress--;
            if (host._suppress == 0)
                host.RebaseSoon(); // 渲染一拍后重建基线,吸收本次程序化变更
        }
    }

    // ---------------------------------------------------------------- 工具

    // AvalonDock 的 ILayoutPositionableElement 为 internal,
    // DockWidth / DockHeight 只能经各具体面板类型访问 —— 用反射统一读写
    private static GridLength GetDockLength(ILayoutElement element, bool horizontal)
    {
        var prop = element.GetType().GetProperty(horizontal ? "DockWidth" : "DockHeight");
        return prop?.GetValue(element) is GridLength len
            ? len
            : new GridLength(1, GridUnitType.Star);
    }

    private static void SetDockLength(ILayoutElement element, bool horizontal, GridLength value)
    {
        var prop = element.GetType().GetProperty(horizontal ? "DockWidth" : "DockHeight");
        if (prop != null && prop.CanWrite)
            prop.SetValue(element, value);
    }

    private static GridLength Star(double value)
        => new(Math.Max(value, 0.02), GridUnitType.Star);

    private static string SideText(DockSide side) => side switch
    {
        DockSide.Left => "left",
        DockSide.Right => "right",
        DockSide.Top => "top",
        DockSide.Bottom => "bottom",
        _ => "center",
    };

    private static double NormalizeRatio(double ratio, double fallback)
    {
        if (double.IsFinite(ratio) && ratio is > 0 and < 1)
            return ratio;
        return double.IsFinite(fallback) && fallback is > 0 and < 1 ? fallback : 0.25;
    }

    private static string FormatRatio(double value)
        => value.ToString("0.##", CultureInfo.InvariantCulture);


    // ---------------------------------------------------------------- 状态与查找

    private sealed record WinState(bool Visible, bool Floating, DockSide? Side, double Ratio);

    private WinState ComputeState(string id)
    {
        var a = FindAnchorable(id);
        if (a == null || a.IsHidden || _hiddenCenterIds.Contains(id))
            return new WinState(false, false, null, 0);

        if (IsFloating(a))
            return new WinState(true, true, null, 0);

        if (IsHostedInDocumentPane(a))
            return new WinState(true, false, DockSide.Center, 0);

        var side = DetectSide(a);
        return new WinState(true, false, side, DetectRatio(a, side));
    }

    private static bool IsFloating(LayoutContent content)
        => IsInsideFloatingWindow(content);

    private static bool IsInsideFloatingWindow(ILayoutElement element)
    {
        for (ILayoutContainer? p = element.Parent; p != null; p = (p as ILayoutElement)?.Parent)
        {
            if (p is LayoutFloatingWindow)
                return true;
        }

        return false;
    }

    private DockSide? DetectSide(LayoutAnchorable a)
    {
        if (a.IsHidden || IsFloating(a))
            return null;

        ILayoutElement? centerAnchor = TryFindMainDocumentPane();
        if (centerAnchor == null)
            return null;

        for (ILayoutContainer? parent = centerAnchor.Parent;
             parent != null;
             parent = (parent as ILayoutElement)?.Parent)
        {
            if (parent is not LayoutPanel panel)
                continue;
            var windowChild = ChildContaining(panel, a);
            var centerChild = ChildContaining(panel, centerAnchor);
            if (windowChild == null || centerChild == null || ReferenceEquals(windowChild, centerChild))
                continue;

            var before = panel.Children.IndexOf(windowChild) < panel.Children.IndexOf(centerChild);
            return panel.Orientation == Orientation.Horizontal
                ? before ? DockSide.Left : DockSide.Right
                : before ? DockSide.Top : DockSide.Bottom;
        }

        return null;
    }

    private double DetectRatio(LayoutAnchorable a, DockSide? side)
    {
        if (side == null)
            return 0;

        var pane = a.Parent as ILayoutElement;
        if (pane == null)
            return 0;

        var fe = FindControlFor(pane);
        if (fe == null || _manager.ActualWidth <= 0 || _manager.ActualHeight <= 0)
            return 0;

        if (side == DockSide.Center)
            return 0;
        var ratio = side is DockSide.Left or DockSide.Right
            ? fe.ActualWidth / _manager.ActualWidth
            : fe.ActualHeight / _manager.ActualHeight;
        ratio = Math.Round(ratio, 2);
        return double.IsFinite(ratio) && ratio is > 0 and < 1 ? ratio : 0;
    }

    private static ILayoutPanelElement? ChildContaining(LayoutPanel panel, ILayoutElement element)
    {
        ILayoutElement current = element;
        while (current.Parent != null && !ReferenceEquals(current.Parent, panel))
            current = current.Parent;
        return ReferenceEquals(current.Parent, panel) ? current as ILayoutPanelElement : null;
    }

    private FrameworkElement? FindControlFor(ILayoutElement model)
        => FindVisualDescendants(_manager)
            .FirstOrDefault(fe => fe is ILayoutControl lc && ReferenceEquals(lc.Model, model));

    private static IEnumerable<FrameworkElement> FindVisualDescendants(DependencyObject parent)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement fe)
                yield return fe;
            foreach (var g in FindVisualDescendants(child))
                yield return g;
        }
    }

    private LayoutAnchorable? FindAnchorable(string id)
        => _manager.Layout.Descendents()
            .OfType<LayoutAnchorable>()
            .Concat(_manager.Layout.Hidden)
            .FirstOrDefault(a => string.Equals(a.ContentId, id, StringComparison.OrdinalIgnoreCase));

    private LayoutAnchorable FindRequiredAnchorable(string id)
    {
        if (!_byId.ContainsKey(id))
            throw new ArgumentException($"未注册的窗口: {id}", nameof(id));
        return FindAnchorable(id)
               ?? throw new InvalidOperationException($"窗口 {id} 不在当前布局中");
    }
}
