using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Services.Commands;

namespace HistoryAurora.Shell.Neutral.CommandSurface;

/// <summary>参数表的取用：补全与指令详情页共用同一条路径与同一份缓存。</summary>
internal sealed partial class LocalCommandCatalogSession
{
    /// <summary>参数表：本地注册表优先，缺则问宿主一次并缓存。</summary>
    private async Task<IReadOnlyList<CommandParameterInfo>> ParametersAsync(
        string commandName,
        CancellationToken cancellationToken)
    {
        if (commandName.Length == 0)
            return [];
        if (_parameters.TryGetValue(commandName, out var cached))
            return cached;

        if (_registry.TryGet(commandName, out var descriptor))
        {
            var local = descriptor.Parameters.Select(Convert).ToList();
            _parameters[commandName] = local;
            return local;
        }

        if (_bus.RemoteExecutor == null)
            return [];

        try
        {
            var shown = await _bus
                .ExecuteAsync(
                    "vulcan.command.show name=" + CommandParser.QuoteArg(commandName),
                    "UI",
                    cancellationToken)
                .ConfigureAwait(true);
            if (shown.Success
                && CommandResultData.TryRead<CommandCatalogDetail>(shown.Data, out var detail))
            {
                _parameters[commandName] = detail.Parameters;
                return detail.Parameters;
            }
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception)
        {
            // 补全取不到参数表时必须表现为"没有候选"，而不是一条打断输入的报错。
        }

        _parameters[commandName] = [];
        return [];
    }

    /// <summary>取一条指令的完整详情，供指令详情页显示。</summary>
    public async Task<CommandCatalogDetail?> DetailAsync(
        string commandName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(commandName))
            return null;

        if (_registry.TryGet(commandName, out var descriptor))
            return LocalDetail(descriptor);

        if (_bus.RemoteExecutor == null)
            return null;

        try
        {
            var shown = await _bus
                .ExecuteAsync(
                    "vulcan.command.show name=" + CommandParser.QuoteArg(commandName),
                    "UI",
                    cancellationToken)
                .ConfigureAwait(true);
            if (shown.Success
                && CommandResultData.TryRead<CommandCatalogDetail>(shown.Data, out var detail))
                return detail;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _log.Log(ShellLogLevel.Warn, "catalog", "读取指令详情失败: " + ex.Message);
        }

        return null;
    }

    private CommandCatalogDetail LocalDetail(CommandDescriptor descriptor)
    {
        var entry = Find(descriptor.Name);
        var row = new CommandCatalogRow(
            descriptor.Name,
            entry?.Domain ?? _registry.GetDomain(descriptor.Name),
            descriptor.Summary,
            descriptor.Example,
            descriptor.Parameters.Count,
            _registry.GetSource(descriptor.Name),
            null,
            descriptor.Level == CommandLevel.Ask,
            descriptor.RequiresUiThread,
            descriptor.Readonly,
            descriptor.HiddenReason)
        {
            CommandClass = entry?.CommandClass ?? _registry.GetCommandClass(descriptor.Name),
            Method = CommandRegistry.GetMethod(descriptor.Name),
        };

        return new CommandCatalogDetail(row, descriptor.Parameters.Select(Convert).ToList())
        {
            Annotations = descriptor.Annotations,
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
