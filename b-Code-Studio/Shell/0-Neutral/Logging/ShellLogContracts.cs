using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Shell.Neutral.Logging;

/// <summary>控制台显示的一条日志。</summary>
public sealed record ShellLogEntry(
    DateTime Time,
    ShellLogLevel Level,
    string Category,
    string Message);

/// <summary>
/// 界面自己的日志视图接口（1.29.0）：写入（<see cref="IModuleLog"/>）、新纪录事件与缓冲快照。
/// </summary>
/// <remarks>
/// 宿主 6.0.0 起宿主日志对模块只写，它原先那个带事件与快照的 <c>IShellLog</c> 收回了宿主内部。
/// 控制台要的「读」这一半归界面自己：实现是 <see cref="MemoryShellLog"/>，
/// 数据来自宿主总线主题 <c>vulcan.log.entry</c> 与 <c>vulcan.log.recent</c>。
/// </remarks>
public interface IShellLog : IModuleLog
{
    /// <summary>新纪录到达。</summary>
    event EventHandler<ShellLogEntry>? EntryAdded;

    /// <summary>当前缓冲内容快照。</summary>
    IReadOnlyList<ShellLogEntry> Snapshot();
}

/// <summary>
/// 宿主写指令回显、进度与结果时用的日志类别（宿主模块API「日志类别」一节）。
/// 契约是这几段字符串，不是宿主的某个 C# 常量。
/// </summary>
internal static class HostLogCategories
{
    /// <summary>回显：<c>cmd:&lt;来源&gt;</c>。</summary>
    public const string EchoPrefix = "cmd:";

    /// <summary>结果：<c>cmd:result:&lt;域&gt;:&lt;类&gt;</c>。</summary>
    public const string Result = "cmd:result";

    /// <summary>进度：<c>cmd:progress:&lt;域&gt;:&lt;类&gt;</c>。</summary>
    public const string Progress = "cmd:progress";
}
