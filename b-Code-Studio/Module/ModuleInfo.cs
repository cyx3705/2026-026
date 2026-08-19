using BaseVariable;

namespace HistoryAurora.Module;

/// <summary>
/// Aurora 在 Vulcan 注册表里的身份(MD-02)。
///
/// Aurora 是前端应用，但它不因此享有特例：与 Diana / Janus / Mercury / Minerva 一样
/// 登记进宿主的模块注册表，在「模块管理」里作为平级条目出现。宿主之所以是宿主，
/// 只因为它持有指令总线与注册表本身，前端并不属于宿主。
/// </summary>
public sealed class ModuleInfo : ModuleInfoBase
{
    /// <summary>
    /// 必须显式覆写。基类默认取程序集名，而本模块的程序集名是 HistoryAurora.Module
    /// ——它不能叫 HistoryAurora，否则会与 App 同名并遮蔽 pack URI 的资源解析
    /// (见 HistoryAurora.Module.csproj 的注释)。ModuleName 同时是指令域名，
    /// 若随程序集名漂成 HistoryAuroraModule，域就会从 aurora 变成别的东西。
    /// </summary>
    public override string ModuleName => "HistoryAurora";

    /// <summary>指令域短拼：宿主按此投影模块指令，产出 aurora.*。</summary>
    public string CommandPrefix => "aurora";

    public override string Description => "前端界面：窗口、布局、控制台与主题";

    public override string Author => "OneHistory";

    public override string Version =>
        typeof(ModuleInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// 暂不经模块注册指令。前端的 aurora.ui.* / aurora.log.* 由 Aurora 进程自持
    /// (它们要求 UI 线程与活动窗口)，经前端注册表上报宿主，来源标记 frontend:*。
    /// 本模块目前只承担注册表身份；页面注册契约(模块间调用)属于 C 阶段。
    /// </summary>
    public override Type? MainClassType => null;
}
