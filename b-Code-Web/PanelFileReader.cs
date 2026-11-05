using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using HistoryAurora.Shell.Components.Panels;

namespace HistoryAurora.Web;

internal sealed record PanelFileSnapshot(IReadOnlyList<PanelDefinition> Panels, IReadOnlyList<string> Errors);

// Uses the native model and validator; reading never creates or rewrites user files.
internal static class PanelFileReader
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };
    public static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        IgnoreReadOnlyProperties = true,
    };

    public static PanelFileSnapshot Read(string directory)
    {
        var panels = new List<PanelDefinition>();
        var errors = new List<string>();
        if (!Directory.Exists(directory)) return new(panels, errors);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(file);
            try
            {
                var parsed = PanelDefinitionValidator.Validate(JsonSerializer.Deserialize<PanelDefinition>(File.ReadAllText(file), ReadOptions));
                if (!parsed.Ok) { errors.Add($"{name}：{parsed.Error}"); continue; }
                if (!ids.Add(parsed.Value!.Id)) { errors.Add($"{name}：面板 id 重复 {parsed.Value.Id}"); continue; }
                panels.Add(parsed.Value);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                errors.Add($"{name}：无法读取面板声明（{ex.GetType().Name}）");
            }
        }
        return new(panels, errors);
    }
}
