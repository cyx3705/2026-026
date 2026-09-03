using HistoryAurora.Module;
using HistoryAurora.Shell.Composition;
using Xunit;

namespace HistoryAurora.Verify.Contracts;

/// <summary>
/// 界面在宿主进程内启动时，不得再造一套宿主已经拥有的东西（DEC-008）。
///
/// 迁入前这条测的是"独立前端把 MCP 归属让给后台"。界面回归模块形态后两者同处一个进程，
/// 问题从"路由给谁"变成"会不会开出第二份"——而第二份的失败方式都是静默的：
/// 第二个 MCP 网关去抢端口，第二个 ModuleHost 把每个模块装载两遍。
///
/// 原先那条 `BackendMcpSettingCommandsAreRoutedToTheService` 一并退役：
/// 它测的是 IPC 客户端的路由判据，而进程内已经没有 IPC 客户端。
/// </summary>
public sealed class FrontendMcpOwnershipTests
{
    /// <summary>
    /// 界面不得能够创建 MCP 网关——不是"默认不创建"，是**没有这个能力**。
    /// </summary>
    /// <remarks>
    /// 断言从 <c>Assert.False(config.EnableMcp)</c> 改成「这个开关不存在」（Vulcan 4.4.0）。
    /// 一个从不被置 true 的开关守不住任何东西：它只是把「未实现」写成「可配置」的样子，
    /// 而下一个读代码的人会以为那条路还能走通。
    ///
    /// MCP 由 HistoryPortunus 承载，界面需要它的地方只剩命令集页，走指令总线取数。
    /// 哪天有人把网关加回界面，这条会立刻失败。
    /// </remarks>
    [Fact]
    public void ShellCannotCreateAnMcpGatewayAtAll()
    {
        Assert.Null(typeof(ShellConfig).GetProperty("EnableMcp"));
        Assert.Null(typeof(ShellConfig).GetProperty("McpAuditLog"));
        Assert.Null(typeof(ShellConfig).GetProperty("McpRemoteConfirm"));
        Assert.Null(typeof(ShellWindow).GetProperty("Mcp"));
        Assert.Null(typeof(ShellWindow).GetProperty("Prompts"));

        // 远程管理视图仍然要在：命令集页靠它经指令总线取数。
        Assert.True(AuroraShellHost.CreateConfig().EnableRemoteManagementViews);
    }

    [Fact]
    public void InProcessShellDoesNotStartASecondModuleHost()
    {
        var config = AuroraShellHost.CreateConfig();

        // 宿主自己的 ModuleHost 是模块生命周期的唯一所有者。界面若再建一套，
        // 每个模块会被装载两遍——两份实例各自注册命令、各自建页，
        // 症状是"命令数翻倍、页面重影"，很难联想到是装载了两次。
        Assert.False(config.EnableModules);
        Assert.False(config.EnableUiModules);
    }

    [Fact]
    public void InProcessShellHidesOnCloseSoTheHostKeepsRunning()
    {
        var config = AuroraShellHost.CreateConfig();

        // 关窗只是隐藏：界面与宿主同进程，真关掉等于把服务一起关了。
        Assert.Equal(ShellCloseBehavior.Hide, config.CloseBehavior);
    }
}
