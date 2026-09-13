using System.IO;

namespace HistoryAurora.Shell.Neutral.Commands;

/// <summary>
/// 手输指令历史(C-11):↑/↓ 翻阅 + 持久化(默认 500 条,可配)。
/// 线程安全;文件一行一条,追加式加载、整体重写保存。
/// </summary>
/// <remarks>
/// 宿主 5.4 起不再携带界面状态类型，本类随控制台迁回 Aurora。
/// 文件头保持 <c>HistoryVulcan.CommandHistory.v2</c> 不变：用户已有的 history.txt 原样可读。
/// </remarks>
public sealed class CommandHistory
{
    private const string RedactedFormatHeader = "# HistoryVulcan.CommandHistory.v2:redacted";

    private readonly object _gate = new();
    private readonly List<string> _items = new();
    private readonly string _filePath;
    private readonly int _capacity;

    /// <summary>从历史文件加载；旧的未脱敏格式会被清空而不是载入。</summary>
    /// <param name="filePath">历史文件路径。</param>
    /// <param name="capacity">保留条数上限，最小 10。</param>
    public CommandHistory(string filePath, int capacity = 500)
    {
        _filePath = filePath;
        _capacity = Math.Max(10, capacity);
        try
        {
            if (File.Exists(filePath))
            {
                var lines = File.ReadAllLines(filePath);
                if (lines.FirstOrDefault() == RedactedFormatHeader)
                {
                    _items.AddRange(
                        lines.Skip(1)
                            .Where(l => !string.IsNullOrWhiteSpace(l))
                            .TakeLast(_capacity));
                }
                else
                {
                    // Earlier formats stored raw input. Do not load or preserve entries that may
                    // contain credentials; new entries arrive from the already-redacted echo log.
                    File.WriteAllLines(filePath, [RedactedFormatHeader]);
                }
            }
        }
        catch (IOException)
        {
            // 历史读不出来不影响使用
        }
    }

    /// <summary>
    /// 追加一条已经过 CommandBus 回显脱敏的文本(与上一条重复时不重复入表)。
    /// 原始用户输入不得直接传入；Shell 使用 <c>cmd:手动</c> 回显作为唯一写入源。
    /// </summary>
    public void Add(string command)
    {
        var text = command.Trim();
        if (text.Length == 0)
            return;

        lock (_gate)
        {
            if (_items.Count > 0 && _items[^1] == text)
                return;
            _items.Add(text);
            while (_items.Count > _capacity)
                _items.RemoveAt(0);
        }
    }

    /// <summary>最新在末尾的快照。</summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return _items.ToList();
        }
    }

    /// <summary>持久化到文件(退出时调用)。</summary>
    public void Save()
    {
        try
        {
            lock (_gate)
            {
                File.WriteAllLines(_filePath, new[] { RedactedFormatHeader }.Concat(_items));
            }
        }
        catch (IOException)
        {
        }
    }
}
