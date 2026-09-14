using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 分层与依赖方向的契约（REQ-UI-049、REQ-UI-074）。
///
/// 界面分四层加一个装配根，编号越大越靠上，**依赖只能向下**：
///
/// <list type="table">
///   <item><term>0-Neutral</term><description>中立：指令面、日志、纯工具。不认识任何一层界面。</description></item>
///   <item><term>1-Base</term><description>基础层，页面外壳：停靠、拖出拖入、页面合并、弹窗。
///         **它不知道页面里画的是什么**——只认 <c>ToolWindowDescriptor</c> 这样的纯数据。</description></item>
///   <item><term>2-Components</term><description>组件层，演进层：风格令牌、表格、控制面板、泳道、页面描述与渲染器。
///         别的模块注册页面，事实上就是在消费这一层。</description></item>
///   <item><term>3-HostedPages</term><description>页面层：Aurora 自己托管的那几页。与模块页同一条路，没有特殊待遇。</description></item>
///   <item><term>4-Composition</term><description>装配根：ShellWindow 与内置指令组，把下面四层接起来。没有任何东西可以依赖它。</description></item>
/// </list>
///
/// **1.16.0 起，层不再由这份文件里的字典规定，而由目录本身规定。**
/// 此前层号写在两张硬编码表里（文件夹名→层、根目录散文件名→层），目录则是平铺的：
/// 层是「测试知道、看目录看不出来」的东西。更要命的是所有散文件同在
/// <c>HistoryAurora.Shell</c> 一个命名空间下，跨层引用写作 <c>Selection.X</c>、
/// <c>FrontendCommandCatalog.Source</c> 这样的短名就能就近解析，
/// 而门禁的正则只认全限定形式——于是有三处真实的反向依赖在门禁全绿的情况下活了很多版：
///
/// <list type="bullet">
///   <item>基础层的 <c>MessageBoxConfirmation</c> 直接 <c>is ShellWindow</c> 问装配根要主题；</item>
///   <item>基础层的 <c>ShellTopBarCoordinator</c> 在交互控件清单里列着组件层的 <c>AuroraOptionBox</c>；</item>
///   <item>页面层两处登记命令时引用装配根的 <c>FrontendCommandCatalog.Source</c>。</item>
/// </list>
///
/// 现在目录、命名空间、层号三者对齐：<c>Shell/2-Components/Panels/</c> 里的文件必须声明
/// <c>HistoryAurora.Shell.Components.Panels</c>，任何跨层引用都只能写成带层名的形式，
/// 门禁的正则因此第一次真的覆盖全仓。<see cref="NamespaceFollowsLayerDirectory"/> 守这条对齐。
///
/// 另外两道闸不在本文件里：<c>HistoryAurora.Module.csproj</c> 一层一组 <c>Include</c>，
/// 没分层的顶层目录根本不参与编译；<see cref="EveryShellFileSitsUnderALayer"/>
/// 则确保没人把文件塞回 <c>Shell/</c> 根部。
/// </summary>
public sealed partial class LayerContractTests
{
    private const int Base = 1;
    private const int Component = 2;

    /// <summary>
    /// 层目录名。序号即层号，序号后的那一段即命名空间里的层名。
    /// 加层要同时改这里与 <c>HistoryAurora.Module.csproj</c> 的 <c>Include</c> 组。
    /// </summary>
    private static readonly string[] LayerDirectories =
    [
        "0-Neutral",
        "1-Base",
        "2-Components",
        "3-HostedPages",
        "4-Composition",
    ];

    private static string LayerName(int layer) => LayerDirectories[layer][2..];

    /// <summary>
    /// 引用不只有 using。<c>PageDescription</c> 里的
    /// <c>IReadOnlyList&lt;HistoryAurora.Shell.Components.Panels.PanelWidget&gt;</c> 就是全限定写的——
    /// 只扫 using 的门禁，会被「全限定一下」这种毫无恶意的写法整个绕过。
    /// </summary>
    [GeneratedRegex(@"HistoryAurora\.Shell\.([A-Za-z][A-Za-z0-9]*)")]
    private static partial Regex ShellReference();

    /// <summary>块注释与行注释（<c>///</c> 文档注释也由后者吃掉）。</summary>
    [GeneratedRegex(@"/\*.*?\*/|//[^\n]*", RegexOptions.Singleline)]
    private static partial Regex Comment();

    /// <summary>文件级命名空间声明（本仓统一这一种写法）。</summary>
    [GeneratedRegex(@"^namespace\s+([A-Za-z0-9_.]+)\s*;", RegexOptions.Multiline)]
    private static partial Regex NamespaceDeclaration();

    /// <summary>
    /// **注释必须先剥掉。** 仓里大量注释在正文里写全限定名解释设计意图——本文件自己的
    /// 摘要就提了好几次。不剥注释，这条门禁会报出一串假违规，
    /// 而**误报的门禁最后一定被删掉**，那时真违规也就没人拦了。
    ///
    /// 代价是字符串字面量里的引用扫不到（<c>//</c> 之后一律当注释）。这个方向的错
    /// 是安全的：漏报只是少拦一条，误报会让整条规则失去信用。
    /// </summary>
    private static string CodeOnly(string path)
        => Comment().Replace(File.ReadAllText(path), " ");

