using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;

namespace HistoryAurora.Module;

/// <summary>
/// REQ-WEB-045：记住电脑壳在哪，<c>vulcan.app.show</c> 时拉起它（Mercury 的坞按钮与快捷键都走这条）。
/// </summary>
/// <remarks>
/// <para>壳不进模块包，也不装到固定位置：壳里的网页连上推送通道时把壳自己的路径报上来，这里记进数据目录的
/// <c>shell.json</c>。从没开过壳就没有记录，show 照旧开系统浏览器。</para>
/// <para>壳是单实例的：已经开着时再拉起一次，新进程只把旧窗口带到前台就退出。所以 show 不必区分开没开着。
/// 浏览器标签做不到这一点——网页的 <c>window.focus()</c> 不能把自己带到前台，这正是 2.1.1 之前 Mercury 唤起无反应的原因。</para>
/// </remarks>
internal sealed class ShellLauncher(string dataDirectory, IModuleLog log)
{
    /// <summary>只认这个文件名：路径是网页报上来的，不能让它变成「随便启动一个程序」。</summary>
    internal const string ExecutableName = "AuroraShell.exe";

    private const int AnyProcess = -1;
    private readonly string _file = Path.Combine(dataDirectory, "shell.json");
    private readonly object _gate = new();

    /// <summary>记下壳的位置；不是存在的 AuroraShell.exe 就忽略。</summary>
    public void Remember(string path)
    {
        if (!IsShell(path))
        {
            log.Log(ShellLogLevel.Warn, "aurora.shell", $"忽略无效的壳路径：{path}");
            return;
        }

        lock (_gate)
        {
            if (string.Equals(Read(), path, StringComparison.OrdinalIgnoreCase))
                return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                File.WriteAllText(_file, JsonSerializer.Serialize(new { path }));
                log.Log(ShellLogLevel.Info, "aurora.shell", $"已记下 Aurora 壳：{path}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Log(ShellLogLevel.Warn, "aurora.shell", $"记录壳路径失败：{ex.Message}");
            }
        }
    }

    /// <summary>拉起（或唤到前台）记下的壳；没有可用记录返回 false。</summary>
    public bool TryLaunch()
    {
        string? path;
        lock (_gate)
            path = Read();
        if (path == null || !IsShell(path))
            return false;

        try
        {
            // 调用方（Mercury 坞按钮/全局快捷键）刚拿到用户输入，宿主进程此刻有前台权；
            // 把它让给接下来的壳进程，壳才能把已开着的窗口带到前台。
            AllowSetForegroundWindow(AnyProcess);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)! })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.Log(ShellLogLevel.Warn, "aurora.shell", $"启动 Aurora 壳失败，改开浏览器：{ex.Message}");
            return false;
        }
    }

    internal static bool IsShell(string path)
        => Path.IsPathFullyQualified(path)
           && string.Equals(Path.GetFileName(path), ExecutableName, StringComparison.OrdinalIgnoreCase)
           && File.Exists(path);

    private string? Read()
    {
        try
        {
            if (!File.Exists(_file))
                return null;
            using var document = JsonDocument.Parse(File.ReadAllText(_file));
            return document.RootElement.TryGetProperty("path", out var path) ? path.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
