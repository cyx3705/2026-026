using System.Runtime.InteropServices;

namespace HistoryAurora.Verify;

/// <summary>
/// 真实鼠标输入。用于"页面合并"这类**只能靠系统输入驱动**的冒烟：
/// 浮窗停靠依赖 <c>DefWindowProc</c> 的窗口移动循环（它才发 <c>WM_MOVING</c>，
/// AvalonDock 据此建 DragService 并画出那组蓝色方位指示），
/// 而移动循环不会被合成的 WPF 事件触发——必须是真的按下、真的移动。
///
/// 会占用物理鼠标，因此这套冒烟是**显式开关**，不进常规门禁。
/// </summary>
internal static class MouseInput
{
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventAbsolute = 0x8000;
    private const uint MouseEventVirtualDesk = 0x4000;
    private const int InputMouse = 0;

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    /// <summary>把指针移到屏幕物理像素坐标处。</summary>
    public static void MoveTo(int x, int y) => Send(MouseEventMove | MouseEventAbsolute | MouseEventVirtualDesk, x, y);

    public static void LeftDown() => Send(MouseEventLeftDown, 0, 0);

    public static void LeftUp() => Send(MouseEventLeftUp, 0, 0);

    public static (int X, int Y) Cursor()
    {
        GetCursorPos(out var point);
        return (point.X, point.Y);
    }

    /// <summary>从 from 走到 to，分若干步，每步之间停一会——一次瞬移不像拖动。</summary>
    public static void DragPath((int X, int Y) from, (int X, int Y) to, int steps, int stepDelayMilliseconds)
    {
        for (var i = 1; i <= steps; i++)
        {
            var x = from.X + ((to.X - from.X) * i / steps);
            var y = from.Y + ((to.Y - from.Y) * i / steps);
            MoveTo(x, y);
            Thread.Sleep(stepDelayMilliseconds);
        }
    }

    private static void Send(uint flags, int x, int y)
    {
        var input = new Input { Type = InputMouse };
        input.Data.Mouse.Flags = flags;

        if ((flags & MouseEventAbsolute) != 0)
        {
            var left = GetSystemMetrics(SmXVirtualScreen);
            var top = GetSystemMetrics(SmYVirtualScreen);
            var width = Math.Max(1, GetSystemMetrics(SmCxVirtualScreen));
            var height = Math.Max(1, GetSystemMetrics(SmCyVirtualScreen));
            input.Data.Mouse.X = (int)(((x - left) * 65535L) / width);
            input.Data.Mouse.Y = (int)(((y - top) * 65535L) / height);
        }

        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
            throw new InvalidOperationException("SendInput 被拒绝（前台窗口权限或输入被阻断）");
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    /// <summary>屏幕点上真正在最前面的是哪个窗口——点没打到目标时，这是第一个要看的。</summary>
    public static IntPtr WindowAt(int x, int y) => WindowFromPoint(new NativePoint { X = x, Y = y });

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInputData Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
}
