namespace HistoryAurora.Shell.Neutral.Commands;

/// <summary>
/// 来源标签的显示（1.29.0）。宿主 6.0.0 起给模块的调用盖章：<c>module:&lt;模块名&gt;[:&lt;内层&gt;]</c>，
/// 内层是调用方自己给的标签（界面给 <c>UI</c>、<c>手动</c>，网关给 <c>MCP:&lt;客户端&gt;</c>）。
/// </summary>
internal static class SourceLabels
{
    private const string ModulePrefix = "module:";

    /// <summary>
    /// 控制台上显示的来源：剥掉全部模块章看最里面那一层；没有内层时显示模块名。
    /// 于是界面发起的仍显示 <c>UI</c> / <c>手动</c>，控制台按来源筛选的键不变。
    /// </summary>
    public static string Display(string source)
    {
        var current = source ?? "";
        var owner = "";
        while (current.StartsWith(ModulePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var rest = current[ModulePrefix.Length..];
            var colon = rest.IndexOf(':');
            if (colon < 0)
                return rest;
            owner = rest[..colon];
            current = rest[(colon + 1)..];
        }

        return current.Length == 0 ? owner : current;
    }

    /// <summary>
    /// 界面指令里再发一条指令时交给总线的来源：用最里面那一层，宿主会再盖一次本模块的章。
    /// 原样转交会叠成 <c>module:HistoryAurora:module:HistoryAurora:UI</c>。
    /// </summary>
    public static string Forward(string source) => Display(source);
}
