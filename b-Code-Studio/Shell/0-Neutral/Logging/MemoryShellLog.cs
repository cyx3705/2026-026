using HistoryVulcan.Core.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HistoryAurora.Shell.Neutral.Logging;

/// <summary>
/// 控制台日志视图：给日志编单调序号、脱敏并缓冲，供控制台窗口与 <c>aurora.log.snapshot</c> 读取。
/// </summary>
/// <remarks>
/// 1.23.0 起（DEC-042）进程内界面把宿主那唯一一份日志（<c>IModuleContext.Log</c>）交进来：
/// 写入转给宿主，显示来自宿主，界面不再自建第二份。经宿主总线执行的每条指令——来自界面、CLI、MCP
/// 还是模块嵌套调用——回显、进度与结果因此都进控制台，也都落宿主日志文件。
/// 此前界面只看得见自己这份，宿主上跑的指令只剩界面总线拿回的最终回执。
///
/// 不传宿主日志时（契约测试、组件画廊）退化为独立的内存日志。
/// 挂着宿主日志时必须 <see cref="Dispose"/>：否则热重载后宿主仍握着旧实例，旧加载上下文回收不掉。
/// </remarks>
public sealed class MemoryShellLog : IShellLog, IDisposable
{
    private const int BufferLimit = 20_000;

    /// <summary>挂上宿主日志时补读的旧记录上限：宿主缓冲可达五万条，全补进来控制台首屏会卡。</summary>
    private const int BacklogImportLimit = 2_000;

    /// <summary>补读与订阅交界处要去重的记录数：只有补读那一刻正在派发事件的少数几条会两边都到。</summary>
    private const int BacklogSeamLimit = 256;

    internal const int MaximumSnapshotBytes = 256 * 1024;
    private readonly object _gate = new();
    private readonly Queue<SequencedLogEntry> _buffer = new();
    private readonly IShellLog? _host;
    private readonly HashSet<ShellLogEntry> _backlogSeam = new(ReferenceEqualityComparer.Instance);
    private long _sequence;

    /// <summary>独立的内存日志，不连宿主。</summary>
    public MemoryShellLog()
    {
    }

    /// <summary>显示宿主那一份日志，写入也转给它。</summary>
    /// <param name="host">宿主日志，通常来自 <c>IModuleContext.Log</c>。</param>
    public MemoryShellLog(IShellLog host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        lock (_gate)
        {
            // 先订阅再补读，且整段持锁：补读期间到达的事件等在 OnHostEntry 的锁外，不会漏；
            // 补读快照里已有、事件又晚到的那几条按引用去重，不会重复。
            host.EntryAdded += OnHostEntry;
            var backlog = host.Snapshot();
            var start = Math.Max(0, backlog.Count - BacklogImportLimit);
            for (var index = start; index < backlog.Count; index++)
            {
                Enqueue(backlog[index]);
                if (index >= backlog.Count - BacklogSeamLimit)
                    _backlogSeam.Add(backlog[index]);
            }
        }
    }

    internal string InstanceId { get; } = Guid.NewGuid().ToString("N");

    public event EventHandler<ShellLogEntry>? EntryAdded;

    public void Log(ShellLogLevel level, string category, string message)
    {
        if (_host is not null)
        {
            // 写进宿主那一份；控制台显示经 OnHostEntry 回来，本地不另记，免得一条出现两次。
            _host.Log(level, category, ConsoleLogSanitizer.Redact(message));
            return;
        }

        ShellLogEntry entry;
        lock (_gate)
            entry = Enqueue(new ShellLogEntry(DateTime.Now, level, category, message));

        EntryAdded?.Invoke(this, entry);
    }

    /// <summary>解除对宿主日志的订阅；独立日志上是空操作。</summary>
    public void Dispose()
    {
        if (_host is not null)
            _host.EntryAdded -= OnHostEntry;
    }

    private void OnHostEntry(object? sender, ShellLogEntry entry)
    {
        ShellLogEntry shown;
        lock (_gate)
        {
            if (_backlogSeam.Remove(entry))
                return;
            shown = Enqueue(entry);
        }

        EntryAdded?.Invoke(this, shown);
    }

