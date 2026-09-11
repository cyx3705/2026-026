namespace HistoryAurora.Shell.Base;

/// <summary>
/// Ctrl 标签态的起手判定（REQ-UI-097）：**单独**按下 Ctrl，而且 Aurora 在用户眼前，当拍就进。
///
/// 1.20.2 删掉了按住 0.3 秒的延迟（用户真机试下来延迟不明显，只是让起手变慢）。
/// 从窗体里拆出来是为了能脱离真键盘验：输入只有「此刻 Ctrl 在不在、别的键在不在、Aurora 在不在眼前」，
/// 装配根每一拍轮询系统按键状态喂进来。
/// <list type="bullet">
///   <item>按下 Ctrl 时别的键（含鼠标键）已经按着，这一次按住作废——Ctrl+点选不闪成标签；</item>
///   <item>一次按住只起一次：进过标签态、又被收回（比如随后按了 C），不松开 Ctrl 不会再进；</item>
///   <item>Aurora 不在眼前（焦点在别的程序、鼠标也不在 Aurora 上）不进，挪过来之后再进。</item>
/// </list>
/// </summary>
internal sealed class LabelModeGate
{
    private bool _pressed;
    private bool _spent;

    /// <returns>这一拍该进标签态。</returns>
    public bool Update(bool ctrlDown, bool otherKeyDown, bool shellInFront)
    {
        if (!ctrlDown)
        {
            _pressed = false;
            _spent = false;
            return false;
        }

        if (!_pressed)
        {
            _pressed = true;
            _spent = otherKeyDown;
        }
        else if (otherKeyDown)
        {
            _spent = true;
        }

        if (_spent || !shellInFront)
            return false;

        _spent = true;
        return true;
    }
}
