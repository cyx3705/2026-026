using System.Text.Json;
using HistoryVulcan.Core.Storage;

namespace HistoryAurora.Shell.Table;

/// <summary>
/// 列序记忆（REQ-UI-062）。表格把「用户把列拖成了什么顺序」交给它，重开时再要回来。
///
/// **只记顺序，不记宽度。** 宽度是声明出来的权重按页面宽度算的结果（REQ-UI-039），
/// 记下来就等于把一个随窗口变化的量固化成常量；而列序是用户的一次明确动作，
/// 不记的话每次开页都要重拖一遍。
/// </summary>
public interface IColumnOrderStore
{
    /// <summary>取某张表记住的列键顺序；没记过时返回空。</summary>
    IReadOnlyList<string> Read(string key);

    /// <summary>记下某张表当前的列键顺序。</summary>
    void Write(string key, IReadOnlyList<string> columnKeys);
}

/// <summary>
/// 落在设置里的实现。**整本账是一个键**，而不是一张表一个设置项：
/// 表的身份由「模块/页面/节点」拼出来，数量随模块增长没有上界，
/// 一张表一个键会让设置文件被这类条目淹掉，且没有任何一处能把它们一起清掉。
/// </summary>
public sealed class ColumnOrderStore(ISettingsService settings) : IColumnOrderStore
{
    public const string SettingKey = "aurora.ui.columnorder";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private readonly ISettingsService _settings = settings;

    private Dictionary<string, List<string>>? _cache;

    public IReadOnlyList<string> Read(string key)
        => string.IsNullOrWhiteSpace(key)
            ? []
            : Load().TryGetValue(key, out var order) ? order : [];

    public void Write(string key, IReadOnlyList<string> columnKeys)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        var all = Load();
        if (columnKeys is not { Count: > 0 })
            all.Remove(key);
        else
            all[key] = columnKeys.ToList();

        // 读不回来的账等于没有账，因此写失败必须让调用方看得见。
        // 这里不吞异常：设置服务写不进去是环境问题，不是表格的问题。
        _settings.Set(SettingKey, JsonSerializer.Serialize(all, Options));
    }

    private Dictionary<string, List<string>> Load()
    {
        if (_cache != null)
            return _cache;

        var raw = _settings.Get(SettingKey);
        if (string.IsNullOrWhiteSpace(raw))
            return _cache = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        try
        {
            _cache = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(raw, Options)
                ?? new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // 账坏了就当没记过：列序是个便利，不值得为它挡住整张表。
            _cache = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }

        return _cache;
    }
}
