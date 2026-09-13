using System.Windows;
using System.ComponentModel;

namespace HistoryAurora.Shell.Components.Actions;

/// <summary>一个动作的运行状态。可由多个按钮共享，不依附于虚拟化容器的生命周期。</summary>
public sealed class AuroraCommandActivity : DependencyObject, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
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

    public static AuroraCommandActivity? GetActivity(DependencyObject target)
        => (AuroraCommandActivity?)target.GetValue(ActivityProperty);
    public static void SetActivity(DependencyObject target, AuroraCommandActivity? value)
    {
        target.SetValue(ActivityProperty, value);
    }

    private void PublishActive(bool active) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));

    /// <summary>同步进入运行态、拒绝重复执行；成功、失败及异常都恢复可操作状态。</summary>
    public async Task RunAsync(Func<Task<bool>> execute)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(execute);
        if (IsRunning) return;
        SetValue(IsRunningKey, true);
        SetValue(StatusKey, "运行中");
        PublishActive(true);
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
            await Dispatcher.InvokeAsync(() => SetValue(IsRunningKey, false));
            PublishActive(false);
        }
    }
}
