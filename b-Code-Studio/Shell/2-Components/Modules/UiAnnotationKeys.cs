namespace HistoryAurora.Shell.Components.Modules;

/// <summary>
/// 模块用命令注解登记窗格时使用的键。5.0 起宿主不再提供 IShellUiRegistrar，
/// 模块在命令描述符的 Annotations 里写下这些键；Aurora 认领后执行该命令，
/// 把 <c>CommandResult.Data</c> 里的活对象停靠进布局。
/// </summary>
public static class UiAnnotationKeys
{
    public const string Window = "ui.window";

    public const string Side = "ui.side";

    public const string Title = "ui.title";

    public const string Ratio = "ui.ratio";
}
