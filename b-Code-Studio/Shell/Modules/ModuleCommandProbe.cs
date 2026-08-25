using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Services.Commands;

namespace HistoryAurora.Shell.Modules;

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
    /// <summary>域名反推模块名，用于 owner 校验：mercury → HistoryMercury。</summary>
    public static string ExpectedOwner(string domain) => "History" + Capitalize(domain);

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
            try
            {
                var listed = await bus.ExecuteAsync("vulcan.command.list", "UI", cancellation)
                    .ConfigureAwait(true);
                if (listed.Success
                    && CommandResultData.TryRead<IReadOnlyList<CommandCatalogRow>>(listed.Data, out var rows))
                {
                    names.AddRange(rows.Select(row => row.CommandName));
                }
            }
            catch (Exception ex)
            {
                log.Log(ShellLogLevel.Warn, source, "读取宿主命令目录失败: " + ex.Message);
            }
        }

        return names
            .Where(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .Select(name => name[..^suffix.Length])
            .Where(domain => domain.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(domain => domain, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string Capitalize(string value)
        => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
