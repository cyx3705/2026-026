using HistoryAurora.Shell.Docking;

namespace HistoryAurora.Shell;

internal sealed record ShellMenuAction(string Header, string CommandText);

internal enum ShellCloseBehavior
{
    Exit,
    Hide,
}

/// <summary>
/// 派生应用向 Shell 提交的装配清单(§9 开发流程第 2/7 条的入口)。
/// </summary>
internal sealed class ShellConfig
{
    public required string AppName { get; init; }

    public required string AppVersion { get; init; }

    /// <summary>要注册的工具窗口清单。</summary>
    public List<ToolWindowDescriptor> ToolWindows { get; } = new();

    /// <summary>派生应用放入“工具”菜单的命令入口。</summary>
    public List<ShellMenuAction> ToolMenuActions { get; } = new();

    /// <summary>
    /// 派生应用注册自定义指令的挂点(§5.3,§9 流程第 3 条):
    /// 在内置指令组注册完成后调用;指令名冲突会在此时抛出。
    /// </summary>
    public Action<HistoryVulcan.Core.Commands.CommandRegistry>? ConfigureCommands { get; set; }

    /// <summary>
    /// C# 通道声明的控制面板(P-01;与数据目录 panels/*.json 合并,JSON 优先加载在后)。
    /// </summary>
    public List<HistoryAurora.Shell.Panels.PanelDefinition> Panels { get; } = new();

    // ---------------------------------------------------------------- 0.4.4 反哺能力(消费方显式启用)

    /// <summary>
    /// 模块托管(MD-01~08,0.4.4 由 HistoryJanus 反哺):
    /// &lt;数据目录&gt;\Modules 热重载,DLL 即指令域;module.* 指令组随之注册。
    /// 默认关闭；消费方明确需要模块扫描、热重载和 module.* 时置 true。
    /// false 时完全不创建宿主、不扫描目录、不注册 module.*。
    /// </summary>
    public bool EnableModules { get; set; }

    /// <summary>
    /// 在本进程加载声明了 ui=true 的模块界面。默认由 EnableModules 隐式启用;
    /// 前端/服务分离应用可在 EnableModules=false 时单独置 true。
    /// </summary>
    public bool EnableUiModules { get; set; }

    /// <summary>
    /// Optional module directory for a packaged host. When omitted, modules remain
    /// under the application's standard AppData directory.
    /// </summary>
    public string? ModuleDirectory { get; set; }

    /// <summary>
    /// Z-level module discovery roots. When non-empty, module manifests are discovered from
    /// direct <c>z-*</c> children and <see cref="ModuleDirectory"/> is ignored.
    /// </summary>
    public List<string> ModuleDiscoveryRoots { get; } = new();

    /// <summary>
    /// Keeps a UI-only module host empty until the backend confirms the manifest paths to load.
    /// </summary>
    public bool RequireConfirmedModuleSources { get; set; }

    /// <summary>双击工具窗口标题条时切换窗口最大化。</summary>
    public bool EnableMaximizeOnDoubleClick { get; set; } = true;

    /// <summary>
    /// 客户端模式下只创建命令集、指令详情和模块管理视图，不在本进程创建 MCP 或 ModuleHost。
    /// 视图经 CommandBus.RemoteExecutor 读取服务端结构化结果。
    /// </summary>
    public bool EnableRemoteManagementViews { get; set; }

    /// <summary>Determines whether a user close hides the frontend or exits it.</summary>
    public ShellCloseBehavior CloseBehavior { get; set; } = ShellCloseBehavior.Exit;

    /// <summary>
    /// 应用身份:null 时取 <c>AppIdentity.Current</c>(入口程序集)。
    /// 测试宿主等入口程序集不是应用本体的场景应显式提供。
    /// </summary>
    public HistoryVulcan.Core.ApplicationIdentity? Identity { get; set; }

    /// <summary>
    /// 命令集选中状态(0.4.4):框架的命令集窗口(McpToolsView)写入选中的指令名。
    /// 派生应用若有指令详情窗口需与之联动,应在此传入**同一个实例**,并把详情窗口也接到它;
    /// null 时框架自建一个(此时派生侧无法与之联动)。
    /// 由派生应用创建并传入(而非框架创建后回取),是因为工具窗口内容工厂在 ShellWindow
    /// 构造期间(DockingHost 构建默认布局时)即被调用,那时派生应用尚拿不到 window 实例。
    /// </summary>
    public HistoryVulcan.Core.Commands.CommandSelectionState? CommandSelection { get; set; }
}
