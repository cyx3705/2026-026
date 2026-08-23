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

        // 可热重载：宿主先拆界面再装新包，XAML 经默认上下文返回可回收 ALC 里的程序集。
        // pinned 缺省即 false；写明是为了防止再被钉回 Default 而锁死 DLL。
        Assert.False(root.GetProperty("pinned").GetBoolean());

        // AvalonDock 要随包走：模块的装载上下文只在包目录内解析依赖。
        var deps = root.GetProperty("deps").EnumerateArray().Select(item => item.GetString()).ToList();
        Assert.Contains("AvalonDock.dll", deps);
    }

    [Fact]
    public void DisposeWithoutAttachDoesNotThrow()
    {
        new AuroraBusinessComposition().Dispose();
    }
}
