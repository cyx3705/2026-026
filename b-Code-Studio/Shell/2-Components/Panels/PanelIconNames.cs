namespace HistoryAurora.Shell.Components.Panels;

/// <summary>图标名校验不依赖绘图 API，桌面和 Web 共用同一目录。</summary>
public static class PanelIconNames
{
    public static bool Supports(string? name)
        => string.Equals(name, "refresh-cw", StringComparison.OrdinalIgnoreCase);
}