    /// <summary>脱敏后入缓冲并编号；调用方持有 <see cref="_gate"/>。</summary>
    private ShellLogEntry Enqueue(ShellLogEntry entry)
    {
        var shown = entry with { Message = ConsoleLogSanitizer.Redact(entry.Message) };
        _buffer.Enqueue(new SequencedLogEntry(++_sequence, shown));
        while (_buffer.Count > BufferLimit)
            _buffer.Dequeue();
        return shown;
    }

    public IReadOnlyList<ShellLogEntry> Snapshot()
    {
        lock (_gate)
            return _buffer.Select(item => item.Entry).ToList();
    }

    internal ConsoleLogSnapshot ReadSnapshot(ConsoleLogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        lock (_gate)
        {
            var capturedAt = DateTimeOffset.Now;
            var oldestSequence = _buffer.Count == 0 ? 0 : _buffer.Peek().Sequence;
            var newestSequence = _buffer.Count == 0 ? 0 : _buffer.Last().Sequence;
            var matching = _buffer.Where(item => Matches(item, query)).ToList();
            var ordered = query.After.HasValue
                ? matching.OrderBy(item => item.Sequence)
                : matching.OrderByDescending(item => item.Sequence);
            var selected = ordered.Take(query.Limit)
                .Select(item => new ConsoleLogSnapshotEntry(
                    item.Sequence,
                    new DateTimeOffset(item.Entry.Time),
                    item.Entry.Level.ToString(),
                    item.Entry.Category,
                    item.Entry.Message))
                .ToList();

            var truncated = matching.Count > selected.Count;
            var snapshot = CreateSnapshot(
                capturedAt, oldestSequence, newestSequence, matching.Count, selected, truncated, query.After);
            while (selected.Count > 0
                   && JsonSerializer.SerializeToUtf8Bytes(snapshot).Length > MaximumSnapshotBytes)
            {
                selected.RemoveAt(selected.Count - 1);
                truncated = true;
                snapshot = CreateSnapshot(
                    capturedAt, oldestSequence, newestSequence, matching.Count, selected, truncated, query.After);
            }

            return snapshot;
        }
    }

    private ConsoleLogSnapshot CreateSnapshot(
        DateTimeOffset capturedAt,
        long oldestSequence,
        long newestSequence,
        int matchedCount,
        IReadOnlyList<ConsoleLogSnapshotEntry> entries,
        bool truncated,
        long? after)
    {
        var nextAfter = entries.Count > 0
            ? entries.Max(item => item.Sequence)
            : after ?? newestSequence;
        return new ConsoleLogSnapshot(
            1,
            InstanceId,
            capturedAt,
            oldestSequence,
            newestSequence,
            matchedCount,
            entries.Count,
            truncated,
            nextAfter,
            entries);
    }

