using HistoryVulcan.Services.Modules;
using System.Windows.Controls;
using HistoryVulcan.Core.Commands;

namespace HistoryAurora.Shell.Views;

/// <summary>
/// 模块管理页(V2.1.1):已装载模块清单。
/// 数据经 vulcan.module.list 消费(Data = ModuleMeta 列表);动作按钮全部经总线
/// (vulcan.module.reload / vulcan.module.install / vulcan.module.open)。命令详情统一由命令集页面提供。
/// </summary>
public partial class ModulesView : UserControl
{
    private readonly Func<CommandBus?> _busAccessor;
    private bool _initialLoadDone;

    public ModulesView(Func<CommandBus?> busAccessor)
    {
        InitializeComponent();
        _busAccessor = busAccessor;
        // 0.4.4 上抛框架:内联首次加载守卫,不再依赖 App 层 ViewKit(停靠重排会反复触发 Loaded)
        Loaded += async (_, _) =>
        {
            if (_initialLoadDone)
                return;
            _initialLoadDone = true;
            await RefreshAsync();
            // 热重载曾在 Show 时快照仍空；若这一帧仍是空列表，等闲时再读一次。
            if (ModuleList.ItemsSource is System.Collections.ICollection { Count: > 0 })
                return;
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await RefreshAsync();
        };
    }

    /// <summary>
    /// <paramref name="DomainCommandCount"/> 是**该域当前注册的指令总数**，不是本模块经模块
    /// 路径注册的条数。二者对四个业务模块相等，对 HistoryAurora 却差得很远：Aurora 是应用，
    /// 它的 40 条 aurora.* 由应用进程自持并上报（来源 frontend:*），经模块路径注册的是 0 条
    /// （DEC-007，ModuleInfo.MainClassType = null）。列里显示 0 会让人以为它坏了。
    /// </summary>
    public sealed record ModuleRow(
        string ModuleName, string Version, string Mode, int DomainCommandCount,
        string AssemblyFile, string Description);

    /// <summary>按域统计当前注册表里的指令条数；域名取自模块名（History 前缀剥离）。</summary>
    private static int DomainCommandCount(CommandRegistry registry, string moduleName)
    {
        var domain = ModuleDomainNaming.ToDomain(moduleName);
        if (domain.Length == 0)
            return 0;
        return registry.All().Count(descriptor =>
            string.Equals(DomainOf(descriptor), domain, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>描述符未声明域时按指令名首段兜底，与注册表的归一化口径一致。</summary>
    private static string DomainOf(CommandDescriptor descriptor)
    {
        if (!string.IsNullOrWhiteSpace(descriptor.Domain))
            return descriptor.Domain;
        var separator = descriptor.Name.IndexOf('.');
        return separator > 0 ? descriptor.Name[..separator] : descriptor.Name;
    }

    private async void OnReloadClick(object sender, System.Windows.RoutedEventArgs e)
    {
        var bus = _busAccessor();
        if (bus == null)
            return;
        RefreshButton.IsEnabled = false;
        try
        {
            var result = await bus.ExecuteAsync("vulcan.module.reload", "UI");
            if (!result.Success)
            {
                ClearModules("模块重载失败: " + FirstLine(result.Message));
                return;
            }
            await RefreshAsync();
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private async void OnHotReloadClick(object sender, System.Windows.RoutedEventArgs e)
    {
        var bus = _busAccessor();
        if (bus == null)
        {
            StatusText.Text = "命令总线尚未就绪";
            return;
        }

        HotReloadButton.IsEnabled = false;
        try
        {
            var picked = await bus.ExecuteAsync("aurora.ui.selectdirectory", "UI");
            if (!picked.Success)
            {
                StatusText.Text = FirstLine(picked.Message);
                return;
            }

            if (!CommandResultData.TryRead<string>(picked.Data, out var path)
                || string.IsNullOrWhiteSpace(path))
            {
                StatusText.Text = "已取消热重载";
                return;
            }

            var result = await bus.ExecuteAsync(
                $"vulcan.module.install path={CommandParser.QuoteArg(path)}",
                "UI");
            if (!result.Success)
            {
                StatusText.Text = "热重载失败: " + FirstLine(result.Message)
                    + "。请再选主树候选或 z-Publish/history/HistoryX-vX.Y.Z，不会自动恢复。";
                return;
            }

            await RefreshAsync();
            StatusText.Text = FirstLine(result.Message);
        }
        finally
        {
            HotReloadButton.IsEnabled = true;
        }
    }

    private async void OnOpenDirClick(object sender, System.Windows.RoutedEventArgs e)
    {
        var bus = _busAccessor();
        if (bus == null)
        {
            StatusText.Text = "命令总线尚未就绪";
            return;
        }

        OpenDirButton.IsEnabled = false;
        try
        {
            var result = await bus.ExecuteAsync("vulcan.module.open", "UI");
            if (!result.Success)
                StatusText.Text = FirstLine(result.Message);
        }
        finally
        {
            OpenDirButton.IsEnabled = true;
        }
    }

    private async Task RefreshAsync()
    {
        var bus = _busAccessor();
        if (bus == null)
        {
            ClearModules("命令总线尚未就绪");
            return;
        }

        RefreshButton.IsEnabled = false;
        ClearModules("正在读取运行区模块...");
        try
        {
            var result = await ModuleCatalogReader.LoadModulesAsync(bus);
            if (result.Success && result.Snapshot is { } snapshot)
            {
                var rows = snapshot.Modules.Select(m => new ModuleRow(
                        m.ModuleName, m.Version, m.Open ? "全暴露" : "精准暴露",
                        DomainCommandCount(bus.Registry, m.ModuleName), m.AssemblyFile, m.Description))
                    .ToList();
                ModuleList.ItemsSource = rows;
                StatusText.Text = rows.Count == 0
                    ? "当前无已装载模块；运行区为空或包未通过校验"
                    : $"已装载 {rows.Count} 个模块,合计 {rows.Sum(row => row.DomainCommandCount)} 条域指令"
                      + $"（其中经模块路径注册 {snapshot.Modules.Sum(module => module.CommandCount)} 条）";
            }
            else
            {
                ClearModules(result.Message);
            }
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void ClearModules(string status)
    {
        ModuleList.ItemsSource = null;
        StatusText.Text = status;
    }

    private static string FirstLine(string value)
        => value.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? value;
}
