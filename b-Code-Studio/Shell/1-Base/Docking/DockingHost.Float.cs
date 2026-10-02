using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
/// </summary>
internal sealed partial class DockingHost
{
    /// <summary>首次浮出、且原页面量不出尺寸时卡片的大小。</summary>
    private static readonly Size DefaultFloatSize = new(760, 460);

    /// <summary>首次浮出时卡片不超过这么大：中央区的页面常常铺满整屏，原样浮出来就把 SolidWorks 盖住了。</summary>
    private static readonly Size MaximumInitialFloatSize = new(820, 520);

    private sealed record FloatSession(PageFloatWindow Window, FrameworkElement Placeholder);

    private readonly Dictionary<string, FloatSession> _floats = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>每页上次浮着时卡片的位置与大小（DIP），只在本次运行内记着。</summary>
    private readonly Dictionary<string, Rect> _floatBounds = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>退出前把浮着的页全搬回去：布局按停靠的样子存，下次启动不会少一页。</summary>
    public void RestoreAllFloats()
    {
        foreach (var id in _floats.Keys.ToList())
            RestorePage(id);
    }

    /// <summary>布局节点该显示的内容：浮着时是占位，否则是页面本身。</summary>
    private object? DockedContent(string id, object? content)
        => _floats.TryGetValue(id, out var session) ? session.Placeholder : content;

    private void FloatPage(ToolWindowDescriptor descriptor)
    {
        var id = descriptor.Id;
        if (GetOrCreateContent(descriptor) is not FrameworkElement page)
            throw new InvalidOperationException($"窗口 {id} 的内容不是界面元素，不能浮出");

        var card = _floatBounds.TryGetValue(id, out var remembered) ? remembered : InitialCardBounds(page);
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
        window.Show();
        _log.Info(LayoutSource, $"{id} 已浮出到置顶小窗");
        FloatChanged?.Invoke(this, id);
        WindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RestorePage(string id)
    {
        if (!_floats.Remove(id, out var session))
            return;

        var window = session.Window;
        _floatBounds[id] = window.CardBounds;
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
        _log.Info(LayoutSource, $"{id} 已还原到停靠区");
        FloatChanged?.Invoke(this, id);
        WindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>页面被注销（模块卸载）时浮窗直接关掉，不往回搬：内容随注销一起处置。</summary>
    private void DropFloat(string id)
    {
        if (!_floats.Remove(id, out var session))
            return;
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
