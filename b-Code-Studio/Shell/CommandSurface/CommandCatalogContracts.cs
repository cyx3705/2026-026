namespace HistoryAurora.Shell.CommandSurface;

public sealed record CommandCatalogFilter(
    string Query = "",
    string Domain = "全部",
    string CommandClass = "全部",
    int McpFilter = 0);

public enum CommandCatalogChangeKind
{
    Snapshot,
    Filter,
    Selection,
    Invalidated,
}

public sealed class CommandCatalogChangedEventArgs(CommandCatalogChangeKind kind) : EventArgs
{
    public CommandCatalogChangeKind Kind { get; } = kind;
}

public interface ICommandCatalogSession : IDisposable
{
    event EventHandler<CommandCatalogChangedEventArgs>? Changed;

    IReadOnlyList<string> Domains { get; }

    IReadOnlyList<string> Classes { get; }

    string? SelectedCommandName { get; }

    CommandCatalogFilter CurrentFilter { get; }

    Task<bool> RefreshAsync(bool force = false, CancellationToken cancellationToken = default);

    void SetFilter(CommandCatalogFilter filter);

    bool TrySetDomain(string domain, out IReadOnlyList<string> availableDomains);

    bool TrySetCommandClass(string commandClass, out IReadOnlyList<string> availableClasses);

    void SetConsoleQuery(string query);

    bool MoveSelection(int direction);

    void Select(string? commandName);

    Task<ConsoleCompletionResult> CompleteAsync(
        string text,
        int caretIndex,
        CancellationToken cancellationToken = default);
}

public enum ConsoleCompletionKind
{
    Command,
    Parameter,
    Value,
    Domain,
    Class,
    Method,
}

public sealed class ConsoleCompletionCandidate
{
    public required string InsertText { get; init; }

    public required string DisplayText { get; init; }

    public string Description { get; init; } = "";

    public required ConsoleCompletionKind Kind { get; init; }
}

public sealed class ConsoleCompletionResult
{
    public static ConsoleCompletionResult Empty { get; } = new()
    {
        Candidates = [],
        ReplaceStart = 0,
        ReplaceLength = 0,
    };

    public required IReadOnlyList<ConsoleCompletionCandidate> Candidates { get; init; }

    public required int ReplaceStart { get; init; }

    public required int ReplaceLength { get; init; }

    public bool HasCandidates => Candidates.Count > 0;
}
