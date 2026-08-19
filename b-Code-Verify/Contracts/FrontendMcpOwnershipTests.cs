using HistoryVulcan.Core.Commands;
using Xunit;
using AuroraApp = HistoryAurora.App;

namespace HistoryAurora.Verify.Contracts;

/// <summary>
/// 前端不持有 MCP，只把后台设置命令路由回去（REQ-MCP-002 的前端侧，REQ-A8 迁入）。
///
/// 宿主 `StandaloneMcpOwnershipTests` 原本一并覆盖前后两侧。前端迁出后按被测对象拆开：
/// 后台侧（`ServiceComposer` 的键白名单与脱敏）留在宿主，前端侧两条搬来这里。
/// 拆分的判据是"这段断言调用的方法住在哪个进程"，不是"它讲的是哪个主题"。
/// </summary>
public sealed class FrontendMcpOwnershipTests
{
    /// <summary>
    /// 独立前端配置必须 `EnableMcp = false`。前端一旦自持 MCP，就会出现第二个工具目录——
    /// 它只看得到前端本地注册表，模块命令全部缺席，而调用方无从知道自己连的是哪一个。
    /// </summary>
    [Fact]
    public void StandaloneFrontendDelegatesMcpOwnershipToBackend()
    {
        var config = AuroraApp.CreateStandaloneFrontendConfig(
            HistoryVulcan.Core.AppIdentity.Current);

        Assert.False(config.EnableMcp);
        Assert.True(config.EnableRemoteManagementViews);
    }

    /// <summary>
    /// `mcp.*` 配置键的读写必须路由到后台，`web.*` 等前端自己的键则留在本地。
    /// 判错的后果是静默的：设置写进了错误的那一份 settings.json，界面显示正常，重启后失效。
    /// </summary>
    [Fact]
    public void BackendMcpSettingCommandsAreRoutedToTheService()
    {
        Assert.True(AuroraApp.IsBackendMcpSettingCommand(
            CommandParser.Parse("vulcan.app.get key=mcp.policy")));
        Assert.False(AuroraApp.IsBackendMcpSettingCommand(
            CommandParser.Parse("vulcan.app.get key=web.port")));
    }
}
