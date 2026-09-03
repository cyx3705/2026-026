using HistoryAurora.Shell.Neutral.CommandSurface;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 界面发现调度闸门（REQ-UI-048）。注册表每登记一条命令就 Changed 一次，
/// 闸门必须把重叠请求收成在途一轮加一次补跑——否则冷启动会变成几十轮发现。
/// </summary>
public sealed class CoalescingAsyncWorkContractTests
{
    [Fact]
    public async Task OverlappingRunsShareOneTaskAndDrainOnceMore()
    {
        var work = new CoalescingAsyncWork<int>();
        var gate = new TaskCompletionSource();
        var calls = 0;

        async Task<int> Body()
        {
            var n = Interlocked.Increment(ref calls);
            await gate.Task.ConfigureAwait(false);
            return n;
        }

        var first = work.RunAsync(Body);
        var second = work.RunAsync(Body);
        var third = work.RunAsync(Body);

        Assert.Same(first, second);
        Assert.Same(first, third);

        gate.SetResult();
        Assert.Equal(2, await first);
        Assert.Equal(2, calls);
        Assert.Equal(2, await second);
    }

    [Fact]
    public async Task AFailedPassDoesNotJamTheGate()
    {
        var work = new CoalescingAsyncWork<int>();
        var first = work.RunAsync(() => throw new InvalidOperationException("boom"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);

        Assert.Equal(7, await work.RunAsync(() => Task.FromResult(7)));
    }
}
