using System.IO;
using System.Xml.Linq;
using Xunit;

namespace HistoryAurora.Verify.Contracts;

/// <summary>
/// 模块管理页的形态契约（自宿主 ModuleCatalogSnapshotTests 迁入，REQ-A8）。
///
/// 它按路径读 ModulesView.xaml 的源文件做结构断言，因此路径随前端一起改到
/// `b-Code-Studio/Shell/Views`。宿主那份文件的另外两条用例测的是目录快照过滤与
/// 跨代计数，属 Services，留在宿主。
/// </summary>
public sealed class ModulesViewContractTests
{
    [Fact]
    public void ModulesViewAddsHotReloadWithoutCommandDetailPane()
    {
        var path = Path.Combine(
            RepositoryRoot(),
            "b-Code-Studio",
            "Shell",
            "Views",
            "ModulesView.xaml");
        var code = File.ReadAllText(path + ".cs");
        var document = XDocument.Load(path);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var elements = document.Descendants().ToList();
        var buttons = elements.Where(element => element.Name == presentation + "Button").ToList();

        Assert.Single(buttons, button => (string?)button.Attribute("Click") == "OnReloadClick");
        Assert.Single(buttons, button => (string?)button.Attribute("Click") == "OnHotReloadClick");
        Assert.Contains(buttons, button => (string?)button.Attribute("Content") == "刷新模块");
        Assert.Contains(buttons, button => (string?)button.Attribute("Content") == "热重载");
        Assert.Contains(buttons, button => (string?)button.Attribute("Content") == "打开发现根");
        Assert.DoesNotContain(buttons, button => (string?)button.Attribute("Click") == "OnRefreshClick");
        Assert.DoesNotContain(elements, element => (string?)element.Attribute(x + "Name") == "CommandList");
        Assert.DoesNotContain(elements, element => (string?)element.Attribute(x + "Name") == "CommandsTitle");
        Assert.Contains(buttons, button => (string?)button.Attribute(x + "Name") == "HotReloadButton");
        Assert.Contains(buttons, button => (string?)button.Attribute(x + "Name") == "OpenDirButton");
        Assert.Contains("aurora.ui.selectdirectory", code, StringComparison.Ordinal);
        Assert.Contains("vulcan.module.install path=", code, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenFolderDialog", code, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenFileDialog", code, StringComparison.Ordinal);
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
