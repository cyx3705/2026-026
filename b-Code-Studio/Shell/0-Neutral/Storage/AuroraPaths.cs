using System.IO;

namespace HistoryAurora.Shell.Neutral.Storage;

/// <summary>
/// Aurora 的数据根：宿主给的数据目录（<c>ModuleData\HistoryAurora\</c>），布局、面板与界面设置都在其下。
/// </summary>
/// <remarks>
/// 1.29.0 起数据根由宿主给（宿主 6.0.0 统一契约 <c>IModuleEnvironment.DataDirectory</c>）。
/// 此前 Aurora 直接用宿主数据根 <c>%AppData%\HistoryVulcan\</c>，与宿主自己的文件混在一处；
/// 旧数据已在宿主 6.0.0 切换时一次性搬过来，模块不再认旧布局。
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

    /// <summary>以宿主给的数据目录为根，并确保布局与面板目录存在。</summary>
    public static AuroraPaths ForDataDirectory(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        return new AuroraPaths(dataDirectory);
    }

    /// <summary>给定数据根下的面板目录。</summary>
    public static string PanelsDirOf(string root) => Path.Combine(root, "panels");
}
