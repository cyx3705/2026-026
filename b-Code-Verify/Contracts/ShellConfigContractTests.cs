using HistoryAurora.Module;
using HistoryAurora.Shell.Composition;
using Xunit;

namespace HistoryAurora.Verify.Contracts;

/// <summary>
/// `ShellConfig` 的默认能力，与进程内启动时界面的那一份配置（DEC-008）。
///
/// 界面与宿主同处一个进程，宿主已经拥有的东西界面不得再造一套——第二份的失败方式都是静默的：
/// 第二个 MCP 网关去抢端口，第二个 ModuleHost 把每个模块装载两遍（命令数翻倍、页面重影）。
/// </summary>
public sealed class ShellConfigContractTests
{
    /// <summary>界面只拿宿主版本做显示；任取一个与最低宿主版本无关的值，免得测试跟着宿主升级改。</summary>
    private const string AnyHostVersion = "0.0.0-test";

    /// <summary>能力默认全关：宿主与前端都不得靠默认值悄悄打开模块、MCP 或远程管理面。</summary>
    [Fact]
    public void ShellConfigDefaultsToMinimalOptionalCapabilities()
    {
        var config = new ShellConfig
        {
            AppName = "Minimal",
            AppVersion = "1.0.0",
        };

        Assert.False(config.EnableModules);
        Assert.False(config.EnableUiModules);
        Assert.False(config.EnableRemoteManagementViews);
        Assert.Empty(config.Panels);
    }

    /// <summary>
    /// 进程内的界面：不装模块（宿主的 ModuleHost 是唯一所有者）、关窗只隐藏（真关等于把服务一起关了）、
    /// 远程管理视图开着（命令集页靠它经指令总线取数）。MCP 由 HistoryPortunus 承载，界面连这个开关都没有。
    /// </summary>
    [Fact]
    public void InProcessShellLeavesModulesAndMcpToTheHost()
    {
        var config = AuroraShellHost.CreateConfig(AnyHostVersion);

        Assert.False(config.EnableModules);
        Assert.False(config.EnableUiModules);
        Assert.Equal(ShellCloseBehavior.Hide, config.CloseBehavior);
        Assert.True(config.EnableRemoteManagementViews);
        Assert.Null(typeof(ShellConfig).GetProperty("EnableMcp"));
    }
}
