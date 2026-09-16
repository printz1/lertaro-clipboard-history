using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Lertaro.Plugins.Crosshair;

/// <summary>
/// 全局热键：注册/注销 + WM_HOTKEY 分发。隐藏消息窗口挂在宿主 UI 线程上。
/// 热键字符串解析与 ClipboardHistory 的 HotkeyBinding 同一套规则（Ctrl+Alt+C 之类）。
/// </summary>
internal static class CrosshairHotkey
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0xC501;

    /// <summary>WS_POPUP：不显示标题栏/边框的顶层窗口。</summary>
    private const int WsPopup = unchecked((int)0x80000000);

    /// <summary>WS_EX_TOOLWINDOW：不出现在任务栏与 Alt+Tab。</summary>
    private const int WsExToolWindow = 0x00000080;

    /// <summary>WS_EX_NOACTIVATE：永不抢焦点。</summary>
    private const int WsExNoActivate = 0x08000000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    internal const uint ModAlt = 0x0001;
    internal const uint ModControl = 0x0002;
    internal const uint ModShift = 0x0004;
    internal const uint ModWin = 0x0008;

    private static HwndSource? _source;
    private static string _registered = string.Empty;

    /// <summary>按设置注册热键；返回是否注册成功（失败通常是键位被占）。</summary>
    internal static bool Apply(string hotkeyText)
    {
        var app = Application.Current;
        if (app is null || !app.Dispatcher.CheckAccess())
        {
            app?.Dispatcher.BeginInvoke(new Action(() => Apply(hotkeyText)));
            return false;
        }

        Unregister();

        if (string.IsNullOrWhiteSpace(hotkeyText))
        {
            return false;
        }

        if (!TryParse(hotkeyText, out var modifiers, out var virtualKey))
        {
            return false;
        }

        if (_source is null)
        {
            // 关键：用 HwndSource 直接建一个原生消息窗，**不要 new Window()**。
            // WPF 会把 Application.MainWindow 指向"第一个被实例化的 Window"；插件在宿主自己的
            // 窗口之前加载，一旦这里建了 WPF Window，宿主后面"显示/激活快速窗"的逻辑就会拿到
            // 错的 MainWindow —— 实测表现为：宿主精简搜索窗唤不醒（完整面板仍正常），
            // 卸载本插件即恢复。HwndSource 只产生原生 HWND，不进 Application.Windows。
            var parameters = new HwndSourceParameters("LertaroCrosshairHotkey")
            {
                WindowStyle = WsPopup,
                ExtendedWindowStyle = WsExToolWindow | WsExNoActivate,
                Width = 0,
                Height = 0,
                PositionX = 0,
                PositionY = 0
            };

            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
            // 消息窗保持存活；不 Dispose（注销热键后仍复用同一个 HWND）
        }

        if (RegisterHotKey(_source.Handle, HotkeyId, modifiers, virtualKey))
        {
            _registered = hotkeyText;
            return true;
        }

        return false;
    }

    internal static string Registered => _registered;

    private static void Unregister()
    {
        if (_source is not null && _registered.Length > 0)
        {
            UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = string.Empty;
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            CrosshairPlugin.Toggle("hotkey");
        }

        return IntPtr.Zero;
    }

    // ---- 解析："Ctrl+Alt+C" → 修饰键掩码 + 虚拟键码 ----

    private static bool TryParse(string text, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModControl;
                    break;
                case "alt":
                    modifiers |= ModAlt;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                case "win":
                case "windows":
                    modifiers |= ModWin;
                    break;
                default:
                    if (!TryParseKey(part, out virtualKey))
                    {
                        return false;
                    }

                    break;
            }
        }

        return virtualKey != 0;
    }

    private static bool TryParseKey(string token, out uint virtualKey)
    {
        virtualKey = 0;
        var key = token.Trim();
        if (key.Length == 0)
        {
            return false;
        }

        if (key.Length == 1)
        {
            var ch = char.ToUpperInvariant(key[0]);
            virtualKey = ch is >= 'A' and <= 'Z' or >= '0' and <= '9' ? (uint)ch : 0u;
            return virtualKey != 0;
        }

        if (key.Length is >= 2 and <= 3 && (key[0] is 'F' or 'f')
            && int.TryParse(key[1..], out var fn) && fn is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + fn - 1);
            return true;
        }

        return false;
    }
}
