using HistoryVulcan.Core.Modules;

namespace HistoryAurora.Module;

/// <summary>
/// HistoryAurora 的模块装配入口。
///
/// 2.1.0 起 Aurora 是装在宿主上的无界面服务：Attach 拉起 Aurora Web 并登记为宿主唯一前端，
/// 界面只在浏览器里（REQ-WEB-040/041）。卸载走 <see cref="IDisposable"/>。
/// </summary>
public sealed class AuroraBusinessComposition : IModuleContextAware, IDisposable
{
    private AuroraService? _service;

    public void Attach(IModuleContext context)
    {
        _service?.Dispose();
        _service = AuroraService.Start(context);
    }

    public void Dispose()
    {
        _service?.Dispose();
        _service = null;
    }
}
