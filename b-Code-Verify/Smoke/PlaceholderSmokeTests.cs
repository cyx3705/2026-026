using HistoryAurora.Module;
using Xunit;

namespace HistoryAurora.Verify.Smoke;

/// <summary>
/// 冒烟工程占位。承接界面能力后，这里放需要真实 WPF 组件的用例
/// （窗口、停靠、布局持久化），与不需要界面的 Contracts 分开。
/// </summary>
public sealed class PlaceholderSmokeTests
{
    [Fact]
    public void ModuleAssemblyLoads()
        => Assert.NotNull(typeof(AuroraBusinessComposition).Assembly);
}
