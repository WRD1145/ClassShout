using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace ClassShout.Classroom.Services;

/// <summary>
/// Windows 窗口层级控制。
///
/// 为什么需要它：Avalonia 的 <c>Window.Topmost</c> 只做一次 SetWindowPos(HWND_TOPMOST)。
/// 只要后面有别的程序也把自己设成置顶，我们就被压下去了 —— 对"课堂喊话"这种
/// 必须让学生看到的场景是不能接受的。所以强制档位需要周期性重申。
///
/// 用经典 DllImport 而不是 LibraryImport：后者要求整个项目开启 AllowUnsafeBlocks，
/// 为了三个 P/Invoke 给全项目打开 unsafe 不划算。
/// 全部调用都做了平台判断与句柄判空，非 Windows 平台上退化为空操作。
/// </summary>
internal static class WindowTopmost
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

#pragma warning disable SYSLIB1054 // 见类注释：刻意使用 DllImport
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int newValue);
#pragma warning restore SYSLIB1054

    /// <summary>
    /// 把窗口设为置顶，且不激活它（不抢焦点）。
    /// 不抢焦点很重要：教室端弹窗是为了提示，不能把老师正在操作的窗口顶掉。
    /// </summary>
    public static void BringToFront(Window window)
    {
        if (!OperatingSystem.IsWindows() || GetHandle(window) is not { } handle || handle == IntPtr.Zero)
        {
            return;
        }

        // 已经藏起来的窗口一概不碰。
        //
        // 原来这里的标志位里带着 SWP_SHOWWINDOW，而置顶看门狗是每秒重来一次的：
        // 弹窗 Hide() 之后，下一秒就被这个调用重新显示出来 ——
        // 于是屏幕上留下一个"看不见但仍在最上层、仍然接收鼠标点击"的窗口，
        // 正好压在那一片区域上，谁也点不动。
        if (!window.IsVisible)
        {
            return;
        }

        // 同理不再需要 SWP_SHOWWINDOW：窗口是 Show() 出来的，
        // 这里只负责把它提到最前，不该负责显示。
        SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    /// <summary>取消置顶。</summary>
    public static void ClearTopmost(Window window)
    {
        if (!OperatingSystem.IsWindows() || GetHandle(window) is not { } handle || handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(handle, HwndNotTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    /// <summary>
    /// 让窗口不进入 Alt+Tab 列表，并且点击时不抢焦点。
    /// 这样弹窗才像"通知"而不是"另一个应用窗口"。
    /// </summary>
    public static void MakeNonActivatingToolWindow(Window window)
    {
        if (!OperatingSystem.IsWindows() || GetHandle(window) is not { } handle || handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
    }

    private static IntPtr? GetHandle(Window window)
    {
        try
        {
            return window.TryGetPlatformHandle()?.Handle;
        }
        catch (InvalidOperationException)
        {
            // 窗口尚未创建原生句柄
            return null;
        }
    }
}
