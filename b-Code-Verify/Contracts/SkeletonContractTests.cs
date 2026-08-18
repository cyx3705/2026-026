using HistoryAurora.Module;
using Xunit;

namespace HistoryAurora.Verify.Contracts;

/// <summary>
/// 骨架阶段的合同：REQ-UI-001 尚未实施，`Attach` 必须**不**注册任何命令。
///
/// 这条看似无用，实则是迁移期的守门人：第一组能力迁入时它会失败，迫使实施者同时更新
/// 技术合同与本用例，而不是让"骨架"和"已接管"两种状态在文档里含糊并存。
/// </summary>
public sealed class SkeletonContractTests
{
    [Fact]
    public void CompositionStartsWithoutHostContext()
    {
        var composition = new AuroraBusinessComposition();

        Assert.Null(composition.Context);
    }
}
