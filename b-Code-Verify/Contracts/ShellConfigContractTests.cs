using HistoryVulcan.Core.Commands;
using HistoryAurora.Shell;
using Xunit;

namespace HistoryAurora.Verify.Contracts;

/// <summary>
/// `ShellConfig` 默认能力与前端命令代理的治理元数据（自宿主 QualityRemediationTests 迁入，REQ-A8）。
///
/// 宿主那份文件里 4 条用例有 2 条测的是前端类型，随前端迁走；
/// 另外 2 条（设置文件损坏恢复、只读回退注册线程安全）测的是 Services 与 Core，留在宿主。
/// 按被测对象拆分，而不是按文件整体搬。
/// </summary>
public sealed class ShellConfigContractTests
{
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
        Assert.False(config.EnableMcp);
        Assert.False(config.EnableRemoteManagementViews);
        Assert.Empty(config.Panels);
    }

    /// <summary>
    /// 两条造代理的路径必须给出同一份治理元数据。它们分别服务于框架内置命令与模块能力，
    /// 若有一条漏掉 <c>IsDangerous</c> 之类的位，危险命令会在某一条路径上悄悄失去确认闸口。
    /// </summary>
    [Fact]
    public void FrontendProxyFactoriesUseTheSameGovernanceMetadata()
    {
        var source = new CommandDescriptor
        {
            Name = "ui.dangerous",
            Summary = "dangerous",
            Dangerous = true,
            RequiresUiThread = true,
            AllowUnspecifiedParameters = true,
            AllowMcpExecution = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
        };

        var frameworkProxy = FrontendCommandCatalog.CreateProxy(source);
        var capabilityProxy = FrontendCommandCapability.From(
            source, FrontendCommandCatalog.Source).CreateProxy();

        Assert.Equal(capabilityProxy.IsDangerous, frameworkProxy.IsDangerous);
        Assert.Equal(capabilityProxy.RequiresUiThread, frameworkProxy.RequiresUiThread);
        Assert.Equal(capabilityProxy.AllowUnspecifiedParameters, frameworkProxy.AllowUnspecifiedParameters);
        Assert.Equal(capabilityProxy.AllowMcpExecution, frameworkProxy.AllowMcpExecution);
        Assert.NotNull(frameworkProxy.ConfirmPrompt);
    }
}
