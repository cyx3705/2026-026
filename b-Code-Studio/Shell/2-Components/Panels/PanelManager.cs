using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using HistoryAurora.Shell.Components.Actions;
using HistoryAurora.Shell.Base.Docking;
using HistoryAurora.Shell.Components.Selection;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Shell.Components.Panels;

/// <summary>
/// 控制面板群管理器（REQ-UI-008）：
/// 从 &lt;数据目录&gt;/panels/*.json 加载面板声明，与 C# 注册通道（ShellConfig.Panels）合并；
/// 每个面板注册为一个独立可停靠工具窗口（窗口名 = 面板 id），随布局一起持久化。
/// <c>aurora.ui.panelreload</c> 重读 JSON 并原地重建既有面板内容（新增面板需重启）。
///
/// 声明经 <see cref="PanelDefinitionValidator"/> 逐份校验，不合规的整份跳过并记一条错误——
/// 半个面板会让按钮拿到错的参数，比没有面板更危险。
/// </summary>
internal sealed class PanelManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _panelsDir;
    private readonly CommandBus _bus;
    private readonly IShellLog _log;
    private readonly ActionRegistry _actions;
    private readonly SelectionChannels? _channels;
    private readonly Dictionary<string, PanelView> _views = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PanelDefinition> _definitions = new();

    /// <summary>仅供框架命令目录生成；不读取文件，也不得执行面板处理器。</summary>
    internal PanelManager()
    {
        _panelsDir = "";
        _bus = null!;
        _log = null!;
        _actions = null!;
        _channels = null;
    }

    public PanelManager(
        string panelsDir,
        IEnumerable<PanelDefinition>? configured,
        CommandBus bus,
        IShellLog log,
        ActionRegistry actions,
        SelectionChannels? channels = null)
    {
        _panelsDir = panelsDir;
        _bus = bus;
        _log = log;
        _actions = actions;
        _channels = channels;
        Directory.CreateDirectory(panelsDir);

        if (configured != null)
        {
            foreach (var definition in configured)
                TryAdd(definition, "ShellConfig");
        }

        LoadJsonFiles();
    }

    public IReadOnlyList<PanelDefinition> Definitions => _definitions;

    /// <summary>把每个面板注册为工具窗口描述符（启动时调用，先于停靠系统初始化）。</summary>
    public void RegisterWindows(List<ToolWindowDescriptor> windows)
    {
        foreach (var def in _definitions)
        {
            if (windows.Any(w => w.Id.Equals(def.Id, StringComparison.OrdinalIgnoreCase)))
            {
                _log.Error("panel", $"面板 id 与已注册窗口冲突，已跳过: {def.Id}");
                continue;
            }

            var captured = def;
            windows.Add(new ToolWindowDescriptor
            {
                Id = def.Id,
                Title = def.Title,
                DefaultSide = ParseSide(def.Side),
                DefaultRatio = def.Ratio,
                DefaultVisible = def.Visible,
                ContentFactory = () => GetView(captured),
            });
        }
    }

    /// <summary>aurora.ui.panelset 落点。</summary>
    public bool TrySetValue(string panelId, string controlId, string value, out string error)
    {
        error = "";
        if (!_views.TryGetValue(panelId, out var view))
        {
            var known = string.Join(" / ", _definitions.Select(d => d.Id));
            error = $"没有名为 {panelId} 的面板。已定义: {(known.Length > 0 ? known : "(无)")}";
            return false;
        }

        if (!view.TrySetValue(controlId, value))
        {
            error = $"面板 {panelId} 没有可写控件 {controlId}。可用: {string.Join(" / ", view.ControlIds)}";
            return false;
        }

        return true;
    }

    /// <summary>aurora.ui.panelreload：重读 JSON，重建既有面板内容；新增面板提示重启。</summary>
    public string Reload()
    {
        var before = _definitions.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _definitions.Clear();
        LoadJsonFiles();

        var rebuilt = 0;
        var pendingRestart = new List<string>();
        foreach (var def in _definitions)
        {
            if (_views.TryGetValue(def.Id, out var view))
            {
                view.Rebuild(def);
                rebuilt++;
            }
            else if (!before.Contains(def.Id))
            {
                pendingRestart.Add(def.Id);
            }
        }

        var message = $"已重载 {rebuilt} 个面板";
        if (pendingRestart.Count > 0)
            message += $"；新增面板需重启后生效: {string.Join(" / ", pendingRestart)}";
        return message;
    }

    /// <summary>
    /// 按当前声明重建全部已实例化的面板。动作声明刷新后必须调用：
    /// 按钮的「有没有落点」是在构建时决定的，声明变了而界面不重建，
    /// 就会留下一块已经过期的警示牌，或者反过来留一个其实已经失效的按钮。
    /// </summary>
    public int RebuildAll()
    {
        foreach (var def in _definitions)
        {
            if (_views.TryGetValue(def.Id, out var view))
                view.Rebuild(def);
        }

        return _views.Count;
    }

    private PanelView GetView(PanelDefinition def)
    {
        if (!_views.TryGetValue(def.Id, out var view))
        {
            view = new PanelView(def, _bus, _log, _actions, _channels);
            _views[def.Id] = view;
        }

        return view;
    }

    private void LoadJsonFiles()
    {
        foreach (var file in Directory.EnumerateFiles(_panelsDir, "*.json")
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var def = JsonSerializer.Deserialize<PanelDefinition>(File.ReadAllText(file), JsonOptions);
                TryAdd(def, Path.GetFileName(file));
            }
            catch (Exception ex)
            {
                // 单个配置损坏不阻断其余面板。
                _log.Error("panel", $"面板配置解析失败，已跳过 {Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }

    private void TryAdd(PanelDefinition? definition, string origin)
    {
        var parsed = PanelDefinitionValidator.Validate(definition);
        if (!parsed.Ok)
        {
            _log.Error("panel", $"面板声明无效，已跳过（{origin}）: {parsed.Error}");
            return;
        }

        var value = parsed.Value!;
        if (_definitions.Any(d => d.Id.Equals(value.Id, StringComparison.OrdinalIgnoreCase)))
        {
            _log.Error("panel", $"面板 id 重复，已跳过: {value.Id}（{origin}）");
            return;
        }

        _definitions.Add(value);
    }

    private static DockSide ParseSide(string side) => side.ToLowerInvariant() switch
    {
        "left" => DockSide.Left,
        "top" => DockSide.Top,
        "bottom" => DockSide.Bottom,
        "center" => DockSide.Center,
        _ => DockSide.Right,
    };
}
