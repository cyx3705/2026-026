using System.Windows;
using System.Windows.Controls;
using HistoryVulcan.Core.Commands;
using HistoryAurora.Shell.Docking;
using HistoryVulcan.Core.Logging;
using HistoryAurora.Shell.Modules;


namespace HistoryAurora.Shell.Pages;

/// <summary>一次拉取的结果，供命令回显与测试断言。</summary>
internal sealed record PageLoadReport(
    int ModulesAsked,
    int PagesRegistered,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<MissingComponent> Missing);

/// <summary>某个模块的某一页引用了组件库尚未提供的组件。</summary>
internal sealed record MissingComponent(string Owner, string PageId, string Component);

/// <summary>
/// 按页面注册协议 V1 从模块拉取页面描述并建页。
///
/// **方向是拉取，不是推送**：Aurora 主动问，宿主不持有页面描述。
/// 这条是刻意的——宿主侧已有的 <c>web.frontendcatalog</c> 正是"宿主缓存 + 连上时重放"，
/// 它按客户端名做键且不做存活性回收，前端改名一次就留下永久幽灵条目。
/// 页面若照抄那条路，幽灵会从"命令数不准"升级成"界面上多出一个打不开的页面"。
/// 拉取让注册成为派生状态，没有缓存可失效。
/// </summary>
internal sealed class ModulePageLoader(
    CommandBus bus,
    IDockingService docking,
    IShellLog log,
    ComponentRequestStore? requests = null,
    HistoryAurora.Shell.Actions.ActionRegistry? actions = null,
    HistoryAurora.Shell.CommandSurface.AuroraCompletionProvider? completions = null,
    HistoryAurora.Shell.Selection.SelectionChannels? channels = null,
    PageDataRefresher? refresher = null)
{
    private const string Source = "page";

    /// <summary>协议约定的描述命令后缀；模块以自己的域注册，例如 mercury.ui.describe。</summary>
    public const string DescribeSuffix = ".ui.describe";

    private readonly List<MissingComponent> _missing = [];
    private readonly HashSet<string> _owners = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private Task<PageLoadReport>? _inFlight;
    private bool _pending;

    /// <summary>最近一次拉取发现的缺件，供 aurora.ui.missing 查询。</summary>
    public IReadOnlyList<MissingComponent> Missing => _missing.ToList();

    /// <summary>
    /// 全量重拉，**合并重入请求**。
    ///
    /// 启动期会连续触发多次：ModuleHost 初次装载一次，宿主的 moduleRevision 通知引发
    /// <c>ReloadConfirmedSources</c> 又各一次——真机实测一次冷启动打了三遍。
    /// 不合并的话，等模块真有页面时就是三倍的 describe 调用与三轮建撤抖动。
    /// 已在跑时只置脏标记，跑完再补一轮，因此最后一次请求的结果一定被反映。
    /// </summary>
    public Task<PageLoadReport> ReloadAsync(CancellationToken cancellation = default)
    {
        TaskCompletionSource<PageLoadReport> completion;
        lock (_gate)
        {
            if (_inFlight != null)
            {
                _pending = true;
                return _inFlight;
            }

            // 先把占位任务放进 _inFlight，再启动实际工作。
            // 不能写成 _inFlight = DrainAsync(...)：当描述命令同步完成时，DrainAsync 会在
            // 返回前就把 _inFlight 置回 null，外层随后又把那个**已完成**的任务赋回去，
            // 于是此后每次拉取都命中"已在跑"分支并返回一个永不推进的旧任务——彻底卡死。
            completion = new TaskCompletionSource<PageLoadReport>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlight = completion.Task;
        }

        _ = DrainAsync(completion, cancellation);
        return completion.Task;
    }

    private async Task DrainAsync(
        TaskCompletionSource<PageLoadReport> completion,
        CancellationToken cancellation)
    {
        try
        {
            while (true)
            {
                var report = await ReloadCoreAsync(cancellation).ConfigureAwait(true);
                lock (_gate)
                {
                    if (_pending)
                    {
                        _pending = false;
                        continue;
                    }

                    _inFlight = null;
                }

                completion.SetResult(report);
                return;
            }
        }
        catch (Exception ex)
        {
            // 失败也必须解开闸门，否则一次异常会让此后所有拉取都卡在"已在跑"。
            lock (_gate)
            {
                _inFlight = null;
                _pending = false;
            }

            completion.SetException(ex);
        }
    }

    private async Task<PageLoadReport> ReloadCoreAsync(CancellationToken cancellation)
    {
        foreach (var owner in _owners.ToList())
            Drop(owner);
        _missing.Clear();

        var owners = await DescribableOwnersAsync(cancellation).ConfigureAwait(true);
        var registered = 0;
        var skipped = new List<string>();

        foreach (var owner in owners)
        {
            var count = await LoadOwnerAsync(owner, skipped, cancellation).ConfigureAwait(true);
            registered += count;
        }

        // 通道断链只能在**整轮建页之后**判：面板所在的页可能先于表格所在的页渲染，
        // 建时判会把正常情况报成断链。这也是它不做成渲染期占位的原因。
        var dangling = channels?.Dangling ?? [];
        foreach (var reason in dangling)
            log.Log(ShellLogLevel.Warn, Source, "选择通道断链: " + reason);

        log.Log(ShellLogLevel.Info, Source,
            $"页面拉取完成: 问了 {owners.Count} 个模块，建了 {registered} 页"
            + (skipped.Count > 0 ? $"，跳过 {skipped.Count} 个" : "")
            + (dangling.Count > 0 ? $"，{dangling.Count} 条通道断链" : ""));

        return new PageLoadReport(owners.Count, registered, skipped, Missing);
    }

    /// <summary>
    /// 只重拉一个模块（aurora.ui.invalidate）。模块内容变化时由模块自己触发。
    ///
    /// 参数**同时接受模块名与域名**，因为这里有两个身份要用：撤旧页按 owner
    /// （<c>HistoryMercury</c>），拉描述按域（<c>mercury.ui.describe</c>）。
    /// 两者按设计就不相等——域去掉 <c>History</c> 前缀（宿主手册 §3.3.1）。
    /// 1.6.0 把同一个字符串既当 owner 又当域使，结果是：传 <c>owner=HistoryMercury</c>
    /// 会去调不存在的 <c>HistoryMercury.ui.describe</c>；传 <c>owner=mercury</c> 拉得到描述，
    /// 却在 <c>_owners</c> 里撤不掉旧页，随后同 id 重注册失败。
    /// **任何模块的这条上行通知都拉不到描述**，而全量拉取那条路探测的是注册表里的域，
    /// 因此一直是好的——缺陷只在这一条路径上，也只在真机上看得见。
    /// </summary>
    public async Task<PageLoadReport> ReloadOwnerAsync(
        string ownerOrDomain,
        CancellationToken cancellation = default)
    {
        var domain = ModuleDomainNaming.ToDomain(ownerOrDomain ?? "");
        if (domain.Length == 0)
            return new PageLoadReport(0, 0, [], Missing);

        var owner = ModuleCommandProbe.ExpectedOwner(domain);
        Drop(owner);
        _missing.RemoveAll(m => string.Equals(m.Owner, owner, StringComparison.OrdinalIgnoreCase));

        var skipped = new List<string>();
        var registered = await LoadOwnerAsync(domain, skipped, cancellation).ConfigureAwait(true);
        return new PageLoadReport(1, registered, skipped, Missing);
    }

    /// <summary>哪些模块声明了页面：判定收在 <see cref="ModuleCommandProbe"/>，与动作声明同一口径。</summary>
    private Task<List<string>> DescribableOwnersAsync(CancellationToken cancellation)
        => ModuleCommandProbe.OwnersWithSuffixAsync(bus, log, Source, DescribeSuffix, cancellation);

    private async Task<int> LoadOwnerAsync(string domain, List<string> skipped, CancellationToken cancellation)
    {
        CommandResult result;
        try
        {
            result = await bus.ExecuteAsync(domain + DescribeSuffix, "UI", cancellation).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            return Skip(skipped, domain, "调用失败: " + ex.Message);
        }

        if (!result.Success)
            return Skip(skipped, domain, result.Message);

        // 描述可能来自跨进程中继，结构化载荷不保证存活，因此同样接受 Message 承载。
        var payload = result.Data as string ?? result.Message;
        var parsed = PageDescriptionReader.Read(payload, ModuleCommandProbe.ExpectedOwner(domain));
        if (!parsed.Ok)
            return Skip(skipped, domain, parsed.Error!);

        var registered = 0;
        foreach (var page in parsed.Value!.Pages)
        {
            if (Register(parsed.Value.Owner, page))
                registered++;
        }

        _owners.Add(parsed.Value.Owner);
        return registered;
    }

    private bool Register(string owner, PageDescription page)
    {
        RenderedPage rendered;
        try
        {
            rendered = PageRenderer.Render(
                page,
                new PageRenderContext
                {
                    Bus = bus,
                    Log = log,
                    Owner = owner,
                    Actions = actions,
                    Completions = completions,
                    Channels = channels,
                    Refresher = refresher,
                });
        }
        catch (Exception ex)
        {
            // 渲染失败只影响这一页，不牵连同模块的其他页，更不牵连别的模块（协议 §1.5）。
            log.Log(ShellLogLevel.Warn, Source, $"{owner}: 页面 {page.Id} 渲染失败: {ex.Message}");
            return false;
        }

        foreach (var component in rendered.MissingComponents)
        {
            _missing.Add(new MissingComponent(owner, page.Id, component));
            // 用出来的申请自动进台账：它来自真实使用，比设想出来的需求可信。
            requests?.Record(component, owner, owner + "/" + page.Id, null);
        }

        var content = Inset(rendered.Root);

        try
        {
            docking.RegisterWindow(new ToolWindowDescriptor
            {
                Id = page.Id,
                Title = page.Title,
                DefaultSide = ParseSide(page.Placement.Side),
                DefaultRatio = Clamp(page.Placement.Ratio),
                DefaultTabTarget = page.Placement.TabTarget,
                DefaultVisible = page.Placement.Visible,
                IsSingleton = page.Placement.Singleton,
                ContentFactory = () => content,
            }, owner);
        }
        catch (Exception ex)
        {
            log.Log(ShellLogLevel.Warn, Source, $"{owner}: 页面 {page.Id} 注册失败: {ex.Message}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 页面内容相对窗格的内边距（REQ-UI-047）。
    ///
    /// **这一层归 Aurora，不归模块。** 界面自持的 XAML 页按风格规范 §7 自己写
    /// <c>Margin="{DynamicResource Aurora.Space.Pad}"</c>，组件测试页也在
    /// <c>ComponentGalleryView</c> 里写了一句 12；而描述协议这一路没有任何字段能表达它，
    /// 于是模块页的第一个控件一直是贴着窗格边框画的——同一个界面里两种页看得出差别，
    /// 而模块作者没有任何办法把它补上。
    ///
    /// 补在这里而不是 <see cref="PageRenderer.Render"/>：渲染器交出的是**组件树**，
    /// 内边距是它与停靠窗格之间的关系，不属于任何一个组件。放进渲染器还会让
    /// 嵌套渲染（组件测试页、单元测试）各自多套一层。
    /// </summary>
    private static FrameworkElement Inset(FrameworkElement content)
        => new Border { Child = content, Padding = new Thickness(PagePad) };

    /// <summary>
    /// <c>Aurora.Space.Pad</c> 的值。
    ///
    /// **不走 DynamicResource**，与 <see cref="PageRenderer"/> 里的间距同一条理由：
    /// 间距令牌在浅色与深色里取值相同（都是 12），不随主题变化；而要让
    /// <c>SetResourceReference</c> 在这一层解析得到，就得把主题字典并进这个 Border——
    /// 那会把整棵模块页钉在被并进来的那一套配色上，主题一切换它不跟。
    /// 一个不随主题变的数字，不值得用一条会破坏主题跟随的机制去取。
    /// </summary>
    private const double PagePad = 12;

    private void Drop(string owner)
    {
        if (!_owners.Remove(owner))
            return;

        // 页面撤了，它声明的通道跟着撤。留着的话就是一条永远不会再有人发布的幽灵通道，
        // 而跟着它的按钮会一直灰着——那正是宿主侧 web.frontendcatalog 的老毛病。
        channels?.DropOwner(owner);
        refresher?.DropOwner(owner);

        try
        {
            docking.UnregisterOwner(owner);
        }
        catch (Exception ex)
        {
            log.Log(ShellLogLevel.Warn, Source, $"撤销 {owner} 的页面失败: {ex.Message}");
        }
    }

    private int Skip(List<string> skipped, string domain, string reason)
    {
        skipped.Add(domain);
        log.Log(ShellLogLevel.Warn, Source, $"跳过模块 {domain}: {reason}");
        return 0;
    }

    private static DockSide ParseSide(string? side)
        => (side ?? "").ToLowerInvariant() switch
        {
            "left" => DockSide.Left,
            "top" => DockSide.Top,
            "bottom" => DockSide.Bottom,
            "center" => DockSide.Center,
            "tab" => DockSide.Tab,
            _ => DockSide.Right,
        };

    /// <summary>比例必须严格落在 (0,1)，越界按缺省值处理而不是让停靠库抛。</summary>
    private static double Clamp(double ratio)
        => ratio is > 0 and < 1 ? ratio : 0.25;
}
