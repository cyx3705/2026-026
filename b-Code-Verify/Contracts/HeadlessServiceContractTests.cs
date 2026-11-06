using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using HistoryAurora.Module;
using HistoryAurora.Shell.Neutral.Logging;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
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
        "aurora.ui.hostedactions",
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

    /// <summary>转给网页的与模块自持的不重叠；合起来是原先界面登记的 66 条（去掉两条窗口指令）加 2.1.1 的 hostedactions。</summary>
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
        Assert.Equal(67, names.Count + ModuleOwned.Length);
    }

    /// <summary>
    /// 2.1.1 回归：Aurora 原生页面引用的每个动作 id 都必须由 aurora.ui.hostedactions 交出，
    /// 否则网页装配原生页面时整份报「Aurora 缺少动作」（2.1.0 删 WPF 后就是这样）。
    /// </summary>
    [Fact]
    public void EveryActionTheNativePagesReferenceIsDeclared()
    {
        using var document = JsonDocument.Parse(HistoryAurora.Shell.HostedPages.Views.HostedPageDescriptions.Json);
        var required = new HashSet<string>(StringComparer.Ordinal) { "preview.node" };
        void Collect(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in node.EnumerateObject())
                {
                    if (property.Name is "action" or "cellAction" or "cellEditAction" && property.Value.ValueKind == JsonValueKind.String)
                        required.Add(property.Value.GetString()!);
                    else
                        Collect(property.Value);
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in node.EnumerateArray())
                    Collect(item);
            }
        }
        Collect(document.RootElement);

        var declared = AuroraCommands.HostedActions.Select(action => action.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(required.Where(id => !declared.Contains(id)).Order());
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
        Assert.Contains("log.snapshot", link, StringComparison.Ordinal);
        var process = File.ReadAllText(Path.Combine(RepositoryRoot(), "b-Code-Studio", "Module", "WebProcess.cs"));
        Assert.Contains("log.snapshot", process, StringComparison.Ordinal);
        Assert.Contains("QuietCommand", process, StringComparison.Ordinal);
    }

    /// <summary>读日志的回显和成功回执不能再出现在下一次快照里，否则控制台会自己刷个不停。</summary>
    [Fact]
    public void LogSnapshotOmitsItsOwnReadback()
    {
        var log = new MemoryShellLog();
        log.Log(ShellLogLevel.Info, "app", "真实日志");
        log.Log(ShellLogLevel.Info, "cmd:module:HistoryPortunus:Shell:session:AuroraWeb", "aurora.log.snapshot minlevel=trace limit=200 after=1");
        log.Log(ShellLogLevel.Info, "cmd:result:aurora:log", "✓ 控制台日志匹配 2 条，返回 2 条");
        log.Log(ShellLogLevel.Info, "cmd:result:aurora:ui", "✓ 已打开");

        var snapshot = log.ReadSnapshot(new ConsoleLogQuery(ShellLogLevel.Trace, null, null, null, null, 100));

        Assert.Equal(2, snapshot.MatchedCount);
        Assert.Equal(["✓ 已打开", "真实日志"], snapshot.Entries.Select(entry => entry.Message).ToArray());
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

    /// <summary>
    /// REQ-SHELL-001：壳是消费网页的中间件，越少更新越好。它不引用 Aurora、宿主或任何本地工程，
    /// 包依赖只有 MAUI 本身；源码里也不出现 aurora.* 指令名——功能要进网页，不进壳。
    /// </summary>
    [Fact]
    public void ShellIsAPureConsumer()
    {
        var shell = Path.Combine(RepositoryRoot(), "b-Code-Shell", "AuroraShell");
        var project = System.Xml.Linq.XDocument.Load(Path.Combine(shell, "AuroraShell.csproj"));
        Assert.Empty(project.Descendants("ProjectReference"));
        Assert.Empty(project.Descendants("Reference"));
        Assert.All(project.Descendants("PackageReference"),
            package => Assert.StartsWith("Microsoft.Maui.", (string?)package.Attribute("Include") ?? "", StringComparison.Ordinal));

        var sources = Directory.GetFiles(shell, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        Assert.All(sources, path =>
        {
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("aurora.", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("HistoryVulcan", text, StringComparison.Ordinal);
            Assert.DoesNotContain("HistoryAurora", text, StringComparison.Ordinal);
        });
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
