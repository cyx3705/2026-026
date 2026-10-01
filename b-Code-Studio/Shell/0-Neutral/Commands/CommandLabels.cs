namespace HistoryAurora.Shell.Neutral.Commands;

/// <summary>
/// 「无类」显示标签（DEC-025）。两段名 <c>&lt;域&gt;.&lt;方法&gt;</c> 的类为空串，显示成「无类」。
/// </summary>
/// <remarks>1.29.0 起归界面所有：宿主 6.0.0 把同名类型收回了内部，这本来就是界面的显示约定。</remarks>
internal static class CommandClassLabels
{
    public const string None = "无类";

    public static string Display(string? commandClass)
        => string.IsNullOrWhiteSpace(commandClass) ? None : commandClass.Trim().ToLowerInvariant();

    public static string ToKey(string? label)
        => string.IsNullOrWhiteSpace(label) || label.Trim() == None
            ? string.Empty
            : label.Trim().ToLowerInvariant();
}

/// <summary>
/// 域聚焦下的输入解析（DEC-025 / REQ-CMD-012）：首段命中已注册域时按绝对名，否则补上聚焦域前缀。
/// </summary>
/// <remarks>1.29.0 起归界面所有（控制台的交互规则）；已注册域的权威源是 <see cref="ShellCatalog.IsRegisteredDomain"/>。</remarks>
internal static class DomainFocus
{
    public const string All = "全部";

    public static bool IsUnfocused(string? domain)
        => string.IsNullOrWhiteSpace(domain) || domain.Trim() == All;

    public static string Resolve(string? input, string? focusedDomain, Func<string, bool> isRegisteredDomain)
    {
        ArgumentNullException.ThrowIfNull(isRegisteredDomain);

        var text = (input ?? string.Empty).TrimStart();
        if (text.Length == 0 || IsUnfocused(focusedDomain))
            return input ?? string.Empty;

        var end = text.Length;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]) || text[i] == '.')
            {
                end = i;
                break;
            }
        }

        var head = text[..end];
        if (head.Length == 0 || isRegisteredDomain(head))
            return input ?? string.Empty;

        return $"{focusedDomain!.Trim()}.{text}";
    }
}
