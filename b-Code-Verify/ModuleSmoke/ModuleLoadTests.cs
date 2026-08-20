using System.Text.Json;
using HistoryAurora.Module;
using Xunit;

namespace HistoryAurora.Verify.ModuleSmoke;

/// <summary>VERIFY-MODULE-LOAD：模块能被装载并取得宿主上下文。</summary>
public sealed class ModuleLoadTests
{
    [Fact]
    public void CompositionImplementsHostContextContract()
    {
        var composition = new AuroraBusinessComposition();

        Assert.IsAssignableFrom<HistoryVulcan.Core.Modules.IModuleContextAware>(composition);
    }

    /// <summary>
    /// VERIFY-HOST-CONTRACT：清单是宿主识别模块身份的唯一权威，字段错了模块根本不会被发现。
    /// </summary>
    [Fact]
    public void ManifestDeclaresHostModuleIdentity()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "module.manifest.json");
        Assert.True(File.Exists(manifestPath), $"module.manifest.json 未随产物输出: {manifestPath}");

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;

        Assert.Equal("HistoryVulcan.Module", root.GetProperty("type").GetString());
        Assert.Equal("HistoryAurora", root.GetProperty("name").GetString());
        // DEC-008：独立 exe 退役后不再有同名程序集，产物名取回 HistoryAurora——
        // Shell 的 pack URI 按程序集短名解析，名字对上才不用逐条改。
        Assert.Equal("HistoryAurora.dll", root.GetProperty("artifact").GetString());
        Assert.True(root.GetProperty("ui").GetBoolean());

        // 界面在宿主进程内开窗，WPF 的进程级状态拆不掉：必须声明不可热重载，
        // 否则宿主重载时卸载装载上下文，第二次初始化必崩。
        Assert.True(root.GetProperty("pinned").GetBoolean());

        // AvalonDock 要随包走：模块的装载上下文只在包目录内解析依赖。
        var deps = root.GetProperty("deps").EnumerateArray().Select(item => item.GetString()).ToList();
        Assert.Contains("AvalonDock.dll", deps);
    }
}
