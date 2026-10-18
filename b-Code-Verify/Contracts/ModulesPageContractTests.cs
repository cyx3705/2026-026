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
///   <item>三个动作齐全：刷新设备以及模块 / 刷新设备 / 打开发现根；</item>
///   <item>页面上没有指令清单——那是命令集页的事，两页曾经混在一起过；</item>
///   <item>装载列走选择弹窗和 <c>vulcan.node.place</c>，**不得**直接开 WPF 的
///         <c>OpenFolderDialog</c> / <c>OpenFileDialog</c>。</item>
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

        Assert.Equal(["modules.reload", "modules.refreshdevices", "modules.opendir"], actions);

        // 表只有一张，列里没有任何指令清单——指令归命令集页。
        var table = children.Single(child => child.GetProperty("type").GetString() == "table");
        var columns = table.GetProperty("columns")
            .EnumerateArray()
            .Select(column => column.GetProperty("key").GetString())
            .ToList();
        Assert.Equal(["module", "version", "load", "description"], columns);

        var load = table.GetProperty("columns").EnumerateArray()
            .Single(column => column.GetProperty("key").GetString() == "load");
        Assert.Equal("modules.place", load.GetProperty("cellAction").GetString());
        Assert.Equal("button", load.GetProperty("cellStyle").GetString());
        Assert.Contains(HostedPageData.Actions, action => action.Id == "modules.place"
                                                          && action.Command == "aurora.module.place"
                                                          && action.Args!["name"] == "{module}");
    }

    [Fact]
    public void DeviceRefreshUsesTheLocalNodeCommandAndNotAWpfDialog()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "b-Code-Studio", "Shell", "3-HostedPages", "Views", "HostedPageData.cs"));

        Assert.Contains("vulcan.node.refresh", source, StringComparison.Ordinal);
        Assert.Contains("aurora.ui.dialog kind=choice", source, StringComparison.Ordinal);
        Assert.Contains("vulcan.node.place module=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("aurora.module.hotreload", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenFolderDialog", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenFileDialog", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// 装着的模块按归属显示设备名；界面自己那一格是空的；
    /// 归属里有、本机没装上的模块留在表上，写成不装载或对端。
    /// </summary>
    [Fact]
    public void ModuleRowsNameTheDeviceOrStayUnloaded()
    {
        var aurora = new ModuleMeta("HistoryAurora", "前端", "", "1.35.1", false, "HistoryAurora.dll", 63);
        var janus = new ModuleMeta("HistoryJanus", "项目治理", "", "5.15.0", false, "HistoryJanus.dll", 54);
        var devices = new NodeDeviceInfo[]
        {
            new("local-id", "本机", "8.0.0", true, true),
            new("peer-id", "对端", "8.0.0", true, false),
        };
        var placements = new ModulePlacement[]
        {
            new("HistoryJanus", "local-id", false),
            new("HistoryJuno", null, false),
        };

        var rows = HostedPageData.ModuleRows([aurora, janus], placements, devices);
        Assert.Equal(["HistoryAurora", "HistoryJanus", "HistoryJuno"], rows.Select(row => row["module"]));
        Assert.Equal(["", "本机", "不装载"], rows.Select(row => row["load"]));
        Assert.StartsWith("（未在本机运行）", rows[2]["description"], StringComparison.Ordinal);

        placements =
        [
            new ModulePlacement("HistoryJanus", "local-id", false),
            new ModulePlacement("HistoryJuno", "peer-id", false),
        ];
        var juno = new ModuleMeta("HistoryJuno", "面板", "", "0.5.1", false, "HistoryJuno.dll", 10);
        rows = HostedPageData.ModuleRows([aurora, janus, juno], placements, devices);
        Assert.Equal(3, rows.Count);
        Assert.Equal("对端", rows.Single(row => row["module"] == "HistoryJuno")["load"]);
    }

    [Fact]
    public void PlaceChoosesADeviceAndRefusesToMoveAurora()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "b-Code-Studio", "Shell", "3-HostedPages", "Views", "HostedPageData.cs"));

        Assert.Contains("vulcan.node.devices", source, StringComparison.Ordinal);
        Assert.Contains("value = \"none\"", source, StringComparison.Ordinal);
        Assert.Contains("name.Equals(SelfModule", source, StringComparison.Ordinal);
        Assert.DoesNotContain("vulcan.module.reload", source, StringComparison.Ordinal);
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
