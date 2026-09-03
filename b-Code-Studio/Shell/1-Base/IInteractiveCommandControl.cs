namespace HistoryAurora.Shell.Base;

/// <summary>
/// 「点击归控件自己」的标记：命中它的控件不参与页面拖动判定。
///
/// WPF 自带的 <c>ButtonBase</c> / <c>MenuItem</c> / <c>TextBoxBase</c> / <c>ComboBox</c>
/// 基础层本来就认识；组件层自造的交互控件不行——原先 <c>ShellTopBarCoordinator</c> 里
/// 直接列着 <c>Widgets.AuroraOptionBox</c>，那是基础层认识组件层——
/// 分层里最不该破的那一条，而它被破了两版没人看见。
///
/// 现在方向反过来：组件层自己声明「我是交互控件」，基础层只认这个标记，
/// 组件层再加十个控件，基础层一行都不用改。
/// </summary>
internal interface IInteractiveCommandControl;
