using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryAurora.Shell.Neutral.Commands;

namespace HistoryAurora.Shell.Neutral.CommandSurface;

/// <summary>参数表的取用：补全与指令详情页共用同一条路径与同一份缓存。</summary>
internal sealed partial class LocalCommandCatalogSession
{
    /// <summary>参数表：从目录取并缓存（目录里已带参数，不必再逐条 show）。</summary>
    private Task<IReadOnlyList<CommandParameterInfo>> ParametersAsync(
        string commandName,
        CancellationToken cancellationToken)
    {
        if (commandName.Length == 0 || cancellationToken.IsCancellationRequested)
            return Task.FromResult<IReadOnlyList<CommandParameterInfo>>([]);
        if (_parameters.TryGetValue(commandName, out var cached))
            return Task.FromResult(cached);

        IReadOnlyList<CommandParameterInfo> parameters = _registry.TryGet(commandName, out var command)
            ? command.Parameters.Select(Convert).ToList()
            : [];
        _parameters[commandName] = parameters;
        return Task.FromResult(parameters);
    }

    /// <summary>取一条指令的完整详情，供指令详情页显示。</summary>
    public Task<CommandCatalogDetail?> DetailAsync(
        string commandName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(commandName) || !_registry.TryGet(commandName, out var command))
            return Task.FromResult<CommandCatalogDetail?>(null);
        return Task.FromResult<CommandCatalogDetail?>(Detail(command));
    }

    private CommandCatalogDetail Detail(CommandInfo command)
    {
        var entry = Find(command.Name);
        var module = command.Source.StartsWith("module:", StringComparison.OrdinalIgnoreCase);
        var row = new CommandCatalogRow(
            command.Name,
            entry?.Domain ?? command.Domain,
            command.Summary,
            command.Example,
            command.Parameters.Count,
            module ? "module" : command.Source,
            module ? command.Source["module:".Length..] : null,
            command.Level == CommandLevel.Ask,
            command.RequiresUiThread,
            command.Readonly,
            command.HiddenReason)
        {
            CommandClass = entry?.CommandClass ?? command.CommandClass,
            Method = command.Method,
            RequiresConfirmation = command.RequiresConfirmation,
            AllowUnspecifiedParameters = command.AllowUnspecifiedParameters,
        };

        return new CommandCatalogDetail(row, command.Parameters.Select(Convert).ToList())
        {
            Annotations = command.Annotations,
        };
    }

    private static CommandParameterInfo Convert(ParameterSpec parameter) => new(
        parameter.Name,
        parameter.Type.ToString().ToLowerInvariant(),
        parameter.Required,
        parameter.Default,
        parameter.Position,
        parameter.AllowedValues ?? [],
        parameter.Description);
}
