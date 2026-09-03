namespace HistoryAurora.Shell.Base;

/// <summary>
/// 基础层向宿主窗口问「当前是不是暗色」的唯一入口。
///
/// 弹窗要按主题上色，而主题是装配根那一层的状态。原先的写法是
/// <c>_owner is ShellWindow shell &amp;&amp; shell.IsDarkTheme</c>——基础层直接认识装配根，
/// 正是分层里最不该出现的方向。改成窗口自己实现这个接口之后，
/// 基础层拿到的仍然只是一份纯数据（一个 bool），谁来实现它并不知道。
/// </summary>
internal interface IThemedWindow
{
    bool IsDarkTheme { get; }
}
