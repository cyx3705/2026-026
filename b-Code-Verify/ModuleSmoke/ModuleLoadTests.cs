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
        Assert.Equal("HistoryAurora.dll", root.GetProperty("artifact").GetString());
        Assert.True(root.GetProperty("ui").GetBoolean());
    }
}
