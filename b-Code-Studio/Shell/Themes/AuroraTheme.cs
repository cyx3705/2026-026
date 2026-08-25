using System.Windows;
using AvalonDock.Themes;

namespace HistoryAurora.Shell.Themes;

/// <summary>
/// Aurora 的 AvalonDock 主题。**按实例下发，不按 URI 解析**（REQ-UI-029）。
///
/// 从前这里继承 <see cref="Theme"/> 并返回
/// `/HistoryAurora;component/Themes/AuroraTheme.xaml`。停靠管理器那一份没问题，
/// 但覆盖窗（拖动浮窗时那组蓝色方位指示所在的独立 Window）构造时会照着这个 URI
/// **再解析一遍**字典，而模块被装进可回收 ALC、入口程序集不是本程序集时，那条解析
/// 并不成立。失败后 WPF 不抛给我们：覆盖窗就带着 0 份字典活下来，所有画刷解析不到。
///
/// 表现极具迷惑性——元素照常排布、照常"可见"、尺寸正确、Z 序也在主窗体之上，
/// 渲染成位图后非透明像素为 0。真机实测：210 个元素、105 个可见、0 个像素。
///
/// 改继承 <see cref="DictionaryTheme"/>，字典由 ShellWindow.xaml 在编译期解析好
/// （BAML 这条路在宿主里是成立的），运行时只传实例，覆盖窗因此不再需要解析任何 URI。
/// </summary>
public sealed class AuroraTheme(ResourceDictionary dictionary) : DictionaryTheme(dictionary);
