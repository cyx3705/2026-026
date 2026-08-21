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
        Assert.False(config.EnableRemoteManagementViews);
        Assert.Empty(config.Panels);
    }
}
