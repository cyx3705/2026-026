namespace HistoryAurora.Shell.Components.Selection;

/// <summary>不依赖窗口系统的通道绑定解析，供桌面与 Web 声明校验共用。</summary>
public static class SelectionBinding
{
    public static bool TrySplit(string? binding, out string channel, out string column)
    {
        channel = "";
        column = "";
        if (string.IsNullOrWhiteSpace(binding)) return false;
        var cut = binding.LastIndexOf('.');
        if (cut <= 0 || cut == binding.Length - 1) return false;
        channel = binding[..cut];
        column = binding[(cut + 1)..];
        return true;
    }
}
