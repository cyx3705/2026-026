namespace HistoryAurora.Shell.Neutral.CommandSurface;

/// <summary>
/// 把重叠的异步工作收成「在途一轮 + 必要时再补一轮」。
///
/// 注册表每登记一条命令就 <c>Changed</c> 一次。界面若每次都去拉页面、拉动作、
/// 再问一遍 <c>vulcan.command.list</c>，冷启动会变成几十轮发现——真机 1.7.0
/// 测过 230ms 内 34 轮、104 次权威目录查询。页面拉取器自己已经会合并重入，
/// 但调度发生在它外面：每一次 <c>BeginInvoke</c> 都等上一轮结束才开始，
/// 于是合并永远打不中。闸门必须架在调度这一层。
/// </summary>
internal sealed class CoalescingAsyncWork<T>
{
    private readonly object _gate = new();
    private Task<T>? _inFlight;
    private bool _pending;

    public Task<T> RunAsync(Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        TaskCompletionSource<T> completion;
        lock (_gate)
        {
            if (_inFlight != null)
            {
                _pending = true;
                return _inFlight;
            }

            completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlight = completion.Task;
        }

        _ = DrainAsync(completion, work);
        return completion.Task;
    }

    private async Task DrainAsync(TaskCompletionSource<T> completion, Func<Task<T>> work)
    {
        try
        {
            while (true)
            {
                var result = await work().ConfigureAwait(true);
                lock (_gate)
                {
                    if (_pending)
                    {
                        _pending = false;
                        continue;
                    }

                    _inFlight = null;
                }

                completion.SetResult(result);
                return;
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _inFlight = null;
                _pending = false;
            }

            completion.SetException(ex);
        }
    }
}

/// <summary><see cref="CoalescingAsyncWork{T}"/> 的无返回值形态。</summary>
internal sealed class CoalescingAsyncWork
{
    private readonly CoalescingAsyncWork<bool> _inner = new();

    public Task RunAsync(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return _inner.RunAsync(async () =>
        {
            await work().ConfigureAwait(true);
            return true;
        });
    }
}