    [Fact]
    public void DependenciesOnlyPointDownwards()
    {
        var violations = new List<string>();

        foreach (var file in ShellSources())
        {
            var (layer, label) = LayerOf(file);
            if (layer < 0)
                continue;   // 未分层的文件由下一条测试报出，这里不重复报

            foreach (var referenced in ReferencedLayers(file))
            {
                if (referenced > layer)
                    violations.Add(
                        $"{label} 属于 {LayerName(layer)}，却引用了更上层的 {LayerName(referenced)}");
            }
        }

        Assert.Empty(violations.Distinct().Order());
    }

    /// <summary>
    /// 每个源文件都必须住在某个层目录里。<c>Shell/</c> 根部不再允许有任何散文件——
    /// 它曾经是二十七个文件的收容所，靠一张文件名字典兜底，而那张字典没人维护得动。
    /// 新增一个没人分层的文件夹会在这里失败，而不是悄悄成为规则之外的第六种东西：
    /// 分层一旦有例外，例外就是它唯一会长大的部分。
    /// </summary>
    [Fact]
    public void EveryShellFileSitsUnderALayer()
    {
        var unassigned = ShellSources()
            .Where(file => LayerOf(file).Layer < 0)
            .Select(file => Path.GetRelativePath(ShellRoot(), file))
            .Order()
            .ToList();

        Assert.Empty(unassigned);
    }

    /// <summary>
    /// 目录、命名空间、层号三者必须对齐：<c>Shell/1-Base/Docking/X.cs</c> 只能声明
    /// <c>HistoryAurora.Shell.Base.Docking</c>。
    ///
    /// **这是让上面那条依赖门禁真的生效的前提。** 命名空间不带层名时，同层与跨层的引用
    /// 在源码里长得一模一样（都是 <c>Foo.Bar</c>），正则无从区分；带上层名之后，
    /// 跨层引用必然写出 <c>HistoryAurora.Shell.&lt;层&gt;</c>，一条都藏不住。
    /// </summary>
    [Fact]
    public void NamespaceFollowsLayerDirectory()
    {
        var mismatched = new List<string>();

        foreach (var file in ShellSources())
        {
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                continue;

            var (layer, label) = LayerOf(file);
            if (layer < 0)
                continue;

            var match = NamespaceDeclaration().Match(File.ReadAllText(file));
            if (!match.Success)
            {
                mismatched.Add($"{label} 没有文件级命名空间声明");
                continue;
            }

            var segments = Path.GetRelativePath(ShellRoot(), file)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var expected = string.Join(
                '.',
                new[] { "HistoryAurora", "Shell", LayerName(layer) }.Concat(segments[1..^1]));

            if (!string.Equals(match.Groups[1].Value, expected, StringComparison.Ordinal))
                mismatched.Add($"{label} 声明了 {match.Groups[1].Value}，按目录应为 {expected}");
        }

        Assert.Empty(mismatched.Order());
    }

    /// <summary>
    /// 基础层不认识任何组件。它拿到的是 <c>ToolWindowDescriptor</c> 这样的纯数据，
    /// 页面里画的是表格还是泳道与它无关——**顶栏上的页面动作也必须按这条走**：
    /// 组件层把动作翻译成一份数据清单交下来，基础层不得反过来去问组件。
    ///
    /// 这是三层里最容易破的一条，因为「顶栏要显示这一页的按钮」听起来像是顶栏的事。
    /// 1.16.0 之前它确实被破着：顶栏的交互控件清单里直接列着组件层的
    /// <c>AuroraOptionBox</c>，写成短名就绕过了正则。现在方向反过来——
    /// 组件实现基础层的 <c>IInteractiveCommandControl</c> 标记。
    /// </summary>
    [Fact]
    public void BaseLayerKnowsNothingAboutComponents()
    {
        var leaked = new List<string>();

        foreach (var file in ShellSources())
        {
            var (layer, label) = LayerOf(file);
            if (layer != Base)
                continue;

            foreach (var referenced in ReferencedLayers(file))
            {
                if (referenced >= Component)
                    leaked.Add($"{label} → {LayerName(referenced)}");
            }
        }

        Assert.Empty(leaked.Distinct().Order());
    }

    /// <summary>文件正文里出现的层引用。</summary>
    private static IEnumerable<int> ReferencedLayers(string file)
    {
        foreach (Match match in ShellReference().Matches(CodeOnly(file)))
        {
            var index = Array.FindIndex(
                LayerDirectories,
                directory => directory[2..].Equals(match.Groups[1].Value, StringComparison.Ordinal));

            if (index >= 0)
                yield return index;
        }
    }

    private static (int Layer, string Label) LayerOf(string file)
    {
        var relative = Path.GetRelativePath(ShellRoot(), file);
        var separator = relative.IndexOfAny(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);

        if (separator < 0)
            return (-1, relative);   // Shell/ 根部的散文件：没有层

        return (Array.IndexOf(LayerDirectories, relative[..separator]), relative);
    }

    private static IEnumerable<string> ShellSources()
        => Directory.EnumerateFiles(ShellRoot(), "*.*", SearchOption.AllDirectories)
            .Where(path =>
                (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    private static string ShellRoot()
        => Path.Combine(RepositoryRoot(), "b-Code-Studio", "Shell");

    /// <summary>向上找到含 project.manifest.json 的目录，即仓库根。</summary>
    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "project.manifest.json")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("未找到 HistoryAurora 仓库根目录");
    }
}
