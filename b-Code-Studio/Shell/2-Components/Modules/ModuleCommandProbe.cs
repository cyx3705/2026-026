using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Services.Commands;
using HistoryAurora.Shell.Neutral;

namespace HistoryAurora.Shell.Components.Modules;

/// <summary>
/// "哪些模块声明了某类契约"的唯一判定处：直接看注册表里有没有 <c>&lt;域&gt;.&lt;后缀&gt;</c>。
///
/// 不用"先问 manifest 的 ui 标志再逐个试"——那要求宿主把 manifest 字段透出来，
/// 而注册表本来就是权威且已经在手边；模块没注册该命令就是没有这项契约，不是错误。
///
/// 页面描述（<c>.ui.describe</c>）与动作声明（<c>.ui.actions</c>）用的是同一套判定，
/// 因此收在一处：两份各写一遍的话，远程目录那段回退逻辑迟早只在其中一份里被修。
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

    /// <summary>
    /// 远端目录快照的存活时间。动作拉取与页面拉取在同一轮发现里各问一次
    /// <c>vulcan.command.list</c>，目录会话刷新往往还要再问——同一份权威源
    /// 被连打三遍，而 230ms 内的注册风暴会把它放大到上百次。
    /// </summary>
    internal static readonly TimeSpan RemoteListCacheDuration = TimeSpan.FromMilliseconds(400);

    private static readonly object RemoteListGate = new();
    private static CommandBus? _cachedBus;
    private static IReadOnlyList<string>? _cachedRemoteNames;
    private static long _cachedAt;

    /// <summary>域名反推模块名，用于 owner 校验：mercury → HistoryMercury。</summary>
    public static string ExpectedOwner(string domain) => "History" + Capitalize(domain);

    /// <summary>丢掉远端目录缓存。测试在两次探测之间改注册表时必须调用。</summary>
    internal static void ResetRemoteListCache()
    {
        lock (RemoteListGate)
        {
            _cachedBus = null;
            _cachedRemoteNames = null;
            _cachedAt = 0;
        }
    }

    /// <summary>
    /// 列出注册了 <paramref name="suffix"/> 后缀命令的域。
    /// 挂了远程执行器时额外读一次宿主命令目录——本进程注册表里没有对端的模块命令。
    /// </summary>
    public static async Task<List<string>> OwnersWithSuffixAsync(
        CommandBus bus,
        IShellLog log,
        string source,
        string suffix,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(log);

        var names = bus.Registry.All().Select(d => d.Name).ToList();
        if (bus.RemoteExecutor != null)
        {
            var remote = await RemoteCommandNamesAsync(bus, log, source, cancellation)
                .ConfigureAwait(true);
            names.AddRange(remote);
        }

        return names
            .Where(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .Select(name => name[..^suffix.Length])
            .Where(domain => domain.Length > 0)
            // 界面不向自己拉描述或动作声明：那条同名命令是查询，不是声明。
            .Where(domain => !domain.Equals(SelfDomain, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(domain => domain, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<IReadOnlyList<string>> RemoteCommandNamesAsync(
        CommandBus bus,
        IShellLog log,
        string source,
        CancellationToken cancellation)
    {
        lock (RemoteListGate)
        {
            if (ReferenceEquals(_cachedBus, bus)
                && _cachedRemoteNames != null
                && Environment.TickCount64 - _cachedAt < (long)RemoteListCacheDuration.TotalMilliseconds)
                return _cachedRemoteNames;
        }

        IReadOnlyList<string> names = [];
        try
        {
            var listed = await bus.ExecuteAsync("vulcan.command.list", "UI", cancellation)
                .ConfigureAwait(true);
            if (listed.Success
                && CommandResultData.TryRead<IReadOnlyList<CommandCatalogRow>>(listed.Data, out var rows))
            {
                names = rows.Select(row => row.CommandName).ToList();
            }
        }
        catch (Exception ex)
        {
            log.Log(ShellLogLevel.Warn, source, "读取宿主命令目录失败: " + ex.Message);
        }

        lock (RemoteListGate)
        {
            _cachedBus = bus;
            _cachedRemoteNames = names;
            _cachedAt = Environment.TickCount64;
        }

        return names;
    }

    private static string Capitalize(string value)
        => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
