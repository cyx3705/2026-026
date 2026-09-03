using System.Globalization;
using System.Windows;
using HistoryAurora.Shell.Base.Docking;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Shell.Components.Modules;

/// <summary>
/// 认领带 <c>ui.window</c> 注解的命令：执行后把 <see cref="CommandResult.Data"/> 里的
/// 活对象停靠进布局。5.0 起宿主不再转发 IShellUiRegistrar，这是模块露出窗格的路径。
/// </summary>
internal sealed class UiAnnotationClaimer(
    CommandBus bus,
    IDockingService docking,
    IShellLog log)
{
    public const string OwnerPrefix = "annotation:";

    private readonly HashSet<string> _owners = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public async Task<int> ClaimAsync(CancellationToken cancellation = default)
    {
        var claimed = bus.Registry.All()
            .Select(descriptor => new
            {
                Descriptor = descriptor,
                WindowId = descriptor.Annotation(UiAnnotationKeys.Window),
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.WindowId))
            .ToList();

        lock (_gate)
        {
            foreach (var owner in _owners.ToList())
            {
                try
                {
                    docking.UnregisterOwner(owner);
                }
                catch (Exception ex)
                {
                    log.Log(ShellLogLevel.Warn, "ui-claim", $"回收注解窗格失败 ({owner}): {ex.Message}");
                }
            }

            _owners.Clear();
        }

        var registered = 0;
        foreach (var item in claimed)
        {
            cancellation.ThrowIfCancellationRequested();
            if (await ClaimOneAsync(item.Descriptor, item.WindowId!, cancellation).ConfigureAwait(true))
                registered++;
        }

        return registered;
    }

    private async Task<bool> ClaimOneAsync(
        CommandDescriptor descriptor,
        string windowId,
        CancellationToken cancellation)
    {
        CommandResult result;
        try
        {
            result = await bus.ExecuteAsync(descriptor.Name, "UI", cancellation).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            log.Log(ShellLogLevel.Warn, "ui-claim", $"{descriptor.Name} 执行失败: {ex.Message}");
            return false;
        }

        if (!result.Success)
        {
            log.Log(ShellLogLevel.Warn, "ui-claim", $"{descriptor.Name} 返回失败: {result.Message}");
            return false;
        }

        var content = ResolveContent(result.Data);
        if (content == null)
        {
            log.Log(ShellLogLevel.Warn, "ui-claim",
                $"{descriptor.Name} 未返回可停靠对象（需要 UIElement 或工厂）");
            return false;
        }

        var owner = OwnerOf(descriptor.Name);
        var title = descriptor.Annotation(UiAnnotationKeys.Title);
        if (string.IsNullOrWhiteSpace(title))
            title = string.IsNullOrWhiteSpace(descriptor.Summary) ? windowId : descriptor.Summary;

        try
        {
            docking.RegisterWindow(new ToolWindowDescriptor
            {
                Id = windowId.Trim(),
                Title = title.Trim(),
                DefaultSide = ParseSide(descriptor.Annotation(UiAnnotationKeys.Side)),
                DefaultRatio = ParseRatio(descriptor.Annotation(UiAnnotationKeys.Ratio)),
                ContentFactory = () => content,
            }, owner);
        }
        catch (Exception ex)
        {
            log.Log(ShellLogLevel.Warn, "ui-claim", $"{windowId} 注册失败: {ex.Message}");
            return false;
        }

        lock (_gate)
            _owners.Add(owner);
        return true;
    }

    private string OwnerOf(string commandName)
    {
        var source = bus.Registry.GetSource(commandName);
        if (source.StartsWith("module:", StringComparison.OrdinalIgnoreCase))
        {
            var module = source["module:".Length..].Trim();
            if (module.Length > 0)
                return OwnerPrefix + module;
        }

        return OwnerPrefix + commandName;
    }

    private static object? ResolveContent(object? data)
    {
        if (data is UIElement element)
            return element;
        if (data is Func<object> factory)
            return factory();
        return null;
    }

    private static DockSide ParseSide(string? side)
        => (side ?? "").Trim().ToLowerInvariant() switch
        {
            "left" => DockSide.Left,
            "top" => DockSide.Top,
            "bottom" => DockSide.Bottom,
            "center" => DockSide.Center,
            "tab" => DockSide.Tab,
            _ => DockSide.Right,
        };

    private static double ParseRatio(string? ratio)
        => double.TryParse(ratio, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
           && value is > 0 and < 1
            ? value
            : 0.25;
}
