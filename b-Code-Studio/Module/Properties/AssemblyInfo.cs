using System.Runtime.CompilerServices;

// 本程序集**不对任何外部产品开放内部成员**。
//
// 1.7.0 起停靠系统（HistoryAurora.Shell.Base.Docking）整体收成 internal：
// 它曾经是宿主 Core 的一部分，5.0 迁进 Aurora 后仍以 public 面暴露着，
// 而没有任何一个模块引用 Aurora 的程序集——模块只经指令总线打交道。
// 一个没有消费者的公开面只会变成"改不动"的理由。
//
// 这里原先还留着 HistoryVulcan.Tests 与 HistoryMercury 两条。前者属于宿主仓，
// 分仓之后不再编译本程序集；后者从未引用过 Aurora（Mercury 里出现的 HistoryAurora
// 只在注释与页面描述的文字里）。两条都是失效授权，且正好把刚收起来的停靠面重新放开，
// 因此一并删除。测试工程的授权改在 csproj 的 InternalsVisibleTo 项里声明。
