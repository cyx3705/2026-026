using System.Text.Json;
using HistoryAurora.Shell.Pages;
using Xunit;

namespace HistoryAurora.Verify;

public sealed class ComponentGalleryContractTests
{
    [Fact]
    public void DescriptionCoversEveryPageComponentAndKeepsTheProtocolVersion()
    {
        var parsed = PageDescriptionReader.Read(
            HistoryAurora.Shell.Views.ComponentGalleryDescription.Json,
            "HistoryPreview");

        Assert.True(parsed.Ok, parsed.Error);
        Assert.Equal(1, parsed.Value!.SchemaVersion);

        using var document = JsonDocument.Parse(HistoryAurora.Shell.Views.ComponentGalleryDescription.Json);
        var types = document.RootElement
            .GetProperty("pages")[0]
            .GetProperty("content")
            .ToString();

        foreach (var type in PageRenderer.SupportedComponents)
        {
            // 小型交互控件只能出现在控制面板中；页面节点只保留复合容器和展示组件。
            if (type is "button" or "input" or "select")
                continue;
            Assert.Contains("\"type\":\"" + type + "\"", types.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("\"orientation\":\"horizontal\"", types.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"kind\":\"button\"", types.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"kind\":\"textbox\"", types.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"mode\":\"select\"", types.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
    }
}
