using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;

namespace HistoryAurora.Module;

/// <summary>
/// HistoryAurora 的模块装配入口。
///
/// 定位：宿主对用户可见的那一面——窗口、布局、控制台与主题。这些能力目前仍由
/// HistoryVulcan 前端进程自持（实测：`vulcan` 域 81 条命令中 39 条来源为
/// <c>frontend:HistoryVulcan.Frontend</c>，涵盖 ui 21 条、log 10 条、app 4 条、command 4 条，
/// 且其中零条投影为 MCP 工具）。本模块的目标是把那 39 条从宿主搬出来，让宿主只保留
/// 指令总线与 MCP。宿主版本要求见 <c>AuroraVersion.props</c>，此处不复述字面量。
///
/// 迁移尚未开始：此处只是骨架，<see cref="Attach"/> 暂不注册任何命令。每迁入一组能力，
/// 都应在 <c>b-Office/current/技术合同.md</c> 立一条带编号的要求与验收，再落代码。
/// </summary>
public sealed class AuroraBusinessComposition : IModuleContextAware
{
    private IModuleContext? _context;

    /// <summary>宿主在装载时注入上下文，提供权威命令总线、设置、日志与数据根目录。</summary>
    public void Attach(IModuleContext context)
    {
        _context = context;
        context.Log.Info("aurora", "HistoryAurora 已装载（骨架，尚未接管前端能力）");
    }

    /// <summary>当前生效的宿主上下文；未装载时为 null。</summary>
    internal IModuleContext? Context => _context;

    /// <summary>
    /// 迁入命令时统一走这里登记。
    ///
    /// 宿主给的是一个与实时注册表隔离的暂存注册表，由宿主随模块一起提交与回收——
    /// 因此模块不得自己持有实时注册表引用，否则卸载时无法完整回收。
    /// </summary>
    internal void RegisterCommands(Action<CommandRegistry> configure)
        => _context?.RegisterCommands(configure);
}
