using HistoryVulcan.Core.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HistoryAurora.Shell.Logging;

/// <summary>
/// 界面进程内日志。不写宿主日志文件，避免与宿主 ShellLog 抢同一滚动文件。
/// 控制台窗口订阅本实例；指令回显由总线 Executed 转发进来。
/// </summary>
public sealed class MemoryShellLog : IShellLog
{
    private const int BufferLimit = 20_000;
    internal const int MaximumSnapshotBytes = 256 * 1024;
    private readonly object _gate = new();
    private readonly Queue<SequencedLogEntry> _buffer = new();
    private long _sequence;

    internal string InstanceId { get; } = Guid.NewGuid().ToString("N");

    public event EventHandler<ShellLogEntry>? EntryAdded;

    public void Log(ShellLogLevel level, string category, string message)
    {
        var entry = new ShellLogEntry(
            DateTime.Now,
            level,
            category,
            ConsoleLogSanitizer.Redact(message));
        lock (_gate)
        {
            _buffer.Enqueue(new SequencedLogEntry(++_sequence, entry));
            while (_buffer.Count > BufferLimit)
                _buffer.Dequeue();
        }

        EntryAdded?.Invoke(this, entry);
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
