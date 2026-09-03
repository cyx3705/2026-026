namespace HistoryAurora.Shell.Neutral;

/// <summary>
/// 界面自注册命令在宿主注册表里的来源标记。
///
/// **它是一个中立事实，不是装配根的私产。** 这条常量原先只挂在
/// <c>Composition.FrontendCommandCatalog</c> 上，于是页面层每登记一条自己的取数命令，
/// 就得反向 <c>using</c> 装配根一次——依赖方向反了，而两边都写作
/// <c>FrontendCommandCatalog.Source</c>、同在一个 <c>HistoryAurora.Shell</c> 命名空间里，
/// 门禁一个字也看不出来。目录与命名空间对齐之后它才第一次现形。
/// </summary>
internal static class FrontendCommandSource
{
    public const string Name = "framework:frontend";
}
