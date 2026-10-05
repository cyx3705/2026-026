using System.Text;

namespace HistoryAurora.Shell.Components.Catalog;

/// <summary>
/// 组件目录：页面节点、面板小组件与协议的自描述全集（见 <see cref="ComponentSpec"/>）。
///
/// 加组件的人只改两处：组件本体，和这里的一条规格。漏了后者门禁不过——
/// 渲染器认的节点与目录逐个对账，模型字段与字段说明逐个对账，示例逐个真渲染。
/// </summary>
public static partial class ComponentCatalog
{
    public static IReadOnlyList<ComponentSpec> All { get; } = [.. Nodes(), .. Widgets(), .. Protocols()];

    /// <summary>按查询名找（大小写不敏感）；小组件也认不带 <c>panel.</c> 前缀的 kind 值。</summary>
    public static ComponentSpec? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var key = name.Trim();
        return All.FirstOrDefault(spec => string.Equals(spec.Name, key, StringComparison.OrdinalIgnoreCase))
            ?? All.FirstOrDefault(spec => string.Equals(spec.WidgetKind, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>页面节点的 type 值集合。<c>PageRenderer</c> 的分派表必须与它一一对应。</summary>
    public static IEnumerable<string> NodeTypes => All.Where(s => s.Layer == ComponentLayer.Node).Select(s => s.Name);

    public static string LayerTitle(ComponentLayer layer) => layer switch
    {
        ComponentLayer.Node => "页面节点（\"type\"）",
        ComponentLayer.Widget => "面板小组件（\"kind\"）",
        _ => "协议",
    };

    /// <summary>目录总表：每层一段，每条一行。</summary>
    public static string DescribeAll()
    {
        var text = new StringBuilder();
        foreach (var group in All.GroupBy(spec => spec.Layer))
        {
            if (text.Length > 0)
                text.AppendLine();
            text.AppendLine(LayerTitle(group.Key) + "：");
            foreach (var spec in group)
                text.Append("  ").Append(spec.Name.PadRight(20)).Append(spec.Summary).AppendLine();
        }

        text.AppendLine();
        text.Append("看一条的字段、示例与规矩：aurora.component.show name=<名>");
        return text.ToString();
    }

    /// <summary>一条规格的全文。<paramref name="liveKeys"/> 只给 xaml 那一条：运行中现取的键清单。</summary>
    public static string Describe(ComponentSpec spec, IReadOnlyList<string>? liveKeys = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var text = new StringBuilder();
        text.Append(spec.Name).Append("  ·  ").AppendLine(LayerTitle(spec.Layer));
        text.AppendLine(spec.Summary);

        if (spec.Fields.Count > 0)
        {
            text.AppendLine().AppendLine("字段：");
            foreach (var field in spec.Fields)
            {
                text.Append("  ").Append(field.Path).Append("  ").Append(field.Type);
                if (field.Required)
                    text.Append("  必填");
                if (field.Default != null)
                    text.Append("  缺省 ").Append(field.Default);
                if (field.Values is { Count: > 0 } values)
                    text.Append("  取值 ").Append(string.Join(" / ", values));
                text.AppendLine().Append("      ").AppendLine(field.Description);
            }
        }

        if (spec.Rules.Count > 0)
        {
            text.AppendLine().AppendLine("规矩：");
            foreach (var rule in spec.Rules)
                text.Append("  - ").AppendLine(rule);
        }

        text.AppendLine().AppendLine("示例：");
        foreach (var line in spec.Example.Replace("\r\n", "\n").Split('\n'))
            text.Append("  ").AppendLine(line);

        if (liveKeys is { Count: > 0 })
        {
            text.AppendLine().Append("现有键（").Append(liveKeys.Count).AppendLine(" 个）：");
            foreach (var group in liveKeys.GroupBy(KeyFamily))
                text.Append("  ").Append(group.Key).Append(".*  ").AppendLine(string.Join("  ", group.Select(k => k[(group.Key.Length + 1)..])));
        }

        return text.ToString().TrimEnd();
    }

    /// <summary><c>Aurora.Brush.Accent</c> → <c>Aurora.Brush</c>。</summary>
    private static string KeyFamily(string key)
    {
        var dot = key.IndexOf('.', "Aurora.".Length);
        return dot < 0 ? "Aurora" : key[..dot];
    }
}
