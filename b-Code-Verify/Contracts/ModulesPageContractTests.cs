using System.IO;
using System.Text.Json;
using HistoryAurora.Shell.Views;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Services.Modules;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 模块管理页的形态契约（自宿主 ModuleCatalogSnapshotTests 迁入，REQ-A8）。
///
/// 1.9.0 之前这条用例读 <c>ModulesView.xaml</c> 的源文件做 XML 断言。那一页现在是
/// 描述式的（REQ-UI-052），XAML 不存在了，但**它守的三件事一件没变**：
///
/// <list type="number">
///   <item>三个动作齐全：刷新模块 / 热重载 / 打开发现根；</item>
///   <item>页面上没有指令清单——那是命令集页的事，两页曾经混在一起过；</item>
///   <item>选目录走 <c>aurora.ui.selectdirectory</c>，**不得**直接开 WPF 的
///         <c>OpenFolderDialog</c> / <c>OpenFileDialog</c>。这一条尤其要守：
///         直接开对话框的版本在宿主进程里能跑，装进模块之后就会因为线程与
///         所有者窗口不对而挂住整个界面。</item>
/// </list>
/// </summary>
public sealed class ModulesPageContractTests
{
    [Fact]
    public void ModulesPageDeclaresThreeActionsAndNoCommandList()
    {
        using var document = JsonDocument.Parse(HostedPageDescriptions.Json);
        var page = document.RootElement
            .GetProperty("pages")
            .EnumerateArray()
            .Single(candidate => candidate.GetProperty("id").GetString() == "modules");

        var children = page.GetProperty("content").GetProperty("children").EnumerateArray().ToList();
        var panel = children.Single(child => child.GetProperty("type").GetString() == "panel");
        var actions = panel.GetProperty("rows")
            .EnumerateArray()
            .SelectMany(row => row.GetProperty("widgets").EnumerateArray())
            .Where(widget => widget.GetProperty("kind").GetString() == "button")
            .Select(widget => widget.GetProperty("action").GetString())
            .ToList();

        Assert.Equal(["modules.reload", "modules.hotreload", "modules.opendir"], actions);

        // 表只有一张，列里没有任何指令清单——指令归命令集页。
        var table = children.Single(child => child.GetProperty("type").GetString() == "table");
        var columns = table.GetProperty("columns")
            .EnumerateArray()
            .Select(column => column.GetProperty("key").GetString())
            .ToList();
        Assert.Equal(["module", "version", "commands", "description"], columns);
    }

    [Fact]
    public void HotReloadGoesThroughTheShellDirectoryPickerNotAWpfDialog()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "b-Code-Studio", "Shell", "Views", "HostedPageData.cs"));

        Assert.Contains("aurora.ui.selectdirectory", source, StringComparison.Ordinal);
        Assert.Contains("vulcan.module.install path=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenFolderDialog", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenFileDialog", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// 「域指令」一列统计的是**该域当前注册的指令总数**，不是经模块路径注册的条数。
    ///
    /// 二者对四个业务模块相等，对 HistoryAurora 却差得很远：它是应用，
    /// aurora.* 由应用进程自持并上报，经模块路径注册的是 0 条（DEC-007）。
    /// 显示 0 会让人以为它坏了——这条注释此前挂在 ModulesView 上，随实现一起搬过来。
    /// </summary>
    [Fact]
    public void TheCommandCountColumnCountsTheWholeDomain()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "b-Code-Studio", "Shell", "Views", "HostedPageData.cs"));

        Assert.Contains("DomainCommandCount", source, StringComparison.Ordinal);
        Assert.Contains("ModuleDomainNaming.ToDomain", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCommandCountFallsBackToTheHostModuleSnapshot()
    {
        // The embedded Aurora UI has a separate registry. A module's commands are
        // therefore invisible locally, but the host module snapshot still carries
        // the finalized count from the registration pass.
        var local = new CommandRegistry();
        var module = new ModuleMeta(
            "HistoryJanus", "", "", "5.4.8", false, "HistoryJanus.dll", 41);

        Assert.Equal(41, HostedPageData.DomainCommandCount(local, module));
    }

    [Fact]
    public void FileAndDirectoryPickersRestoreTheProcessWorkingDirectory()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "b-Code-Studio", "Shell", "BuiltinCommands.Panel.cs"));

        Assert.Contains("new Microsoft.Win32.OpenFileDialog { RestoreDirectory = true }", source, StringComparison.Ordinal);
        Assert.Contains("new Microsoft.Win32.OpenFolderDialog()", source, StringComparison.Ordinal);
        Assert.Contains("Environment.CurrentDirectory = cwd", source, StringComparison.Ordinal);
    }

    [Fact]
    public void HostMarshalledUiCommandsDoNotSyncWaitOnTheUiDispatcher()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "b-Code-Studio", "Module", "AuroraShellHost.cs"));

        Assert.Contains("window.Dispatcher.CheckAccess()", source, StringComparison.Ordinal);
        Assert.Contains("window.Dispatcher.InvokeAsync(() => source.Handler(context))", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Handler = context => window.Dispatcher.Invoke(() => source.Handler(context))",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>向上找到含 project.manifest.json 的目录，即仓库根。</summary>
    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "project.manifest.json")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("未找到 HistoryAurora 仓库根目录");
    }
}
