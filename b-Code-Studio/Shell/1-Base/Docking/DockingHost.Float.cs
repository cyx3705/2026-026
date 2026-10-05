using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AvalonDock.Layout;
using HistoryVulcan.Core.Logging;
using HistoryAurora.Shell.Neutral.Logging;

namespace HistoryAurora.Shell.Base.Docking;

/// <summary>
/// 页面浮窗（REQ-UI-138，1.30.1）：<c>aurora.ui.float</c> 的落点。
///
/// 浮出 = 页面对象搬进一个置顶的 <see cref="PageFloatWindow"/>，停靠区原位换成一块「已浮出」占位；
/// 还原 = 搬回来。停靠模型（位置、比例、显隐、场景快照）一律不动——浮着的页在布局里照旧占着它那一格，
/// 只是那一格里显示的是占位。所以切场景、存布局、热重载都不需要知道「浮着」这件事，
/// 只有给布局节点发内容的两处（<see cref="CreateAnchorable"/>、<see cref="ReplaceWindow"/>）问一句
/// <see cref="DockedContent"/>。
///
/// 1.32.0（REQ-UI-143）起浮窗跨重启：每页「浮没浮着、卡片在哪多大」记进设置 <c>aurora.ui.floats</c>。
/// 退出时页面照旧搬回停靠区（浮窗没有 Owner，不收进程关不掉），但账上仍记「浮着」；
/// 下次这一页登记进来，就在上次的位置重新浮出。只有人亲手还原（再执行一次、点占位的「还原」、
/// Alt+F4 关浮窗）才把账改成「不浮」。
/// </summary>
internal sealed partial class DockingHost
{
    private const string FloatSettingsKey = "aurora.ui.floats";

    private static readonly JsonSerializerOptions FloatJsonOptions = new() { WriteIndented = false };

    /// <summary>首次浮出、且原页面量不出尺寸时卡片的大小。</summary>
    private static readonly Size DefaultFloatSize = new(760, 460);

    /// <summary>首次浮出时卡片不超过这么大：中央区的页面常常铺满整屏，原样浮出来就把 SolidWorks 盖住了。</summary>
    private static readonly Size MaximumInitialFloatSize = new(820, 520);

    private sealed record FloatSession(PageFloatWindow Window, FrameworkElement Placeholder);

    private readonly Dictionary<string, FloatSession> _floats = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 浮窗账的一条：卡片的位置与大小（DIP），以及人最后一次让它浮着还是收回。
    /// 公开属性是为了走 System.Text.Json；只在本文件里用。
    /// </summary>
    private sealed record FloatRecord(double Left, double Top, double Width, double Height, bool Floating)
    {
        public Rect ToCard() => new(Left, Top, Width, Height);

        public static FloatRecord Of(Rect card, bool floating)
            => new(Math.Round(card.Left), Math.Round(card.Top), Math.Round(card.Width), Math.Round(card.Height), floating);
    }

    /// <summary>浮窗账（REQ-UI-143）。首次用到时从设置读；没有设置服务（测试、停靠层单用）时只在本次运行内记着。</summary>
    private Dictionary<string, FloatRecord>? _floatLedger;

    /// <summary>拖动、拉伸浮窗时位置一秒变几十次：停手后再落盘一次。</summary>
    private DispatcherTimer? _floatSaveDebounce;

    private bool _refloatQueued;

    private bool _restoringFloat;

    /// <summary>浮出或还原了某一页。参数是页 id。</summary>
    public event EventHandler<string>? FloatChanged;

    public bool IsPageFloating(string id) => _floats.ContainsKey(id);

    /// <summary>这一页此刻所在的浮窗；没浮着为 null。</summary>
    internal PageFloatWindow? FloatWindowOf(string id)
        => _floats.TryGetValue(id, out var session) ? session.Window : null;

    /// <summary>此刻浮着的页 id。</summary>
    public IReadOnlyList<string> FloatingPageIds => _floats.Keys.ToList();

