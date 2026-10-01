namespace HistoryAurora.Shell.Neutral.Storage;

/// <summary>
/// 界面自己的扁平设置读写口（1.29.0）。实现是 <see cref="JsonSettingsStore"/>。
/// </summary>
/// <remarks>
/// 此前借用宿主的 <c>HistoryVulcan.Core.Storage.ISettingsService</c>；宿主 6.0.0 起那是宿主内部类型，
/// 模块只能用契约程序集白名单里的类型，于是接口搬回本仓，形状不变。
/// </remarks>
public interface ISettingsService
{
    string? Get(string key);

    int GetInt(string key, int fallback);

    void Set(string key, string value);

    IReadOnlyList<KeyValuePair<string, string>> All();
}
