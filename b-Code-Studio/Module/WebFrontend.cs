using System.Diagnostics;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;

namespace HistoryAurora.Module;

/// <summary>
/// Aurora 交给宿主的唯一前端（2.1.0 起没有窗口）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>确认：推一个 confirm 弹窗给最近活跃的网页；没有网页时按宿主缺省拒绝。</item>
/// <item><c>vulcan.app.show / focusconsole</c>：有网页就让它到前台，没有就用系统浏览器打开地址。</item>
/// <item><c>vulcan.app.hide / close</c>：网页窗口归浏览器管，这里只回成功不关页面。</item>
/// <item><see cref="UiContext"/>：仍是一条 STA 线程。别的模块（Minerva 工作窗等）声明 RequiresUiThread
/// 的指令要在 STA 上建自己的 WPF 窗口，宿主把它们编组到这里。</item>
/// </list>
/// </remarks>
internal sealed class WebFrontend(SynchronizationContext uiContext, WebProcess web, IModuleLog log) : IFrontend
{
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromMinutes(10);

    public SynchronizationContext UiContext { get; } = uiContext;

    public bool Confirm(string prompt)
    {
        if (web.ClientCount == 0)
        {
            log.Log(ShellLogLevel.Warn, "aurora", $"没有打开的 Aurora 网页，确认按拒绝处理：{prompt}");
            return false;
        }

        using var timeout = new CancellationTokenSource(ConfirmTimeout);
        var args = new Dictionary<string, string>
        {
            ["kind"] = "confirm",
            ["title"] = "确认",
            ["body"] = prompt,
        };
        // 不能在调用线程上等一个需要它自己的续体：InvokeAsync 全程 ConfigureAwait(false)，这里阻塞安全。
        var result = Task.Run(() => web.InvokeAsync("aurora.ui.dialog", args, timeout.Token)).GetAwaiter().GetResult();
        return result.Success;
    }

    public async Task<CommandResult> ExecuteAsync(string commandName, string source, CancellationToken cancellation)
    {
        if (commandName.Equals("vulcan.app.show", StringComparison.OrdinalIgnoreCase)
            || commandName.Equals("vulcan.app.focusconsole", StringComparison.OrdinalIgnoreCase))
        {
            web.EnsureStarted();
            if (web.ClientCount > 0)
                return await web.InvokeAsync(commandName.ToLowerInvariant(), new Dictionary<string, string>(), cancellation)
                    .ConfigureAwait(false);
            if (!await web.WaitReadyAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false))
                return CommandResult.Fail("Aurora Web 未就绪：端口可能被占用，详见宿主日志 aurora.web");
            return OpenBrowser(web.Url);
        }

        if (commandName.Equals("vulcan.app.hide", StringComparison.OrdinalIgnoreCase)
            || commandName.Equals("vulcan.app.close", StringComparison.OrdinalIgnoreCase))
            return CommandResult.Ok("Aurora 网页由浏览器管理，未关闭");

        // 不能转回总线：宿主正是经 IFrontend 把这条交过来的，再发回去就是一个来回弹的环。
        return CommandResult.Fail($"前端不处理 {commandName}");
    }

    /// <summary>用系统默认浏览器打开 Aurora。</summary>
    internal static CommandResult OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            return CommandResult.Ok($"已在浏览器打开 Aurora：{url}", new { url });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return CommandResult.Fail($"无法打开浏览器：{ex.Message}；请手动访问 {url}");
        }
    }
}
