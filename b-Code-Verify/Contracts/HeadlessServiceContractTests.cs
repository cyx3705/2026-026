using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using HistoryAurora.Module;
using HistoryVulcan.Core.Commands;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 2.1.0：Aurora 是无界面服务，界面指令转给网页（REQ-WEB-040/041/042）。
/// </summary>
public sealed class HeadlessServiceContractTests
{
    /// <summary>模块里自己实现、不转给网页的指令。</summary>
    private static readonly string[] ModuleOwned =
    [
        "aurora.log.snapshot", "aurora.ui.data", "aurora.command.runreadonly",
        "aurora.module.reloadall", "aurora.module.place", "aurora.module.refreshdevices",
        "aurora.component.list", "aurora.component.show",
        "aurora.preview.chat", "aurora.preview.echo", "aurora.preview.rows", "aurora.preview.graph",
        "aurora.ui.request", "aurora.ui.requests", "aurora.app.opendata", "aurora.host.ready",
    ];

    private static IReadOnlyList<CommandDescriptor> Forwarded()
        => AuroraCommands.ReadForwarded(_ => _ => Task.FromResult(CommandResult.Ok("")));

    /// <summary>转给网页的每一条，网页都必须有实现；否则宿主那边调到的是一句「网页不处理」。</summary>
    [Fact]
    public void EveryForwardedCommandIsImplementedByTheBrowser()
    {
        var web = Path.Combine(RepositoryRoot(), "b-Code-Web", "wwwroot");
        var scripts = string.Concat(Directory.GetFiles(web, "*.js").Select(File.ReadAllText));
        var consoleSettings = Regex.Match(File.ReadAllText(Path.Combine(web, "console-model.js")),
            @"const settings=\{(?<body>[^;]+)\};").Groups["body"].Value;
        var missing = Forwarded()
            .Select(descriptor => descriptor.Name)
            .Where(name => !scripts.Contains($"'{name}'", StringComparison.Ordinal))
            .Where(name => !(name.StartsWith("aurora.log.", StringComparison.Ordinal)
                && (name == "aurora.log.clear" || consoleSettings.Contains(name.Split('.')[2] + ":", StringComparison.Ordinal))))
            .ToList();
        Assert.Empty(missing);
    }

    /// <summary>转给网页的与模块自持的不重叠，合起来就是原先界面登记的那 66 条（去掉了两条窗口指令）。</summary>
    [Fact]
    public void ForwardedAndModuleOwnedCommandsPartitionTheDomain()
    {
        var forwarded = Forwarded();
        Assert.All(forwarded, descriptor =>
        {
            Assert.Equal("aurora", descriptor.Domain);
            Assert.False(descriptor.RequiresUiThread);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Summary));
        });
        var names = forwarded.Select(descriptor => descriptor.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(forwarded.Count, names.Count);
        Assert.Empty(names.Intersect(ModuleOwned, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain("aurora.app.window", names);
        Assert.DoesNotContain("aurora.ui.autohide", names);
        Assert.Equal(66, names.Count + ModuleOwned.Length);
    }

    /// <summary>允许未声明参数的指令（ui.invoke 的占位参数）要把全部参数原样交给网页。</summary>
    [Fact]
    public void UnspecifiedParametersReachTheBrowser()
    {
        var invoke = Forwarded().Single(descriptor => descriptor.Name == "aurora.ui.invoke");
        Assert.True(invoke.AllowUnspecifiedParameters);
        var context = new CommandContext(
            invoke,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["id"] = "rules.add", ["pattern"] = "*.png" },
            "test",
            null,
            CancellationToken.None);

        var args = AuroraCommands.ArgumentsOf(context);

        Assert.Equal("rules.add", args["id"]);
        Assert.Equal("*.png", args["pattern"]);
    }

    /// <summary>网页回执还原成宿主回执：成功带 data，失败只留文字。</summary>
    [Fact]
    public void BrowserRepliesBecomeHostResults()
    {
        var ok = WebProcess.ToResult(JsonSerializer.SerializeToElement(new { success = true, message = "已确认", data = "yes" }));
        Assert.True(ok.Success);
        Assert.Equal("已确认", ok.Message);
        Assert.Equal("yes", ((JsonElement)ok.Data!).GetString());

        var cancelled = WebProcess.ToResult(JsonSerializer.SerializeToElement(new { success = false, message = "已取消" }));
        Assert.False(cancelled.Success);
        Assert.Equal("已取消", cancelled.Message);
    }

    /// <summary>模块与子进程的协议两端写在两个工程里，前缀必须一致。</summary>
    [Fact]
    public void ParentLinkProtocolMatchesOnBothEnds()
    {
        var link = File.ReadAllText(Path.Combine(RepositoryRoot(), "b-Code-Web", "ParentLink.cs"));
        Assert.Contains($"public const string Prefix = \"{WebProcess.Prefix}\";", link, StringComparison.Ordinal);
        Assert.Contains("--parent-stdio", link, StringComparison.Ordinal);
    }

    /// <summary>包里带着 Web 子进程；没有 AvalonDock，也没有任何 XAML。</summary>
    [Fact]
    public void PackageCarriesTheWebServerAndNoDesktopShell()
    {
        var root = RepositoryRoot();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "project.manifest.json")));
        var files = manifest.RootElement.GetProperty("publish").GetProperty("package").GetProperty("files")
            .EnumerateArray().Select(file => file.GetString()).ToList();
        Assert.Contains("web/HistoryAurora.Web.exe", files);
        Assert.Contains("web/HistoryAurora.Web.dll", files);
        Assert.DoesNotContain(files, file => file!.Contains("AvalonDock", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            Directory.GetFiles(Path.Combine(root, "b-Code-Studio"), "*.xaml", SearchOption.AllDirectories),
            path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

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
