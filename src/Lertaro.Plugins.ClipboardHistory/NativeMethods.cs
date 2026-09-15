using System.Runtime.InteropServices;

namespace Lertaro.Plugins.ClipboardHistory;

/// <summary>
/// 剪贴板相关的 Win32 调用。
/// 全部走 P/Invoke 而不是 WPF 的 Clipboard 类：后者要求 STA 线程，
/// 而后台监听线程是 MTA，用 Win32 才能不受线程套间约束。
/// </summary>
internal static class NativeMethods
{
    internal const uint CF_UNICODETEXT = 13;

    // 位图格式：CF_DIB = BITMAPINFOHEADER(可含调色板) + 像素位；CF_DIBV5 = BITMAPV5HEADER 版本
    internal const uint CF_DIB = 8;
    internal const uint CF_DIBV5 = 17;

    // 文件清单格式：资源管理器/桌面复制文件时的标准载体（DROPFILES 结构 + 路径列表）
    internal const uint CF_HDROP = 15;

    internal const uint WM_CLOSE = 0x0010;
    internal const uint WM_CLIPBOARDUPDATE = 0x031D;
    internal const uint WM_HOTKEY = 0x0312;

    // RegisterHotKey 修饰键
    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;
    internal const uint MOD_SHIFT = 0x0004;
    internal const uint MOD_WIN = 0x0008;
    internal const uint MOD_NOREPEAT = 0x4000;

    internal const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    internal const int GWLP_USERDATA = -21;

    /// <summary>HWND_MESSAGE：只收消息、不显示的窗口父句柄。</summary>
    internal static readonly IntPtr HWND_MESSAGE = new(-3);

    /// <summary>剪贴板内容每次变化，该序号自增。只用于校验缓存是否失效，不做轮询。</summary>
    [DllImport("user32.dll", SetLastError = false)]
    internal static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>取鼠标屏幕坐标。用于区分"用户真的动了鼠标"和"内容滚到光标下面"。</summary>
    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    /// <summary>复制完成后把焦点还给用户原来所在的窗口，省掉一次手动切换。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GlobalLock(IntPtr hMem);

    /// <summary>取内存块字节数。读 CF_DIB 前用来判定大小，避免越界复制。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern UIntPtr GlobalSize(IntPtr hMem);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalUnlock(IntPtr hMem);

    // ---- 写剪贴板（面板里"复制回去"要用；宿主的 Copy 动作只作用于搜索结果项） ----

    internal const uint GMEM_MOVEABLE = 0x0002;
    internal const uint GMEM_ZEROINIT = 0x0040;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    /// <summary>取宽字符串长度（不含结尾 null）。用来在分配字符串前判定大小。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern int lstrlenW(IntPtr lpString);

    // ---- 剪贴板格式监听：事件驱动，取代轮询 ----

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    // ---- 全局热键：由插件自己注册，不依赖宿主的键位解析能力 ----

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---- 隐藏消息窗口 ----

    internal delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEX
    {
        internal int cbSize;
        internal uint style;
        internal IntPtr lpfnWndProc;
        internal int cbClsExtra;
        internal int cbWndExtra;
        internal IntPtr hInstance;
        internal IntPtr hIcon;
        internal IntPtr hCursor;
        internal IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] internal string lpszClassName;
        internal IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        internal IntPtr hwnd;
        internal uint message;
        internal IntPtr wParam;
        internal IntPtr lParam;
        internal uint time;
        internal int ptX;
        internal int ptY;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string? lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int nExitCode);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
