using System.IO;
using System.Text.Json;
using HistoryAurora.Web;
using Xunit;

namespace Contracts;

public sealed class PanelFileReaderContractTests
{
    [Fact]
    public void NativeModelsParseCommentsCaseAndTrailingCommasAndIsolateBrokenFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aurora-panel-files-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "a-valid.json"), """
                { // Native panel JSON accepts comments and trailing commas.
                  "ID":"Quick", "Title":"快捷", "Side":"LEFT", "Visible":false,
                  "Rows":[{"Widgets":[{"Kind":"textbox","Id":"draft","Value":"原值"},]},],
                }
                """);
            File.WriteAllText(Path.Combine(directory, "b-duplicate.json"), """{"id":"quick","title":"重复","rows":[]} """);
            File.WriteAllText(Path.Combine(directory, "c-broken.json"), "{broken");
            File.WriteAllText(Path.Combine(directory, "d-invalid.json"), """{"id":"invalid","title":"不合规","rows":[{"widgets":[{"kind":"button"}]}]}""");
            var original = File.ReadAllText(Path.Combine(directory, "a-valid.json"));
            var loaded = PanelFileReader.Read(directory);
            var panel = Assert.Single(loaded.Panels);
            Assert.Equal("Quick", panel.Id);
            Assert.False(panel.Visible);
            Assert.Equal(.22, panel.Ratio);
            Assert.Equal("原值", Assert.Single(Assert.Single(panel.Rows).Widgets).Value);
            Assert.Equal(3, loaded.Errors.Count);
            Assert.Contains(loaded.Errors, error => error.Contains("重复"));
            Assert.Contains(loaded.Errors, error => error.Contains("b-duplicate.json"));
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(loaded, PanelFileReader.WriteOptions));
            var value = json.RootElement.GetProperty("panels")[0];
            Assert.Equal("Quick", value.GetProperty("id").GetString());
            Assert.False(value.TryGetProperty("allWidgets", out _));
            Assert.Equal(original, File.ReadAllText(Path.Combine(directory, "a-valid.json")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void NullRowsWidgetsAndItemsAreDiagnosticsAndMissingDirectoryIsNeverCreated()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aurora-panel-null-" + Guid.NewGuid());
        Assert.Empty(PanelFileReader.Read(directory).Panels);
        Assert.False(Directory.Exists(directory));
        Directory.CreateDirectory(directory);
        try
        {
            var rows = new[] { "null", "[null]", "[{\"widgets\":null}]", "[{\"widgets\":[null]}]" };
            for (var i = 0; i < rows.Length; i++)
                File.WriteAllText(Path.Combine(directory, i + ".json"), "{\"id\":\"bad\",\"title\":\"空结构\",\"rows\":" + rows[i] + "}");
            var loaded = PanelFileReader.Read(directory);
            Assert.Empty(loaded.Panels);
            Assert.Equal(4, loaded.Errors.Count);
        }
        finally { Directory.Delete(directory, true); }
    }
}
