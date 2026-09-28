using System.Windows;
using System.Windows.Controls;

namespace HistoryAurora.Shell.Components.Widgets;

/// <summary>
/// 段标题 + 墨色条（REQ-UI-132，1.28.0）。OneHistory 文艺工业风的招牌模块：
/// 标题后接一段实心墨色圆角条，一直延伸到右缘，条里可以反白写一句说明（右对齐）。
/// 与站点首页「项目库 ▬ 共 N 个项目」、Office「标题 1 + 墨色条」是同一个东西，
/// 权威在 2026-031 z-OneHistoryID/网站风格.md §2.3。
///
/// 长相全在 <c>Aurora.Heading.*</c> 三个样式里，这里只负责拼。
/// </summary>
internal sealed class AuroraSectionHeading : DockPanel
{
    private readonly TextBlock _title = new();
    private readonly TextBlock _note = new();

    public AuroraSectionHeading()
    {
        // 不自带控件字典：它只作为部件嵌在已经 Ensure 过的组件（泳道图）或主窗体里。
        LastChildFill = true;
        _title.SetResourceReference(StyleProperty, "Aurora.Heading.Title");
        _note.SetResourceReference(StyleProperty, "Aurora.Heading.Note");
        var bar = new Border { Child = _note };
        bar.SetResourceReference(StyleProperty, "Aurora.Heading.Bar");
        SetDock(_title, Dock.Left);
        Children.Add(_title);
        Children.Add(bar);
        Note = "";
    }

    /// <summary>标题文字（墨色、半粗）。</summary>
    public string Title
    {
        get => _title.Text;
        set => _title.Text = value ?? "";
    }

    /// <summary>墨色条里反白的一句说明；空串时条仍在，只是不写字。</summary>
    public string Note
    {
        get => _note.Text;
        set
        {
            _note.Text = value ?? "";
            _note.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>
    /// 按「标题 · 说明」的约定拆开：最后一个 <c>" · "</c> 之前是标题，之后进墨色条。
    /// 没有分隔符时整句都是标题。模块的描述里只有一行标题（例如泳道图的
    /// 「2026-028-LocalChat · 28 个节点」），不必为了墨色条多声明一个字段。
    /// </summary>
    public void SetText(string? text)
    {
        text ??= "";
        var cut = text.LastIndexOf(" · ", System.StringComparison.Ordinal);
        if (cut <= 0)
        {
            Title = text;
            Note = "";
            return;
        }

        Title = text[..cut];
        Note = text[(cut + 3)..];
    }
}