    /// <summary>
    /// 浮出或还原一页。<paramref name="on"/> 为 null 时切换。
    /// </summary>
    /// <returns>调用后这一页是否浮着。</returns>
    public bool SetPageFloating(string id, bool? on = null)
    {
        if (!_byId.TryGetValue(id, out var descriptor))
            throw new ArgumentException($"未注册的窗口: {id}", nameof(id));

        var target = on ?? !_floats.ContainsKey(id);
        if (target == _floats.ContainsKey(id))
        {
            if (target)
                _floats[id].Window.Activate();
            return target;
        }

        if (target)
            FloatPage(descriptor);
        else
            RestorePage(id);
        return target;
    }

    /// <summary>
    /// 退出前把浮着的页全搬回去：布局按停靠的样子存，下次启动不会少一页。
    /// 这不是人要还原，账上仍记「浮着」（REQ-UI-143），下次启动在原位重新浮出。
    /// </summary>
    public void RestoreAllFloats()
    {
        foreach (var id in _floats.Keys.ToList())
            RestorePage(id, stillFloating: true);
    }

    /// <summary>
    /// 账上记着「浮着」、此刻已登记却没浮着的页，重新浮出（REQ-UI-143）。
    /// 启动恢复完布局、以及之后每登记一页都排一次；排在 ApplicationIdle 上，
    /// 等停靠区把这一页摆好再搬，卡片位置用账上的，不用去量页面。
    /// </summary>
    private void ScheduleRefloat()
    {
        if (_refloatQueued || !FloatLedger().Values.Any(memory => memory.Floating))
            return;

        _refloatQueued = true;
        _manager.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            _refloatQueued = false;
            foreach (var (id, memory) in FloatLedger().ToList())
            {
                if (!memory.Floating || !_byId.ContainsKey(id) || _floats.ContainsKey(id))
                    continue;
                try
                {
                    SetPageFloating(id, true);
                }
                catch (Exception ex)
                {
                    // 一页浮不出来不能挡住别的页，也不改账：下次登记再试。
                    _log.Warn(LayoutSource, $"{id} 恢复浮窗失败: {ex.Message}");
                }
            }
        }));
    }

    /// <summary>布局节点该显示的内容：浮着时是占位，否则是页面本身。</summary>
    private object? DockedContent(string id, object? content)
        => _floats.TryGetValue(id, out var session) ? session.Placeholder : content;

    private void FloatPage(ToolWindowDescriptor descriptor)
    {
        var id = descriptor.Id;
        if (GetOrCreateContent(descriptor) is not FrameworkElement page)
            throw new InvalidOperationException($"窗口 {id} 的内容不是界面元素，不能浮出");

        var card = FloatLedger().TryGetValue(id, out var remembered) ? remembered.ToCard() : InitialCardBounds(page);
        var placeholder = BuildPlaceholder(descriptor.Title, id);
        var window = new PageFloatWindow(id, descriptor.Title, FloatResources());

        // 先把占位挂上布局节点、让停靠区立即重排，页面才真正离开原来的可视树；
        // 否则它同一时刻有两个可视父级，WPF 直接抛。
        _floats[id] = new FloatSession(window, placeholder);
        SetNodeContents(id, placeholder);
        _manager.UpdateLayout();

        window.Page = page;
        var bounds = PageFloatWindow.BoundsForCard(KeepOnScreen(card));
        window.Left = bounds.Left;
        window.Top = bounds.Top;
        window.Width = bounds.Width;
        window.Height = bounds.Height;
        window.Closed += (_, _) =>
        {
            // Alt+F4 之类绕过还原直接关窗：页面不能跟着窗口一起丢。
            if (!_restoringFloat && _floats.TryGetValue(id, out var session) && ReferenceEquals(session.Window, window))
                RestorePage(id);
        };
        window.LocationChanged += (_, _) => ScheduleFloatSave();
        window.SizeChanged += (_, _) => ScheduleFloatSave();
        window.Show();
        RememberFloat(id, window.CardBounds, floating: true);
        _log.Info(LayoutSource, $"{id} 已浮出到置顶小窗");
        FloatChanged?.Invoke(this, id);
        WindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <param name="stillFloating">
    /// true = 退出时的收回，账上仍记「浮着」；false = 人亲手还原，下次启动不再浮出。
    /// </param>
    private void RestorePage(string id, bool stillFloating = false)
    {
        if (!_floats.Remove(id, out var session))
            return;

        var window = session.Window;
        RememberFloat(id, window.CardBounds, stillFloating);
        window.Page = null;
        _restoringFloat = true;
        try
        {
            if (window.IsLoaded)
                window.Close();
        }
        finally
        {
            _restoringFloat = false;
        }

        // 窗口里那一页可能已被热重载换过（ReplaceWindow），搬回去的是 _contents 里当前那一份。
        SetNodeContents(id, _contents.GetValueOrDefault(id));
        _log.Info(LayoutSource, stillFloating ? $"{id} 退出前收回停靠区（下次启动在原位浮出）" : $"{id} 已还原到停靠区");
        FloatChanged?.Invoke(this, id);
        WindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 页面被注销（模块卸载）时浮窗直接关掉，不往回搬：内容随注销一起处置。
    /// 账上仍记「浮着」：模块重新载入、这一页再登记进来时在原位浮回去（REQ-UI-143）。
    /// </summary>
    private void DropFloat(string id)
    {
        if (!_floats.Remove(id, out var session))
            return;
        RememberFloat(id, session.Window.CardBounds, floating: true);
        session.Window.Page = null;
        _restoringFloat = true;
        try
        {
            session.Window.Close();
        }
        finally
        {
            _restoringFloat = false;
        }

        FloatChanged?.Invoke(this, id);
    }

    /// <summary>热重载换了内容：浮着的话新内容直接进浮窗。</summary>
    private void ReplaceFloatingContent(string id, object? content)
    {
        if (_floats.TryGetValue(id, out var session))
            session.Window.Page = content;
    }

    /// <summary>浮窗账；首次用到时从设置读，读坏了当没记过——浮窗位置是个便利，不值得挡住启动。</summary>
    private Dictionary<string, FloatRecord> FloatLedger()
    {
        if (_floatLedger != null)
            return _floatLedger;

        _floatLedger = new Dictionary<string, FloatRecord>(StringComparer.OrdinalIgnoreCase);
        if (_settings?.Get(FloatSettingsKey) is not { Length: > 0 } raw)
            return _floatLedger;
        try
        {
            foreach (var (id, memory) in JsonSerializer.Deserialize<Dictionary<string, FloatRecord>>(raw, FloatJsonOptions) ?? new())
            {
                if (memory is { Width: > 0, Height: > 0 } && double.IsFinite(memory.Left) && double.IsFinite(memory.Top))
                    _floatLedger[id] = memory;
            }
        }
        catch (JsonException ex)
        {
            _log.Warn(LayoutSource, $"浮窗位置记录无法读取，已忽略: {ex.Message}");
        }

        return _floatLedger;
    }

    private void RememberFloat(string id, Rect card, bool floating)
    {
        if (card.Width <= 0 || card.Height <= 0 || !double.IsFinite(card.Left) || !double.IsFinite(card.Top))
            return;
        FloatLedger()[id] = FloatRecord.Of(card, floating);
        SaveFloatLedger();
    }

    /// <summary>浮窗被拖动或拉伸：停手 400ms 后把所有浮着的卡片位置记一次。</summary>
    private void ScheduleFloatSave()
    {
        if (_floatSaveDebounce == null)
        {
            _floatSaveDebounce = new DispatcherTimer(DispatcherPriority.Background, _manager.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(400),
            };
            _floatSaveDebounce.Tick += (_, _) =>
            {
                _floatSaveDebounce.Stop();
                var memory = FloatLedger();
                foreach (var (id, session) in _floats)
                {
                    var card = session.Window.CardBounds;
                    if (card.Width > 0 && card.Height > 0)
                        memory[id] = FloatRecord.Of(card, floating: true);
                }

                SaveFloatLedger();
            };
        }

        _floatSaveDebounce.Stop();
        _floatSaveDebounce.Start();
    }

    private void SaveFloatLedger()
    {
        if (_settings == null)
            return;
        try
        {
            _settings.Set(FloatSettingsKey, JsonSerializer.Serialize(FloatLedger(), FloatJsonOptions));
        }
        catch (Exception ex)
        {
            _log.Warn(LayoutSource, $"保存浮窗位置失败: {ex.Message}");
        }
    }

    /// <summary>布局树里这一页的全部节点（含隐藏的）换内容。</summary>
    private void SetNodeContents(string id, object? content)
    {
        var root = _manager.Layout;
        foreach (var node in root.Descendents().OfType<LayoutContent>().Concat(root.Hidden)
                     .Where(node => string.Equals(node.ContentId, id, StringComparison.OrdinalIgnoreCase))
                     .Distinct())
        {
            node.Content = content;
        }
    }

    /// <summary>主窗体与停靠管理器的资源：页面在停靠区里就是从这两层取令牌和样式的。</summary>
    private IEnumerable<ResourceDictionary> FloatResources()
    {
        if (Window.GetWindow(_manager) is { } shell)
            yield return shell.Resources;
        yield return _manager.Resources;
    }

    /// <summary>首次浮出：卡片落在页面原来的位置，大小不超过 <see cref="MaximumInitialFloatSize"/>。</summary>
    private Rect InitialCardBounds(FrameworkElement page)
    {
        if (page.IsVisible && page.ActualWidth > 0 && page.ActualHeight > 0
            && PresentationSource.FromVisual(page)?.CompositionTarget is { } target)
        {
            var origin = target.TransformFromDevice.Transform(page.PointToScreen(new Point(0, 0)));
            return new Rect(
                origin,
                new Size(
                    Math.Min(page.ActualWidth, MaximumInitialFloatSize.Width),
                    Math.Min(page.ActualHeight, MaximumInitialFloatSize.Height)));
        }

        // 页面藏着量不出来：放在主窗体正中。
        var shell = Window.GetWindow(_manager);
        var center = shell is { ActualWidth: > 0 }
            ? new Point(shell.Left + shell.ActualWidth / 2, shell.Top + shell.ActualHeight / 2)
            : new Point(SystemParameters.WorkArea.Width / 2, SystemParameters.WorkArea.Height / 2);
        return new Rect(
            center.X - DefaultFloatSize.Width / 2,
            center.Y - DefaultFloatSize.Height / 2,
            DefaultFloatSize.Width,
            DefaultFloatSize.Height);
    }

    /// <summary>卡片至少留一截在虚拟屏幕里：上次拖到副屏、副屏拔了，不能浮到看不见的地方去。</summary>
    private static Rect KeepOnScreen(Rect card)
    {
        var width = Math.Max(card.Width, PageFloatWindow.MinimumWidth - 2 * PageFloatWindow.Gutter);
        var height = Math.Max(card.Height, PageFloatWindow.MinimumHeight - 2 * PageFloatWindow.Gutter);
        var screen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        var left = Math.Clamp(card.Left, screen.Left, Math.Max(screen.Left, screen.Right - width));
        var top = Math.Clamp(card.Top, screen.Top, Math.Max(screen.Top, screen.Bottom - height));
        return new Rect(left, top, width, height);
    }

    /// <summary>停靠区原位的占位：说明页面去哪了，给一个「还原」。</summary>
    private FrameworkElement BuildPlaceholder(string title, string id)
    {
        var text = new TextBlock
        {
            Text = $"「{title}」已浮出到置顶小窗",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
        };
        text.SetResourceReference(TextBlock.StyleProperty, "Aurora.Panel.Label");

        var restore = new Button
        {
            Content = "还原",
            HorizontalAlignment = HorizontalAlignment.Center,
            ToolTip = $"把「{title}」放回这里（aurora.ui.float name={id}）",
        };
        restore.SetResourceReference(FrameworkElement.StyleProperty, "Aurora.Button.Ghost");
        restore.Click += (_, _) => SetPageFloating(id, false);

        return new Grid
        {
            Background = Brushes.Transparent,
            Children =
            {
                new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children = { text, restore },
                },
            },
        };
    }
}
