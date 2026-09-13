using System.IO;

namespace HistoryAurora.Shell.Neutral.Storage;

/// <summary>
/// Aurora 的数据根：<c>%AppData%\&lt;应用名&gt;\</c>，布局、面板与界面设置都在其下。
/// </summary>
/// <remarks>
/// 宿主 5.4 起不再公开 AppPaths。目录形状与迁出前逐字一致，用户已有的
/// <c>layout\</c>、<c>panels\</c> 与 <c>settings.json</c> 原位可读，不做迁移。
/// </remarks>
internal sealed class AuroraPaths
{
    private AuroraPaths(string root)
    {
        Root = Path.GetFullPath(root);
        LayoutDir = Path.Combine(Root, "layout");
        PanelsDir = PanelsDirOf(Root);
        SettingsFile = Path.Combine(Root, "settings.json");
        Directory.CreateDirectory(LayoutDir);
        Directory.CreateDirectory(PanelsDir);
    }

    /// <summary>数据根目录。</summary>
    public string Root { get; }

    /// <summary>停靠布局目录。</summary>
    public string LayoutDir { get; }

    /// <summary>JSON 控制面板目录。</summary>
    public string PanelsDir { get; }

    /// <summary>界面设置文件（扁平键值 JSON）。</summary>
    public string SettingsFile { get; }

    /// <summary>按应用名定位 <c>%AppData%</c> 下的数据根，并确保布局与面板目录存在。</summary>
    public static AuroraPaths ForApplication(string appName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        return new AuroraPaths(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            appName));
    }

    /// <summary>给定数据根下的面板目录。</summary>
    public static string PanelsDirOf(string root) => Path.Combine(root, "panels");
}
