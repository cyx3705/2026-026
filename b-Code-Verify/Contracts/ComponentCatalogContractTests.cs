using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HistoryAurora.Shell.Components.Actions;
using HistoryAurora.Shell.Components.Catalog;
using HistoryAurora.Shell.Components.Graph;
using HistoryAurora.Shell.Components.Pages;
using HistoryAurora.Shell.Components.Panels;
using HistoryAurora.Shell.Components.Selection;
using HistoryAurora.Shell.Components.Table;
using HistoryAurora.Shell.Components.Themes;
using HistoryAurora.Shell.Neutral.Commands;
using HistoryAurora.Shell.Neutral.Logging;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 组件目录门禁：页面协议的用法只来自 <see cref="ComponentCatalog"/>，这里守住它与实现不分家。
///
/// 为什么是门禁而不是文档：组件层谁都能来改，手写的《组件清单与用法》删的时候清出来
/// 一半是过期内容——讲的方法已经删了、讲的字段渲染器根本不读。对齐靠人记得，人总会忘；
/// 对齐靠测试，忘了就提交不了。
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class ComponentCatalogContractTests
{
    private static readonly JsonSerializerOptions ModelJson = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    [Fact]
    public void EveryRenderableNodeTypeHasASpecAndNoSpecIsUnrenderable()
    {
        // 渲染器认、目录不认 = 新组件没写用法；目录认、渲染器不认 = 用法在骗人。
        Assert.Equal(
            PageRenderer.SupportedComponents.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
            ComponentCatalog.NodeTypes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryPanelWidgetKindHasASpec()
    {
        var widgets = ComponentCatalog.All.Where(s => s.Layer == ComponentLayer.Widget).ToList();
        foreach (var spec in widgets)
            Assert.True(new PanelWidget { Kind = spec.WidgetKind! }.ResolvedKind != null, $"{spec.Name} 的 kind 面板不认");

        foreach (var kind in Enum.GetValues<PanelWidgetKind>())
        {
            Assert.True(
                widgets.Any(spec => new PanelWidget { Kind = spec.WidgetKind! }.ResolvedKind == kind),
                $"面板小组件 {kind} 没有规格：在 ComponentCatalog.Widgets 补一条");
        }
    }

    [Fact]
    public void SpecsAreSelfContained()
    {
        var names = ComponentCatalog.All.Select(s => s.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var spec in ComponentCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(spec.Summary), $"{spec.Name} 缺一句话用途");
            Assert.False(string.IsNullOrWhiteSpace(spec.Example), $"{spec.Name} 缺示例");
            Assert.True(spec.Layer == ComponentLayer.Protocol || spec.Model != null, $"{spec.Name} 没有对照模型，字段无从核对");
            foreach (var field in spec.Fields)
            {
                Assert.False(string.IsNullOrWhiteSpace(field.Description), $"{spec.Name}.{field.Path} 缺说明");
                Assert.True(field.Values is null || field.Values.Count > 0, $"{spec.Name}.{field.Path} 的取值表为空");
            }

            Assert.All(spec.Rules, rule => Assert.False(string.IsNullOrWhiteSpace(rule)));
        }
    }

    /// <summary>说明里写的字段必须真实存在：写错一个字的字段说明，比没有说明更害人。</summary>
    [Fact]
    public void EveryDescribedFieldExistsOnTheModel()
    {
        var failures = new List<string>();
        foreach (var spec in ComponentCatalog.All.Where(s => s.Model != null))
        {
            foreach (var field in spec.Fields)
            {
                if (Resolve(spec.Model!, field.Path) == null)
                    failures.Add($"{spec.Name}: {field.Path} 在 {spec.Model!.Name} 上找不到");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// 模型上每个可写字段都必须有人说明。给 PageNode 加了字段却不写进目录，这条就失败——
    /// 退役组件留下的「能写、解析器也认、渲染器不读」的死字段也是这样被清出来的。
    /// </summary>
    [Fact]
    public void EveryWritableModelFieldIsDescribed()
    {
        var models = ComponentCatalog.All.Where(s => s.Model != null).Select(s => s.Model!).Distinct().ToHashSet();
        var described = new HashSet<PropertyInfo>();
        foreach (var spec in ComponentCatalog.All.Where(s => s.Model != null))
        {
            foreach (var field in spec.Fields)
            {
                if (Resolve(spec.Model!, field.Path) is { } chain)
                    described.UnionWith(chain);
            }
        }

        var missing = new List<string>();
        foreach (var model in models)
            Walk(model, models, described, missing, [], model.Name);

        Assert.True(missing.Count == 0, "以下字段没有说明（写进 ComponentCatalog，或者删掉死字段）:\n" + string.Join("\n", missing));
    }

    /// <summary>每份节点与小组件示例都要被真的渲染一遍：不缺件、不进退役牌、不记 Warn。</summary>
    [Fact]
    public void NodeAndWidgetExamplesRenderCleanly()
    {
        UiTestHost.RunSta(() =>
        {
            foreach (var spec in ComponentCatalog.All.Where(s => s.Layer is ComponentLayer.Node or ComponentLayer.Widget))
            {
                var content = spec.Layer == ComponentLayer.Node
                    ? spec.Example
                    : $$"""{ "type": "panel", "id": "example", "rows": [ { "widgets": [ {{spec.Example}} ] } ] }""";

                var (context, log) = Context(content);
                var rendered = PageRenderer.Render(Page(content), context);

                Assert.True(rendered.MissingComponents.Count == 0, $"{spec.Name} 的示例渲染出缺件: {string.Join(",", rendered.MissingComponents)}");
                var noise = log.Snapshot().Where(e => e.Level >= ShellLogLevel.Warn).Select(e => e.Message).ToList();
                Assert.True(noise.Count == 0, $"{spec.Name} 的示例渲染时报了: {string.Join(" | ", noise)}");
            }
        });
    }

    [Fact]
    public void ProtocolExamplesParseWithTheRealReaders()
    {
        Assert.True(PageDescriptionReader.Read(Example("page"), "HistoryMymodule").Ok);
        Assert.True(ActionDeclarationReader.Read(Example("actions"), "HistoryMymodule").Ok);
        Assert.True(SwimlaneReader.Read(Example("swimlane.data")).Ok);
        Assert.True(TableUpdate.Read(Example("table.data")).IsDelta);

        var panel = JsonSerializer.Deserialize<PanelDefinition>(Example("panel.file"), ModelJson);
        Assert.True(PanelDefinitionValidator.Validate(panel).Ok);
    }

    [Fact]
    public void ShowCarriesEverythingAndXamlListsLiveKeys()
    {
        var all = ComponentCatalog.DescribeAll();
        foreach (var spec in ComponentCatalog.All)
        {
            Assert.Contains(spec.Name, all);
            var text = ComponentCatalog.Describe(spec);
            Assert.Contains(spec.Summary, text);
            Assert.All(spec.Fields, field => Assert.Contains(field.Path, text));
        }

        Assert.Same(ComponentCatalog.Find("panel.textbox"), ComponentCatalog.Find("textbox"));

        UiTestHost.RunSta(() =>
        {
            var keys = AuroraComponentResources.PublicKeys();
            Assert.Contains("Aurora.Button.Accent", keys);
            Assert.Contains("Aurora.Brush.Accent", keys);
            Assert.Contains("Aurora.Button.Accent", ComponentCatalog.Describe(ComponentCatalog.Find("xaml")!, keys));
        });
    }

    // ------------------------------------------------------------------ 字段对账

    /// <summary>按点分路径从模型走下去，返回沿途的属性链；走不通返回 null。集合与字典自动展开到元素类型。</summary>
    private static List<PropertyInfo>? Resolve(Type model, string path)
    {
        var chain = new List<PropertyInfo>();
        var current = model;
        foreach (var raw in path.Split('.'))
        {
            var segment = raw.Replace("[]", "");
            var property = Writable(current).FirstOrDefault(p => string.Equals(JsonName(p), segment, StringComparison.OrdinalIgnoreCase));
            if (property == null)
                return null;
            chain.Add(property);
            current = ElementType(property.PropertyType);
        }

        return chain;
    }

    private static void Walk(Type type, HashSet<Type> roots, HashSet<PropertyInfo> described, List<string> missing, HashSet<Type> seen, string path)
    {
        if (!seen.Add(type))
            return;
        foreach (var property in Writable(type))
        {
            var here = path + "." + JsonName(property);
            if (!described.Contains(property))
                missing.Add(here);
            var element = ElementType(property.PropertyType);
            // 别的规格的模型由它自己的规格负责（children → PageNode、widgets → PanelWidget）。
            if (IsOwnModel(element) && !roots.Contains(element))
                Walk(element, roots, described, missing, seen, here);
        }
    }

    private static IEnumerable<PropertyInfo> Writable(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true })
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() == null);

    private static string JsonName(PropertyInfo property)
        => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
           ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);

    private static Type ElementType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(type))
            return type;
        var generic = type.IsGenericType ? type.GetGenericArguments() : [];
        // 字典取值类型，列表取元素类型。
        return generic.Length switch
        {
            2 => ElementType(generic[1]),
            1 => ElementType(generic[0]),
            _ => type,
        };
    }

    private static bool IsOwnModel(Type type)
        => type.IsClass && type != typeof(string) && type.Namespace?.StartsWith("HistoryAurora.", StringComparison.Ordinal) == true;

    // ------------------------------------------------------------------ 渲染装配

    private static string Example(string name) => ComponentCatalog.Find(name)!.Example;

    private static PageDescription Page(string contentJson)
    {
        var parsed = PageDescriptionReader.Read($$"""
            { "schemaVersion": 1, "owner": "HistoryMymodule",
              "pages": [ { "id": "example", "title": "示例", "content": {{contentJson}} } ] }
            """, "HistoryMymodule");
        Assert.True(parsed.Ok, parsed.Error);
        return parsed.Value!.Pages[0];
    }

    /// <summary>示例里引用到的动作 id 全部就地声明，指向一条什么都不做的指令：门禁验的是写法，不是业务。</summary>
    private static (PageRenderContext Context, MemoryLog Log) Context(string content)
    {
        var registry = new CommandTable();
        registry.Register(new CommandDescriptor
        {
            Name = "mymodule.example.sink",
            Domain = "mymodule",
            CommandClass = "example",
            Summary = "示例动作落点",
            Readonly = true,
            AllowUnspecifiedParameters = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("ok")),
        });

        var log = new MemoryLog();
        var bus = TestShell.Bus(registry);
        var actions = new ActionRegistry(bus, log);
        var ids = Regex.Matches(content, "\"(?:action|cellAction|commitAction)\"\\s*:\\s*\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .Select(id => new ActionDeclaration { Id = id, Title = id, Command = "mymodule.example.sink" });
        actions.DeclareLocal("HistoryMymodule", ids);

        var channels = new SelectionChannels();
        return (new PageRenderContext
        {
            Bus = bus,
            Log = log,
            Owner = "HistoryMymodule",
            Actions = actions,
            Channels = channels,
            Refresher = new PageDataRefresher(channels),
        }, log);
    }

    private sealed class MemoryLog : IShellLog
    {
        private readonly List<ShellLogEntry> _entries = [];

        public void Log(ShellLogLevel level, string category, string message)
        {
            var entry = new ShellLogEntry(DateTime.Now, level, category, message);
            _entries.Add(entry);
            EntryAdded?.Invoke(this, entry);
        }

        public event EventHandler<ShellLogEntry>? EntryAdded;

        public IReadOnlyList<ShellLogEntry> Snapshot() => _entries.ToList();
    }
}
