using System.Windows;
using System.Windows.Automation;

namespace HistoryAurora.Shell.Components.Actions;

/// <summary>一个动作的运行状态。可由多个按钮共享，不依附于虚拟化容器的生命周期。</summary>
/// <remarks>
/// <para>
/// **状态怎么到达按钮**（1.25.0，REQ-UI-124）。本类把状态**推**到每个挂上来的元素的
/// <see cref="AutomationProperties.ItemStatusProperty"/> 上，按钮模板的触发器只看那一个属性。
/// 此前是反过来的：模板用 <c>Path=(actions:AuroraCommandActivity.Activity).IsRunning</c>
/// 从按钮**拉**过来。那条路有两处会静默失效，而两处的症状一模一样——进度环一次都不出现：
/// </para>
/// <list type="number">
///   <item>路径第二段落在本类这个**裸 DependencyObject** 上。绑定引擎对它只取得到初值，
///         取不到后续变更通知（能发通知的是 FrameworkElement / FrameworkContentElement
///         这一支）。按钮建好时 <see cref="IsRunning"/> 必然是 false，于是它永远是 false。</item>
///   <item>路径第一段 <c>AuroraCommandActivity.Activity</c> 是**本程序集自有**的附加属性。
///         模板可能由 WPF 跨热重载缓存，缓存下来的那份记的是旧 ALC 里的属性身份；
///         新实例 <see cref="SetActivity"/> 写的是新身份，两者同名而不同一，触发器读不到。
///         同一条坑在 REQ-UI-117 的 <c>PageLabelMode</c> 上已经踩过一次，
///         当时的结论就是"模板只认框架自带的属性"。</item>
/// </list>
/// <para>
/// 推模型把两处一起消掉：写入落在按钮自己身上（FrameworkElement，通知确定有），
/// 落点是框架自带的属性（身份跨 ALC 确定唯一）。模板里因此不再出现任何本程序集的类型。
/// </para>
/// </remarks>
public sealed class AuroraCommandActivity : DependencyObject
{
    /// <summary>运行态的状态文本。按钮模板按它等于本值决定是否显示进度环。</summary>
    public const string RunningStatus = "运行中";

    /// <summary>空闲态。写空串而不是留着上一次的结果，否则"已完成"会一直挂在按钮上。</summary>
    public const string IdleStatus = "";

    public static readonly DependencyProperty ActivityProperty = DependencyProperty.RegisterAttached(
        "Activity", typeof(AuroraCommandActivity), typeof(AuroraCommandActivity));

    private static readonly DependencyPropertyKey IsRunningKey = DependencyProperty.RegisterReadOnly(
        nameof(IsRunning), typeof(bool), typeof(AuroraCommandActivity), new PropertyMetadata(false));
    public static readonly DependencyProperty IsRunningProperty = IsRunningKey.DependencyProperty;
    private static readonly DependencyPropertyKey StatusKey = DependencyProperty.RegisterReadOnly(
        nameof(Status), typeof(string), typeof(AuroraCommandActivity), new PropertyMetadata("就绪"));
    public static readonly DependencyProperty StatusProperty = StatusKey.DependencyProperty;
    public bool IsRunning => (bool)GetValue(IsRunningProperty);
    public string Status => (string)GetValue(StatusProperty);

    /// <summary>挂着本状态的元素。弱引用：回收容器换行、页面重建都不该把旧按钮钉住。</summary>
    private readonly List<WeakReference<DependencyObject>> _targets = [];

    public static AuroraCommandActivity? GetActivity(DependencyObject target)
        => (AuroraCommandActivity?)target.GetValue(ActivityProperty);

    /// <summary>
    /// 把状态挂到元素上。幂等；换挂另一份状态时先从旧那份上摘掉，
    /// 否则回收容器换行之后一个按钮会同时被两份状态写 ItemStatus。
    /// </summary>
    public static void SetActivity(DependencyObject target, AuroraCommandActivity? value)
    {
        ArgumentNullException.ThrowIfNull(target);

        var previous = GetActivity(target);
        if (ReferenceEquals(previous, value))
        {
            value?.Publish(target);
            return;
        }

        previous?.Forget(target);
        target.SetValue(ActivityProperty, value);

        // 摘掉状态的元素要回到空闲外观：不写这一下，虚拟化换行会把"运行中"留在一个
        // 已经换了行的按钮上，看起来是另一条记录在跑。
        if (value == null)
            AutomationProperties.SetItemStatus(target, IdleStatus);
        else
            value.Remember(target);
    }

    private void Remember(DependencyObject target)
    {
        Prune();
        foreach (var reference in _targets)
        {
            if (reference.TryGetTarget(out var existing) && ReferenceEquals(existing, target))
            {
                Publish(target);
                return;
            }
        }

        _targets.Add(new WeakReference<DependencyObject>(target));
        Publish(target);
    }

    private void Forget(DependencyObject target)
    {
        for (var index = _targets.Count - 1; index >= 0; index--)
        {
            if (!_targets[index].TryGetTarget(out var existing) || ReferenceEquals(existing, target))
                _targets.RemoveAt(index);
        }
    }

    private void Prune()
    {
        for (var index = _targets.Count - 1; index >= 0; index--)
        {
            if (!_targets[index].TryGetTarget(out _))
                _targets.RemoveAt(index);
        }
    }

    /// <summary>
    /// 把当前状态写到一个元素上。跨线程时编组回该元素自己的 Dispatcher。
    ///
    /// 写的是**完整状态文本**而不只是"跑没跑"：辅助技术读的就是这一格（这是 ItemStatus 的本职），
    /// 1.21.0 起它一直承载着完整状态，本轮不缩水。界面上只有恰好等于
    /// <see cref="RunningStatus"/> 时才显示进度环与状态角标，"已完成"只进辅助技术，不占版面。
    /// </summary>
    private void Publish(DependencyObject target)
    {
        var status = Status;
        if (target.Dispatcher.CheckAccess())
            AutomationProperties.SetItemStatus(target, status);
        else
            target.Dispatcher.InvokeAsync(() => AutomationProperties.SetItemStatus(target, status));
    }

    /// <summary>把当前状态写到全部挂着的元素上。</summary>
    private void PublishAll()
    {
        Prune();
        foreach (var reference in _targets.ToArray())
        {
            if (reference.TryGetTarget(out var target))
                Publish(target);
        }
    }

    /// <summary>同步进入运行态、拒绝重复执行；成功、失败及异常都恢复可操作状态。</summary>
    public async Task RunAsync(Func<Task<bool>> execute)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(execute);
        if (IsRunning) return;
        SetValue(IsRunningKey, true);
        SetValue(StatusKey, RunningStatus);
        PublishAll();
        try
        {
            var result = await execute().ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() => SetValue(StatusKey, result ? "已完成" : "失败或已取消"));
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => SetValue(StatusKey, "执行失败：" + ex.Message));
        }
        finally
        {
            await Dispatcher.InvokeAsync(() =>
            {
                SetValue(IsRunningKey, false);
                PublishAll();
            });
        }
    }
}
