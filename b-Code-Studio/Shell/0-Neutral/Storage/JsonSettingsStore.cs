using System.Globalization;
using System.IO;
using System.Text.Json;
using HistoryVulcan.Core.Storage;

namespace HistoryAurora.Shell.Neutral.Storage;

/// <summary>
/// 扁平键值的 JSON 设置文件；写入即原子落盘。
/// </summary>
/// <remarks>
/// 宿主 5.4 起不再公开 SettingsService，Aurora 自持设置存储。文件格式与迁出前一致，
/// 读不出来的文件改名为 <c>.corrupt-时间戳</c> 保留，不覆盖用户数据。
/// </remarks>
internal sealed class JsonSettingsStore : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _filePath;
    private Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>从指定文件加载设置；文件不存在时为空配置。</summary>
    public JsonSettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
        try
        {
            if (File.Exists(_filePath))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(_filePath), JsonOptions);
                _values = new Dictionary<string, string>(
                    loaded ?? new Dictionary<string, string>(),
                    StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            PreserveUnreadableSettings(ex);
        }
    }

    public string? Get(string key)
    {
        lock (_gate)
            return _values.TryGetValue(key, out var value) ? value : null;
    }

    public int GetInt(string key, int fallback)
        => int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    public void Set(string key, string value)
    {
        lock (_gate)
        {
            var existed = _values.TryGetValue(key, out var previous);
            _values[key] = value;
            try
            {
                Persist();
            }
            catch
            {
                if (existed)
                    _values[key] = previous!;
                else
                    _values.Remove(key);
                throw;
            }
        }
    }

    public IReadOnlyList<KeyValuePair<string, string>> All()
    {
        lock (_gate)
            return _values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporary = _filePath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(_values, JsonOptions));
            File.Move(temporary, _filePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void PreserveUnreadableSettings(Exception error)
    {
        var preserved = "";
        if (error is JsonException && File.Exists(_filePath))
        {
            preserved = _filePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            try
            {
                File.Move(_filePath, preserved, overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                preserved = $"(备份失败: {ex.Message})";
            }
        }

        System.Diagnostics.Trace.TraceWarning(
            $"Aurora 设置文件无法读取，已使用空配置: {_filePath}; {error.Message}"
            + (preserved.Length == 0 ? "" : $"; 原文件: {preserved}"));
    }
}
