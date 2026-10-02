using System.IO;
using System.Text.Json;
using HistoryAurora.Shell.HostedPages.Views;
using HistoryVulcan.Core.Commands;
using Xunit;
using HistoryAurora.Shell.Neutral.CommandSurface;
using HistoryAurora.Shell.Neutral.Commands;

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
        Assert.Equal(["module", "version", "load", "description"], columns);

        // 1.30.0（REQ-UI-137）：第三列是载入载出按钮，替换原来的「域指令」条数。
        var load = table.GetProperty("columns").EnumerateArray()
            .Single(column => column.GetProperty("key").GetString() == "load");
        Assert.Equal("modules.toggle", load.GetProperty("cellAction").GetString());
        Assert.Equal("button", load.GetProperty("cellStyle").GetString());
        Assert.Contains(HostedPageData.Actions, action => action.Id == "modules.toggle"
                                                          && action.Command == "aurora.module.toggle"
                                                          && action.Args!["name"] == "{module}");
    }

    [Fact]
    public void HotReloadGoesThroughTheShellDirectoryPickerNotAWpfDialog()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "b-Code-Studio", "Shell", "3-HostedPages", "Views", "HostedPageData.cs"));

        Assert.Contains("aurora.ui.selectdirectory", source, StringComparison.Ordinal);
        Assert.Contains("vulcan.module.install path=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenFolderDialog", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenFileDialog", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// REQ-UI-137：装着的模块按钮写「载出」，Aurora 自己那一格是空的（界面不能载出自己）；
    /// 载出过的模块留在表上写「载入」；它在宿主清单里重新出现时从台账划掉，不会出现两行。
    /// </summary>
    [Fact]
    public void ModuleRowsCarryALoadToggleAndKeepUnloadedModules()
    {
        var aurora = new ModuleMeta("HistoryAurora", "前端", "", "1.30.0", false, "HistoryAurora.dll", 63);
        var janus = new ModuleMeta("HistoryJanus", "项目治理", "", "5.15.0", false, "HistoryJanus.dll", 54);
        var juno = new ModuleMeta("HistoryJuno", "面板", "", "0.5.1", false, "HistoryJuno.dll", 10);
        var ledger = new ModuleUnloadLedger();
        ledger.Remember(juno);

        var rows = HostedPageData.ModuleRows([aurora, janus], ledger);
        Assert.Equal(["HistoryAurora", "HistoryJanus", "HistoryJuno"], rows.Select(row => row["module"]));
        Assert.Equal(["", "载出", "载入"], rows.Select(row => row["load"]));
        Assert.StartsWith("（已载出）", rows[2]["description"], StringComparison.Ordinal);

        rows = HostedPageData.ModuleRows([aurora, janus, juno], ledger);
        Assert.Equal(3, rows.Count);
        Assert.Equal("载出", rows.Single(row => row["module"] == "HistoryJuno")["load"]);
        Assert.False(ledger.Contains("HistoryJuno"));
    }

    [Fact]
    public void TheToggleUnloadsWithHostCommandsAndRefusesToUnloadAurora()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "b-Code-Studio", "Shell", "3-HostedPages", "Views", "HostedPageData.cs"));

        Assert.Contains("vulcan.module.unload name=", source, StringComparison.Ordinal);
        Assert.Contains("vulcan.module.reload", source, StringComparison.Ordinal);
        Assert.Contains("name.Equals(SelfModule", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FileAndDirectoryPickersRestoreTheProcessWorkingDirectory()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "b-Code-Studio", "Shell", "4-Composition", "BuiltinCommands.Panel.cs"));

        Assert.Contains("new Microsoft.Win32.OpenFileDialog { RestoreDirectory = true }", source, StringComparison.Ordinal);
        Assert.Contains("new Microsoft.Win32.OpenFolderDialog()", source, StringComparison.Ordinal);
        Assert.Contains("Environment.CurrentDirectory = cwd", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// 1.29.0：界面指令原样登记进宿主，由宿主经前端的界面上下文编组（异步 Post，已在界面线程上就地执行），
    /// 界面不再逐字段抄代理描述符、也不再自己同步等 Dispatcher。
    /// </summary>
    [Fact]
    public void ShellCommandsAreRegisteredAsIsAndMarshalledByTheHost()
    {
        var host = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "b-Code-Studio", "Module", "AuroraShellHost.cs"));
        var frontend = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "b-Code-Studio", "Module", "AuroraFrontend.cs"));

        Assert.Contains("registrar.Register(descriptor)", host, StringComparison.Ordinal);
        Assert.DoesNotContain("Marshalled(", host, StringComparison.Ordinal);
        Assert.DoesNotContain("source.Handler(context)", host, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoteExecutor", host, StringComparison.Ordinal);
        Assert.Contains("new DispatcherSynchronizationContext(window.Dispatcher)", frontend, StringComparison.Ordinal);
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
