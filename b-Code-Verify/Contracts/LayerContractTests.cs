using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 分层与依赖方向的契约（REQ-UI-049）。
///
/// 界面分四层加一个装配根，编号越大越靠上，**依赖只能向下**：
///
/// <list type="table">
///   <item><term>0 中立</term><description>指令面、日志、纯工具。不认识任何一层界面。</description></item>
///   <item><term>1 基础层</term><description>页面外壳：停靠、顶栏、浮窗、拖出拖入、页面合并、弹窗。
///         **它不知道页面里画的是什么**——只认 <c>ToolWindowDescriptor</c> 这样的纯数据。</description></item>
///   <item><term>2 组件层</term><description>演进层：风格令牌、表格、控制面板、泳道、页面描述与渲染器。
///         别的模块注册页面，事实上就是在消费这一层。</description></item>
///   <item><term>3 页面层</term><description>Aurora 自己托管的那几页。</description></item>
///   <item><term>4 装配根</term><description>ShellWindow 与内置指令组，把上面四层接起来。没有任何东西可以依赖它。</description></item>
/// </list>
///
/// **这条测试的价值不在今天。** 写它的时候仓里一处反向依赖都没有——三层是
/// 事实存在但没被承认的状态。没有门禁的分层会在第一次「就这一次」里破掉，
/// 而破掉的当天没有任何症状；等到基础层为了显示一个按钮去 using 组件层，
/// 再想拆开就已经晚了。
///
/// 同样重要的是 <see cref="EveryShellFileIsAssignedToALayer"/>：新目录必须先被
/// 分配层级才能过测试。没有这一条，加一个没人分层的文件夹就等于绕过整套规则。
/// </summary>
public sealed partial class LayerContractTests
{
    private const int Neutral = 0;
    private const int Base = 1;
    private const int Component = 2;
    private const int HostedPage = 3;
    private const int CompositionRoot = 4;

    private static readonly IReadOnlyDictionary<string, int> FolderLayers =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["CommandSurface"] = Neutral,
            ["Logging"] = Neutral,
            ["Properties"] = Neutral,

            ["Docking"] = Base,
            ["Dialogs"] = Base,

            ["Themes"] = Component,
            ["Table"] = Component,
            ["Panels"] = Component,
            ["Widgets"] = Component,
            ["Graph"] = Component,
            ["Pages"] = Component,
            ["Selection"] = Component,
            ["Actions"] = Component,
            ["Modules"] = Component,

