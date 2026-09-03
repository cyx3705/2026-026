using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace HistoryAurora.Shell.Neutral;

internal static class CommandResultData
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static bool TryGetJsonText(object? data, string? message, [NotNullWhen(true)] out string? text)
    {
        if (TryGetJsonContainer(data, out text))
            return true;

        if (TryGetJsonContainer(message, out text))
            return true;

        text = null;
        return false;
    }

    private static bool TryGetJsonContainer(object? value, [NotNullWhen(true)] out string? text)
    {
        switch (value)
        {
            case null:
                text = null;
                return false;
            case string stringValue:
                return TryValidateJsonContainer(stringValue, out text);
            case JsonElement element:
                return TryValidateJsonContainer(element.GetRawText(), out text);
            case JsonDocument document:
                return TryValidateJsonContainer(document.RootElement.GetRawText(), out text);
            default:
                try
                {
                    return TryValidateJsonContainer(JsonSerializer.Serialize(value, JsonOptions), out text);
                }
                catch (NotSupportedException)
                {
                    text = null;
                    return false;
                }
        }
    }

    private static bool TryValidateJsonContainer(string value, [NotNullWhen(true)] out string? text)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            {
                text = value;
                return true;
            }
        }
        catch (JsonException)
        {
        }

        text = null;
        return false;
    }

    public static bool TryRead<T>(object? data, [NotNullWhen(true)] out T? value)
    {
        if (data is T typed)
        {
            value = typed;
            return true;
        }

        if (data is JsonElement element)
        {
            try
            {
                value = element.Deserialize<T>(JsonOptions);
                return value is not null;
            }
            catch (JsonException)
            {
            }
            catch (NotSupportedException)
            {
            }
        }

        value = default;
        return false;
    }
}
