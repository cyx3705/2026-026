using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryAurora.Shell.Neutral;
using HistoryAurora.Shell.Neutral.Logging;
using HistoryAurora.Shell.Neutral.Commands;

namespace HistoryAurora.Shell.Components.Modules;

/// <summary>
/// "哪些模块声明了某类契约"的唯一判定处：直接看注册表里有没有 <c>&lt;域&gt;.&lt;后缀&gt;</c>。
///
/// 不用"先问 manifest 的 ui 标志再逐个试"——那要求宿主把 manifest 字段透出来，
/// 而目录本来就是权威且已经在手边；模块没注册该命令就是没有这项契约，不是错误。
///
/// 页面描述（<c>.ui.describe</c>）与动作声明（<c>.ui.actions</c>）用的是同一套判定，因此收在一处。
/// </summary>
public static class ModuleCommandProbe
{
    /// <summary>
    /// 界面自己的域。探测时必须排除它。
    ///
    /// Aurora 自己注册了一条 <c>aurora.ui.actions</c>——那是**查询**动作台账的命令，
    /// 与协议槽位 <c>&lt;域&gt;.ui.actions</c>（模块用来**声明**动作）同名。
    /// 不排除的话，每一轮探测都会把 Aurora 当成一个声明了动作的模块，
    /// 去调它自己那条查询命令，再拿一段人话去做 JSON 解析并失败。
    /// 真机上还多一层：模块热重载时 Aurora 的命令要晚几百毫秒才重新注册，
    /// 那个窗口期里这次自调会留下一条 `✗ 未知指令: aurora.ui.actions` 的错误
    /// （2026-08-25 实测，界面右上角的错误计数就是它）。
    /// </summary>
    public const string SelfDomain = "aurora";

    /// <summary>域名反推模块名，用于 owner 校验：mercury → HistoryMercury。</summary>
    public static string ExpectedOwner(string domain) => "History" + Capitalize(domain);

    /// <summary>
    /// 列出注册了 <paramref name="suffix"/> 后缀命令的域。
    /// 1.29.0 起目录就是宿主目录（<see cref="ShellBus.Registry"/>），不再另读一次远端补齐。
    /// </summary>
    public static Task<List<string>> OwnersWithSuffixAsync(
        ShellBus bus,
        IShellLog log,
        string source,
        string suffix,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(log);

        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult(bus.Registry.All().Select(d => d.Name)
            .Where(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .Select(name => name[..^suffix.Length])
            .Where(domain => domain.Length > 0)
            // 界面不向自己拉描述或动作声明：那条同名命令是查询，不是声明。
            .Where(domain => !domain.Equals(SelfDomain, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(domain => domain, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    private static string Capitalize(string value)
        => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
