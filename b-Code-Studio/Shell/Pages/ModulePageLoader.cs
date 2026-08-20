using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Logging;

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
public sealed class ModulePageLoader(CommandBus bus, IDockingService docking, IShellLog log)
{
    private const string Source = "page";

    /// <summary>协议约定的描述命令后缀；模块以自己的域注册，例如 mercury.ui.describe。</summary>
    public const string DescribeSuffix = ".ui.describe";

    private readonly List<MissingComponent> _missing = [];
    private readonly HashSet<string> _owners = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>最近一次拉取发现的缺件，供 aurora.ui.missing 查询。</summary>
    public IReadOnlyList<MissingComponent> Missing => _missing.ToList();

    /// <summary>全量重拉：先撤掉本机制注册过的全部页面，再逐个模块问一遍。</summary>
    public async Task<PageLoadReport> ReloadAsync(CancellationToken cancellation = default)
    {
        foreach (var owner in _owners.ToList())
            Drop(owner);
        _missing.Clear();

        var owners = DescribableOwners();
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
    private List<string> DescribableOwners()
        => bus.Registry.All()
            .Select(d => d.Name)
            .Where(name => name.EndsWith(DescribeSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(name => name[..^DescribeSuffix.Length])
            .Where(domain => domain.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(domain => domain, StringComparer.OrdinalIgnoreCase)
            .ToList();

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
            _missing.Add(new MissingComponent(owner, page.Id, component));

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
