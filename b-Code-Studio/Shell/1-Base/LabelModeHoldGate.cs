namespace HistoryAurora.Shell.Base;

/// <summary>
/// Ctrl 标签态的起手判定（REQ-UI-097）：**单独**按住 Ctrl 满一段时间，而且 Aurora 在用户眼前。
///
/// 从窗体里拆出来是为了能脱离真键盘验：输入只有「此刻 Ctrl 在不在、别的键在不在、Aurora 在不在眼前、现在几点」，
/// 装配根每一拍轮询系统按键状态喂进来。
/// <list type="bullet">
///   <item>按下 Ctrl 之后任何时刻碰了别的键（含鼠标键），这一次按住就作废——Ctrl+C、Ctrl+点选都不闪成标签；</item>
///   <item>一次按住只起一次：进过标签态、又被程序收回，不松开 Ctrl 不会再进；</item>
///   <item>按满时 Aurora 不在眼前（焦点在别的程序、鼠标也不在 Aurora 上）不进，挪过来之后再进。</item>
/// </list>
/// </summary>
internal sealed class LabelModeHoldGate(TimeSpan hold)
{
    private readonly long _holdMilliseconds = (long)hold.TotalMilliseconds;
    private long? _pressedAt;
    private bool _spent;

    /// <returns>这一拍该进标签态。</returns>
    public bool Update(bool ctrlDown, bool otherKeyDown, bool shellInFront, long nowMilliseconds)
    {
        if (!ctrlDown)
        {
            _pressedAt = null;
            _spent = false;
            return false;
        }

        if (_pressedAt == null)
        {
            _pressedAt = nowMilliseconds;
            _spent = otherKeyDown;
        }
        else if (otherKeyDown)
        {
            _spent = true;
        }

        if (_spent || !shellInFront || nowMilliseconds - _pressedAt.Value < _holdMilliseconds)
            return false;

        _spent = true;
        return true;
    }
}
