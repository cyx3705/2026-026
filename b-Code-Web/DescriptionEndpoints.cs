using System.Text.Json;
using System.Text.Json.Serialization;
using HistoryAurora.Shell.Components.Pages;
using HistoryAurora.Shell.Components.Actions;

namespace HistoryAurora.Web;

internal sealed record DescriptionResponse<T>(bool Success, T Description);

// Normalize a declaration as data only; this endpoint never dispatches commands.
internal static class DescriptionEndpoints
{
    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        IgnoreReadOnlyProperties = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Task<IResult> ReadPages(HttpContext context) => Read<PageDescriptionSet>(context, (json, owner) =>
    {
        var parsed = PageDescriptionReader.Read(json, owner);
        return (parsed.Value, parsed.Error);
    });

    public static Task<IResult> ReadActions(HttpContext context) => Read<ActionDeclarationSet>(context, (json, owner) =>
    {
        var parsed = ActionDeclarationReader.Read(json, owner);
        return (parsed.Value, parsed.Error);
    });

    private static async Task<IResult> Read<T>(HttpContext context,
        Func<string?, string, (T? Value, string? Error)> read) where T : class
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var input = document.RootElement;
            if (input.ValueKind != JsonValueKind.Object
                || !input.TryGetProperty("owner", out var owner) || owner.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(owner.GetString())
                || !input.TryGetProperty("description", out var description))
                return Results.BadRequest(new { success = false, message = "声明或被询问模块缺失" });
            var parsed = read(
                description.ValueKind == JsonValueKind.String ? description.GetString() : description.GetRawText(), owner.GetString()!);
            if (parsed.Value == null) return Results.BadRequest(new { success = false, message = parsed.Error });
            // Serialize before sending headers so non-finite numeric strings accepted
            // by the native reader become an explicit registration failure, not HTTP 500.
            var json = JsonSerializer.Serialize(new DescriptionResponse<T>(true, parsed.Value), WriteOptions);
            return Results.Content(json, "application/json");
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { success = false, message = "声明解析请求不是有效 JSON" });
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { success = false, message = "声明包含非有限数值，无法转换为 JSON" });
        }
    }
}
