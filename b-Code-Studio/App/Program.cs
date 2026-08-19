using System.Windows;

namespace HistoryAurora;

/// <summary>
/// Aurora 的进程入口（REQ-A7）。
///
/// 与迁出前的 <c>HistoryVulcan.App.Program</c> 相比少了两条分支：
/// <list type="bullet">
/// <item><c>--service</c> —— Aurora 不再承载服务角色。宿主是独立的无头进程
/// （REQ-A4），前端只作为 IPC 客户端连过去。</item>
/// <item><c>--export-command-manual</c> / <c>--repair-autostart</c> —— 都是宿主职责，
/// 随服务留在 HistoryVulcan.exe。前端没有权威注册表，导出的手册会是残缺的。</item>
/// </list>
///
/// 剩下的只有 <c>--focus-console</c>：宿主中继 <c>vulcan.app.focusconsole</c> 时，
/// 若前端未启动会带该参数把它拉起来，启动后执行同一条语义命令。
/// </summary>
internal static class Program
{
    internal const string FocusConsoleSwitch = "--focus-console";

    [STAThread]
    private static int Main(string[] args)
    {
        // 不要写 StartupUri = null —— WPF 的 setter 不接受 null，会抛 ArgumentNullException
        // 而且是在 App 的异常处理器装好之前，表现为进程静默退出、连日志目录都来不及建。
        // 主窗口由 App.OnStartup 自己创建并赋给 MainWindow，本就不需要 StartupUri。
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
