using HistoryAurora.Shell.Base;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// REQ-UI-097：Ctrl 标签态的起手判定。1.20.1 起按键靠轮询系统按键状态喂进来，
/// 判定本身脱离真键盘验：单独按住满 0.3 秒才进、碰了别的键就作废、一次按住只进一次、Aurora 不在眼前不进。
/// </summary>
public sealed class LabelModeHoldGateTests
{
    private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(300);

    [Fact]
    public void HoldingCtrlAloneForTheHoldEntersOncePerPress()
    {
        var gate = new LabelModeHoldGate(Hold);
        Assert.False(gate.Update(ctrlDown: true, otherKeyDown: false, shellInFront: true, 0));
        Assert.False(gate.Update(true, false, true, 250));
        Assert.True(gate.Update(true, false, true, 300));
        Assert.False(gate.Update(true, false, true, 400));

        Assert.False(gate.Update(false, false, true, 450));
        Assert.False(gate.Update(true, false, true, 500));
        Assert.True(gate.Update(true, false, true, 800));
    }

    /// <summary>Ctrl+C、Ctrl+点选：按住 Ctrl 期间碰了别的键或鼠标键，这一次按住作废，松开重按才算。</summary>
    [Fact]
    public void AnyOtherKeyDuringTheHoldSpoilsThePress()
    {
        var gate = new LabelModeHoldGate(Hold);
        gate.Update(true, false, true, 0);
        gate.Update(true, true, true, 100);
        Assert.False(gate.Update(true, false, true, 400));
        Assert.False(gate.Update(true, false, true, 1000));

        gate.Update(false, false, true, 1100);
        gate.Update(true, false, true, 1200);
        Assert.True(gate.Update(true, false, true, 1500));
    }

    [Fact]
    public void CtrlPressedWhileAnotherKeyIsHeldNeverCounts()
    {
        var gate = new LabelModeHoldGate(Hold);
        gate.Update(true, true, true, 0);
        Assert.False(gate.Update(true, false, true, 500));
    }

    /// <summary>按满时 Aurora 不在眼前（焦点在别的程序、鼠标也不在它上面）不进；鼠标挪过来就进。</summary>
    [Fact]
    public void WaitsUntilAuroraIsInFront()
    {
        var gate = new LabelModeHoldGate(Hold);
        gate.Update(true, false, false, 0);
        Assert.False(gate.Update(true, false, false, 400));
        Assert.True(gate.Update(true, false, true, 450));
    }
}
