using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using HistoryAurora.Module;
using HistoryVulcan.Core.Logging;
using Xunit;

namespace HistoryAurora.Verify.ModuleSmoke;

/// <summary>
/// VERIFY-MODULE-LOAD：模块能被宿主发现、装载并取得宿主上下文；2.1.0 起再加 Aurora Web 子进程的真实往返。
/// </summary>
public sealed class ModuleLoadTests
{
    /// <summary>装配根接得住宿主上下文；宿主没来得及 Attach 就卸载时，Dispose 也不抛。</summary>
    [Fact]
    public void CompositionTakesTheHostContextAndDisposesWithoutAttach()
    {
        var composition = new AuroraBusinessComposition();

        Assert.IsAssignableFrom<HistoryVulcan.Core.Modules.IModuleContextAware>(composition);
        composition.Dispose();
    }

    /// <summary>VERIFY-HOST-CONTRACT：清单是宿主识别模块身份的唯一权威。2.1.0 起不再随包带任何界面库。</summary>
    [Fact]
    public void ManifestDeclaresHostModuleIdentity()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "module.manifest.json");
        Assert.True(File.Exists(manifestPath), $"module.manifest.json 未随产物输出: {manifestPath}");

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;

        Assert.Equal("HistoryVulcan.Module", root.GetProperty("type").GetString());
        Assert.Equal("HistoryAurora", root.GetProperty("name").GetString());
        Assert.Equal("HistoryAurora.dll", root.GetProperty("artifact").GetString());
        Assert.Equal("frontend", root.GetProperty("role").GetString());
        // ui=true 仍要：宿主据此只在有桌面运行时的执行目标里装它（它要拉起子进程、开浏览器）。
        Assert.True(root.GetProperty("ui").GetBoolean());
        // 可热重载：pinned 缺省即 false；写明是为了防止再被钉回 Default 而锁死 DLL。
        Assert.False(root.GetProperty("pinned").GetBoolean());
        Assert.Empty(root.GetProperty("deps").EnumerateArray());
    }

    /// <summary>模块在可回收上下文里装得上，内嵌的界面指令清单读得出来。</summary>
    [Fact]
    public void ModuleLoadsInACollectibleContextAndReadsItsCommandManifest()
    {
        var modulePath = Path.Combine(AppContext.BaseDirectory, "HistoryAurora.dll");
        Assert.True(File.Exists(modulePath), $"HistoryAurora.dll 不存在: {modulePath}");

        var context = new ModuleProbeLoadContext(modulePath);
        var assembly = context.LoadFromAssemblyPath(modulePath);
        Assert.True(assembly.IsCollectible);
        using (var stream = assembly.GetManifestResourceStream(AuroraCommands.ManifestResource))
        {
            Assert.NotNull(stream);
            using var document = JsonDocument.Parse(stream!);
            Assert.True(document.RootElement.GetArrayLength() > 40);
        }
        context.Unload();
    }

    /// <summary>
    /// 真起一个 Aurora Web 子进程：标准输入输出协议能往返；网页本体从内嵌资源送出；
    /// 没有网页连着时界面指令明确失败而不是挂住；Dispose 后子进程退出、端口释放。
    /// </summary>
    [Fact]
    public async Task WebChildProcessAnswersOverStdioAndExitsOnDispose()
    {
        var executable = Path.Combine(RepositoryRoot(), "b-Code-Studio", "Module", "bin", "Release", "net8.0-windows", "web", "HistoryAurora.Web.exe");
        Assert.True(File.Exists(executable), $"模块构建没有带出 Aurora Web：{executable}");
        var port = FreePort();
        var log = new RecordingLog();

        using (var web = new WebProcess(executable, log, port))
        {
            web.EnsureStarted();
            Assert.True(await web.WaitReadyAsync(TimeSpan.FromSeconds(30)), "Aurora Web 未就绪：" + string.Join(" | ", log.Lines));

            using (var http = new HttpClient())
            {
                var page = await http.GetStringAsync(web.Url);
                Assert.Contains("<html", page, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("frontend-link", await http.GetStringAsync(web.Url + "app.js"), StringComparison.Ordinal);
                // REQ-WEB-043：热装后网页要立刻换新，静态文件一律不缓存。
                using var script = await http.GetAsync(web.Url + "app.js");
                Assert.True(script.Headers.CacheControl?.NoStore, "app.js 必须带 Cache-Control: no-store");
            }

            var result = await web.InvokeAsync("aurora.ui.dialog",
                new Dictionary<string, string> { ["kind"] = "confirm", ["body"] = "冒烟" }, CancellationToken.None);
            Assert.False(result.Success);
            Assert.Contains("没有打开的 Aurora 网页", result.Message, StringComparison.Ordinal);

            // REQ-WEB-045：壳里的网页连上时把壳路径经子进程报给模块，模块记下、只认 AuroraShell.exe。
            var data = Directory.CreateTempSubdirectory("aurora-shell-");
            try
            {
                var fake = Path.Combine(data.FullName, ShellLauncher.ExecutableName);
                File.WriteAllText(fake, "");
                var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                web.ShellSeen += path => seen.TrySetResult(path);
                using var http = new HttpClient();
                http.DefaultRequestHeaders.Add("X-Aurora-Web", "1");
                using var stream = await http.GetAsync(
                    $"{web.Url}api/events?client=shellsmoke01&kind=shell&launch={Uri.EscapeDataString(fake)}",
                    HttpCompletionOption.ResponseHeadersRead);
                Assert.Same(seen.Task, await Task.WhenAny(seen.Task, Task.Delay(10000)));
                var reported = await seen.Task;
                Assert.Equal(fake, reported);
                Assert.True(await web.WaitClientAsync(TimeSpan.FromSeconds(5)));

                var launcher = new ShellLauncher(data.FullName, log);
                launcher.Remember(Path.Combine(data.FullName, "notepad.exe"));
                Assert.False(File.Exists(Path.Combine(data.FullName, "shell.json")), "非 AuroraShell.exe 的路径不能被记下");
                launcher.Remember(reported);
                Assert.Contains(fake.Replace("\\", "\\\\"), File.ReadAllText(Path.Combine(data.FullName, "shell.json")), StringComparison.Ordinal);
            }
            finally
            {
                data.Delete(recursive: true);
            }
        }

        using var probe = new TcpListener(IPAddress.Loopback, port);
        probe.Start();
        probe.Stop();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
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

    private sealed class RecordingLog : IModuleLog
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public void Log(ShellLogLevel level, string category, string message) => Lines.Enqueue($"{level} {category} {message}");
    }

    private sealed class ModuleProbeLoadContext(string modulePath)
        : AssemblyLoadContext(isCollectible: true)
    {
        private readonly string _directory = Path.GetDirectoryName(modulePath)!;

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name?.StartsWith("HistoryVulcan.", StringComparison.Ordinal) == true)
            {
                return Default.Assemblies.FirstOrDefault(item =>
                    AssemblyName.ReferenceMatchesDefinition(item.GetName(), assemblyName));
            }

            var candidate = Path.Combine(_directory, assemblyName.Name + ".dll");
            return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
        }
    }
}
