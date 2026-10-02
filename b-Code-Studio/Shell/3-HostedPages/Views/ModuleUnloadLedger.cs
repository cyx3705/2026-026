using HistoryAurora.Shell.Neutral.CommandSurface;

namespace HistoryAurora.Shell.HostedPages.Views;

/// <summary>
/// 本次会话从模块页载出的模块（REQ-UI-137）。<c>vulcan.module.unload</c> 之后宿主的模块清单里就没有它了，
/// 不记下来的话那一行直接从表上消失，「载入」按钮也就无处可点。
///
/// 只记在内存里：载出本身不改磁盘，宿主重启或任何一次重载都会把它装回来，台账跟着作废即可。
/// </summary>
public sealed class ModuleUnloadLedger
{
    private readonly Dictionary<string, ModuleMeta> _modules = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ModuleMeta> All => _modules.Values.ToList();

    public bool Contains(string name) => _modules.ContainsKey(name);

    public void Remember(ModuleMeta module) => _modules[module.ModuleName] = module;

    public void Forget(string name) => _modules.Remove(name);
}
