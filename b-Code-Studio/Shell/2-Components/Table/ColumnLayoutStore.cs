using System.Text.Json;
using HistoryAurora.Shell.Neutral.Storage;

namespace HistoryAurora.Shell.Components.Table;

/// <summary>
/// 列布局记忆：表格把「用户把列拖成了什么顺序」（REQ-UI-062）和「用户把列拖成了多宽」
/// （REQ-UI-142，1.32.0）交给它，重开时再要回来。
///
/// **宽度记的是权重，不是像素。** 表格永远按权重铺满可用宽度（REQ-UI-039），
/// 用户拖完那一刻各列的宽度就是新的权重；下次建表时拿它替掉声明的权重，
/// 窗口变宽变窄照样按用户拖出来的**比例**分摊，不会把一个随窗口变化的量固化成常量。
/// </summary>
public interface IColumnLayoutStore
{
    /// <summary>取某张表记住的列键顺序；没记过时返回空。</summary>
    IReadOnlyList<string> ReadOrder(string key);

    /// <summary>记下某张表当前的列键顺序。</summary>
    void WriteOrder(string key, IReadOnlyList<string> columnKeys);

    /// <summary>取某张表记住的「列键 → 权重」；没记过时返回空。</summary>
    IReadOnlyDictionary<string, double> ReadWidths(string key);

    /// <summary>记下某张表当前各列的权重。</summary>
    void WriteWidths(string key, IReadOnlyDictionary<string, double> weights);
}

/// <summary>
/// 落在设置里的实现。**每一类是一整本账、一个键**，而不是一张表一个设置项：
/// 表的身份由「模块/页面/节点」拼出来，数量随模块增长没有上界，
/// 一张表一个键会让设置文件被这类条目淹掉，且没有任何一处能把它们一起清掉。
/// 列序沿用 1.x 的键，老用户拖好的顺序升级后还在。
/// </summary>
public sealed class ColumnLayoutStore(ISettingsService settings) : IColumnLayoutStore
{
    public const string OrderSettingKey = "aurora.ui.columnorder";

    public const string WidthSettingKey = "aurora.ui.columnwidths";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private readonly ISettingsService _settings = settings;

    private Dictionary<string, List<string>>? _orders;

    private Dictionary<string, Dictionary<string, double>>? _widths;

    public IReadOnlyList<string> ReadOrder(string key)
        => string.IsNullOrWhiteSpace(key)
            ? []
            : (_orders ??= Load<List<string>>(OrderSettingKey)).TryGetValue(key, out var order) ? order : [];

    public void WriteOrder(string key, IReadOnlyList<string> columnKeys)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        var all = _orders ??= Load<List<string>>(OrderSettingKey);
        if (columnKeys is not { Count: > 0 })
            all.Remove(key);
        else
            all[key] = columnKeys.ToList();
        Save(OrderSettingKey, all);
    }

    public IReadOnlyDictionary<string, double> ReadWidths(string key)
        => string.IsNullOrWhiteSpace(key)
            ? new Dictionary<string, double>()
            : (_widths ??= Load<Dictionary<string, double>>(WidthSettingKey)).TryGetValue(key, out var weights)
                ? weights
                : new Dictionary<string, double>();

    public void WriteWidths(string key, IReadOnlyDictionary<string, double> weights)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        var all = _widths ??= Load<Dictionary<string, double>>(WidthSettingKey);
        var valid = weights
            .Where(pair => double.IsFinite(pair.Value) && pair.Value > 0)
            .ToDictionary(pair => pair.Key, pair => Math.Round(pair.Value, 1), StringComparer.Ordinal);
        if (valid.Count == 0)
            all.Remove(key);
        else
            all[key] = valid;
        Save(WidthSettingKey, all);
    }

    // 读不回来的账等于没有账，因此写失败必须让调用方看得见。
    // 这里不吞异常：设置服务写不进去是环境问题，不是表格的问题。
    private void Save<T>(string settingKey, Dictionary<string, T> all)
        => _settings.Set(settingKey, JsonSerializer.Serialize(all, Options));

    private Dictionary<string, T> Load<T>(string settingKey)
    {
        var raw = _settings.Get(settingKey);
        if (string.IsNullOrWhiteSpace(raw))
            return new Dictionary<string, T>(StringComparer.Ordinal);

        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, T>>(raw, Options);
            return loaded == null
                ? new Dictionary<string, T>(StringComparer.Ordinal)
                : new Dictionary<string, T>(loaded, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // 账坏了就当没记过：列布局是个便利，不值得为它挡住整张表。
            return new Dictionary<string, T>(StringComparer.Ordinal);
        }
    }
}