    private static bool Matches(SequencedLogEntry item, ConsoleLogQuery query)
    {
        if (item.Entry.Level < query.MinimumLevel)
            return false;
        if (query.After is { } after && item.Sequence <= after)
            return false;
        if (query.Since is { } since && new DateTimeOffset(item.Entry.Time) < since)
            return false;
        if (!string.IsNullOrEmpty(query.Source)
            && !item.Entry.Category.Equals(query.Source, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.IsNullOrEmpty(query.Keyword)
               || item.Entry.Category.Contains(query.Keyword, StringComparison.OrdinalIgnoreCase)
               || item.Entry.Message.Contains(query.Keyword, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SequencedLogEntry(long Sequence, ShellLogEntry Entry);
}

internal static partial class ConsoleLogSanitizer
{
    private const string Mask = "[REDACTED]";

    internal static string Redact(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var redacted = AuthorizationBearer().Replace(message, "${prefix}" + Mask);
        redacted = QuotedSensitiveField().Replace(
            redacted,
            match => match.Groups["prefix"].Value
                     + match.Groups["quoted"].Value[0]
                     + Mask
                     + match.Groups["quoted"].Value[0]);
        redacted = UnquotedSensitiveField().Replace(redacted, "${prefix}" + Mask);
        redacted = CredentialUri().Replace(redacted, "${scheme}" + Mask + ":" + Mask + "@");
        redacted = DelimitedCredential().Replace(
            redacted,
            "${boundary}" + Mask + "${delimiter}" + Mask + "${suffix}");
        redacted = KnownOpaqueSecret().Replace(redacted, Mask);
        redacted = PrivateKeyBlock().Replace(redacted, Mask);
        return redacted;
    }

    private const string SensitiveFieldNames =
        "account|accounts|username|user_name|login|password|passwd|pwd|token|access_token|refresh_token|"
        + "api_key|apikey|client_secret|secret|private_key|connection_string|authorization|cookie|session|"
        + "账号|帐号|账户|用户名|登录名|密码|口令|令牌|密钥";

    [GeneratedRegex(
        "(?<prefix>[\\\"']?(?:" + SensitiveFieldNames + ")[\\\"']?\\s*(?:=|:)\\s*)(?<quoted>\\\"[^\\\"\\r\\n]*\\\"|'[^'\\r\\n]*')",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        1000)]
    private static partial Regex QuotedSensitiveField();

    [GeneratedRegex(
        "(?<prefix>[\\\"']?(?:" + SensitiveFieldNames + ")[\\\"']?\\s*(?:=|:)\\s*)(?<value>[^\\s,;}\\]\\r\\n\\\"'][^\\s,;}\\]\\r\\n]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        1000)]
    private static partial Regex UnquotedSensitiveField();

    [GeneratedRegex(
        "(?<prefix>\\b(?:authorization\\s*(?:=|:)\\s*)?bearer\\s+)[A-Za-z0-9._~+/=-]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        1000)]
    private static partial Regex AuthorizationBearer();

    [GeneratedRegex(
        "(?<scheme>\\b[a-z][a-z0-9+.-]*://)[^/@\\s:]+:[^/@\\s]+@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        1000)]
    private static partial Regex CredentialUri();

    [GeneratedRegex(
        "(?<boundary>^|\\s)(?:[A-Z0-9._%+-]+@(?:[A-Z0-9](?:[A-Z0-9-]{0,61}[A-Z0-9])?\\.)+[A-Z]{2,}|[A-Z0-9._]{3,}(?:-[A-Z0-9._]+)*)(?<delimiter>-{4,}|\\|{3,})(?:[^\\s|;]{4,})(?<suffix>$|\\s)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        1000)]
    private static partial Regex DelimitedCredential();

    [GeneratedRegex(
        "\\b(?:sk-[A-Za-z0-9_-]{16,}|github_pat_[A-Za-z0-9_]{16,}|gh[oprsu]_[A-Za-z0-9]{16,}|AKIA[A-Z0-9]{16})\\b",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        1000)]
    private static partial Regex KnownOpaqueSecret();

    [GeneratedRegex(
        "-----BEGIN(?: [A-Z0-9]+)* PRIVATE KEY-----[\\s\\S]*?-----END(?: [A-Z0-9]+)* PRIVATE KEY-----",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        1000)]
    private static partial Regex PrivateKeyBlock();
}

internal sealed record ConsoleLogQuery(
    ShellLogLevel MinimumLevel,
    string? Source,
    string? Keyword,
    DateTimeOffset? Since,
    long? After,
    int Limit);

internal sealed record ConsoleLogSnapshot(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("frontendInstanceId")] string FrontendInstanceId,
    [property: JsonPropertyName("capturedAt")] DateTimeOffset CapturedAt,
    [property: JsonPropertyName("oldestSequence")] long OldestSequence,
    [property: JsonPropertyName("newestSequence")] long NewestSequence,
    [property: JsonPropertyName("matchedCount")] int MatchedCount,
    [property: JsonPropertyName("returnedCount")] int ReturnedCount,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("nextAfter")] long NextAfter,
    [property: JsonPropertyName("entries")] IReadOnlyList<ConsoleLogSnapshotEntry> Entries);

internal sealed record ConsoleLogSnapshotEntry(
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("message")] string Message);
