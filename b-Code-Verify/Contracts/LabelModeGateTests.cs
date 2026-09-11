using HistoryAurora.Shell.Base;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// REQ-UI-097：Ctrl 标签态的起手判定。1.20.2 起没有按住延迟：单独按下 Ctrl 当拍就进、
/// 按下时别的键已按着就作废、一次按住只进一次、Aurora 不在眼前不进。
/// </summary>
public sealed class LabelModeGateTests
{
    [Fact]
    public void CtrlAloneEntersOnTheFirstTickAndOncePerPress()
    {
        var gate = new LabelModeGate();
        Assert.True(gate.Update(ctrlDown: true, otherKeyDown: false, shellInFront: true));
        Assert.False(gate.Update(true, false, true));

        Assert.False(gate.Update(false, false, true));
        Assert.True(gate.Update(true, false, true));
    }

    /// <summary>Ctrl+点选：按下 Ctrl 时鼠标键已经按着，这一次按住作废，松开重按才算。</summary>
    [Fact]
    public void CtrlPressedWhileAnotherKeyIsHeldNeverCounts()
    {
        var gate = new LabelModeGate();
        Assert.False(gate.Update(true, true, true));
        Assert.False(gate.Update(true, false, true));

        gate.Update(false, false, true);
        Assert.True(gate.Update(true, false, true));
    }

    /// <summary>进过一次、随后按了别的键被收回：不松开 Ctrl 不会再进。</summary>
    [Fact]
    public void AnotherKeyAfterEnteringSpendsTheRestOfThePress()
    {
        var gate = new LabelModeGate();
        Assert.True(gate.Update(true, false, true));
        Assert.False(gate.Update(true, true, true));
        Assert.False(gate.Update(true, false, true));
    }

    /// <summary>Aurora 不在眼前（焦点在别的程序、鼠标也不在它上面）不进；鼠标挪过来就进。</summary>
    [Fact]
    public void WaitsUntilAuroraIsInFront()
    {
        var gate = new LabelModeGate();
        Assert.False(gate.Update(true, false, false));
        Assert.False(gate.Update(true, false, false));
        Assert.True(gate.Update(true, false, true));
    }
}
