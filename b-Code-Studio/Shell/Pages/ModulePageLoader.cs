using HistoryVulcan.Core.Commands;
using HistoryAurora.Shell.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Services.Commands;

namespace HistoryAurora.Shell.Pages;

/// <summary>一次拉取的结果，供命令回显与测试断言。</summary>
public sealed record PageLoadReport(
    int ModulesAsked,
    int PagesRegistered,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<MissingComponent> Missing);

/// <summary>某个模块的某一页引用了组件库尚未提供的组件。</summary>
public sealed record MissingComponent(string Owner, string PageId, string Component);

/// <summary>
/// 按页面注册协议 V1 从模块拉取页面描述并建页。
///
/// **方向是拉取，不是推送**：Aurora 主动问，宿主不持有页面描述。
/// 这条是刻意的——宿主侧已有的 <c>web.frontendcatalog</c> 正是"宿主缓存 + 连上时重放"，
/// 它按客户端名做键且不做存活性回收，前端改名一次就留下永久幽灵条目。
/// 页面若照抄那条路，幽灵会从"命令数不准"升级成"界面上多出一个打不开的页面"。
/// 拉取让注册成为派生状态，没有缓存可失效。
/// </summary>
public sealed class ModulePageLoader(
    CommandBus bus,
    IDockingService docking,
    IShellLog log,
    ComponentRequestStore? requests = null)
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

        log.Log(ShellLogLevel.Info, Source,
            $"页面拉取完成: 问了 {owners.Count} 个模块，建了 {registered} 页"
            + (skipped.Count > 0 ? $"，跳过 {skipped.Count} 个" : ""));

        return new PageLoadReport(owners.Count, registered, skipped, Missing);
    }

    /// <summary>只重拉一个模块（aurora.ui.invalidate）。模块内容变化时由模块自己触发。</summary>
    public async Task<PageLoadReport> ReloadOwnerAsync(string owner, CancellationToken cancellation = default)
    {
        Drop(owner);
        _missing.RemoveAll(m => string.Equals(m.Owner, owner, StringComparison.OrdinalIgnoreCase));

        var skipped = new List<string>();
        var registered = await LoadOwnerAsync(owner, skipped, cancellation).ConfigureAwait(true);
        return new PageLoadReport(1, registered, skipped, Missing);
    }

    /// <summary>
    /// 哪些模块声明了页面：直接看注册表里有没有 <c>&lt;域&gt;.ui.describe</c>。
    ///
    /// 不用"先问 manifest 的 ui 标志再逐个试"——那要求宿主把 manifest 字段透出来，
    /// 而注册表本来就是权威且已经在手边；模块没注册该命令就是没有页面，不是错误。
    /// </summary>
    private async Task<List<string>> DescribableOwnersAsync(CancellationToken cancellation)
    {
        var names = bus.Registry.All().Select(d => d.Name).ToList();
        if (bus.RemoteExecutor != null)
        {
            try
            {
                var listed = await bus.ExecuteAsync("vulcan.command.list", "UI", cancellation)
                    .ConfigureAwait(true);
                if (listed.Success
                    && CommandResultData.TryRead<IReadOnlyList<CommandCatalogRow>>(listed.Data, out var rows))
                {
                    names.AddRange(rows.Select(row => row.CommandName));
                }
            }
            catch (Exception ex)
            {
                log.Log(ShellLogLevel.Warn, Source, "读取宿主命令目录失败: " + ex.Message);
            }
        }

        return names
            .Where(name => name.EndsWith(DescribeSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(name => name[..^DescribeSuffix.Length])
            .Where(domain => domain.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(domain => domain, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

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
        var parsed = PageDescriptionReader.Read(payload, ExpectedOwner(domain));
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
            rendered = PageRenderer.Render(page, new PageRenderContext { Bus = bus, Log = log, Owner = owner });
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
                ContentFactory = () => rendered.Root,
            }, owner);
        }
        catch (Exception ex)
        {
            log.Log(ShellLogLevel.Warn, Source, $"{owner}: 页面 {page.Id} 注册失败: {ex.Message}");
            return false;
        }

        return true;
    }

    private void Drop(string owner)
    {
        if (!_owners.Remove(owner))
            return;
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

    /// <summary>域名反推模块名，用于 owner 校验：mercury → HistoryMercury。</summary>
    private static string ExpectedOwner(string domain) => "History" + Capitalize(domain);

    private static string Capitalize(string value)
        => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

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