            ["Views"] = HostedPage,
            ["Console"] = HostedPage,
        };

    /// <summary>Shell 根目录下的散文件各归其层；根目录不是「没有层」的避难所。</summary>
    private static readonly IReadOnlyDictionary<string, int> RootFileLayers =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["CoalescingAsyncWork.cs"] = Neutral,
            ["CommandResultData.cs"] = Neutral,

            ["ShellTopBarCoordinator.cs"] = Base,
            ["ShellTopBarCoordinator.VisualTree.cs"] = Base,
            ["FloatingWindowGeometry.cs"] = Base,
            ["FloatingWindowTheme.cs"] = Base,
            ["DelayedDragGesture.cs"] = Base,
            ["WindowForegroundActivator.cs"] = Base,
            ["MessageBoxConfirmation.cs"] = Base,

            ["ShellConfig.cs"] = CompositionRoot,
            ["FrontendCommandCatalog.cs"] = CompositionRoot,
            ["HostedPageActions.cs"] = CompositionRoot,
            ["ShellWindow.xaml.cs"] = CompositionRoot,
            ["ShellWindow.Chrome.cs"] = CompositionRoot,
            ["ShellWindow.ChromeMenus.cs"] = CompositionRoot,
            ["ShellWindow.Pages.cs"] = CompositionRoot,
            ["BuiltinCommands.cs"] = CompositionRoot,
            ["BuiltinCommands.Actions.cs"] = CompositionRoot,
            ["BuiltinCommands.App.cs"] = CompositionRoot,
            ["BuiltinCommands.Basics.cs"] = CompositionRoot,
            ["BuiltinCommands.Channels.cs"] = CompositionRoot,
            ["BuiltinCommands.Dialog.cs"] = CompositionRoot,
            ["BuiltinCommands.Layout.cs"] = CompositionRoot,
            ["BuiltinCommands.Log.cs"] = CompositionRoot,
            ["BuiltinCommands.Pages.cs"] = CompositionRoot,
            ["BuiltinCommands.Panel.cs"] = CompositionRoot,
            ["BuiltinCommands.Window.cs"] = CompositionRoot,
        };

    private static readonly string[] LayerNames =
        ["中立", "基础层", "组件层", "页面层", "装配根"];

    /// <summary>
    /// 引用不只有 using。<c>PageDescription</c> 里的
    /// <c>IReadOnlyList&lt;HistoryAurora.Shell.Panels.PanelWidget&gt;</c> 就是全限定写的——
    /// 只扫 using 的门禁，会被「全限定一下」这种毫无恶意的写法整个绕过。
    /// </summary>
    [GeneratedRegex(@"HistoryAurora\.Shell\.([A-Za-z][A-Za-z0-9]*)")]
    private static partial Regex ShellReference();

    /// <summary>块注释与行注释（<c>///</c> 文档注释也由后者吃掉）。</summary>
    [GeneratedRegex(@"/\*.*?\*/|//[^\n]*", RegexOptions.Singleline)]
    private static partial Regex Comment();

    /// <summary>
    /// **注释必须先剥掉。** 仓里大量注释在正文里写全限定名解释设计意图——
    /// <c>Properties/AssemblyInfo.cs</c> 通篇在讲「停靠系统为什么收成 internal」，
    /// 一个 <c>using</c> 都没有却提了 <c>HistoryAurora.Shell.Docking</c> 三次。
    /// 不剥注释，这条门禁第一次跑就报三条假违规——而**误报的门禁最后一定被删掉**，
    /// 那时真违规也就没人拦了。
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

            foreach (Match match in ShellReference().Matches(CodeOnly(file)))
            {
                var folder = match.Groups[1].Value;
                if (!FolderLayers.TryGetValue(folder, out var referenced))
                    continue;   // 不是分层目录（命名空间根下的类型），跳过

                if (referenced > layer)
                    violations.Add(
                        $"{label} 属于{LayerNames[layer]}，却引用了{LayerNames[referenced]}的 {folder}");
            }
        }

        Assert.Empty(violations.Distinct().Order());
    }

    /// <summary>
    /// 每个源文件都必须有层。新增一个没人分层的文件夹会在这里失败，而不是悄悄
    /// 成为规则之外的第五种东西——分层一旦有例外，例外就是它唯一会长大的部分。
    /// </summary>
    [Fact]
    public void EveryShellFileIsAssignedToALayer()
    {
        var unassigned = ShellSources()
            .Where(file => LayerOf(file).Layer < 0)
            .Select(file => Path.GetRelativePath(ShellRoot(), file))
            .Order()
            .ToList();

        Assert.Empty(unassigned);
    }

    /// <summary>
    /// 基础层不认识任何组件。它拿到的是 <c>ToolWindowDescriptor</c> 这样的纯数据，
    /// 页面里画的是表格还是泳道与它无关——**顶栏上的页面动作也必须按这条走**：
    /// 组件层把动作翻译成一份数据清单交下来，基础层不得反过来去问组件。
    ///
    /// 这是三层里最容易破的一条，因为「顶栏要显示这一页的按钮」听起来像是顶栏的事。
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

            foreach (Match match in ShellReference().Matches(CodeOnly(file)))
            {
                var folder = match.Groups[1].Value;
                if (FolderLayers.TryGetValue(folder, out var referenced) && referenced >= Component)
                    leaked.Add($"{label} → {folder}");
            }
        }

        Assert.Empty(leaked.Distinct().Order());
    }

    private static (int Layer, string Label) LayerOf(string file)
    {
        var relative = Path.GetRelativePath(ShellRoot(), file);
        var separator = relative.IndexOfAny(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);

        if (separator < 0)
        {
            return RootFileLayers.TryGetValue(relative, out var rootLayer)
                ? (rootLayer, relative)
                : (-1, relative);
        }

        var folder = relative[..separator];
        return FolderLayers.TryGetValue(folder, out var layer)
            ? (layer, relative)
            : (-1, relative);
    }

    private static IEnumerable<string> ShellSources()
        => Directory.EnumerateFiles(ShellRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
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
