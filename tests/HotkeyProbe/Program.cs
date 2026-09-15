using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace HotkeyProbe;

internal static class Program
{
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_CLOSE = 0x0010;
    private const int HWND_MESSAGE = -3;

    private static readonly List<(int Id, string Label)> Registered = [];

    private static void Main(string[] args)
    {
        var seconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 180;
        var reportPath = Path.Combine(
            Environment.GetEnvironmentVariable("TEMP") ?? AppContext.BaseDirectory,
            "hotkey-listen.txt");

        File.WriteAllText(reportPath, string.Empty);
        Report(reportPath, "probe start, listening for " + seconds + "s");

        var combinations = new (string Label, uint Mods, uint Vk)[]
        {
            ("Win+V", MOD_WIN, 0x56),
            ("Ctrl+Shift+V", MOD_CONTROL | MOD_SHIFT, 0x56),
            ("Alt+V", MOD_ALT, 0x56),
            ("Ctrl+Alt+V", MOD_CONTROL | MOD_ALT, 0x56),
            ("Ctrl+`", MOD_CONTROL, 0xC0)
        };

        var wndProcDelegate = new WndProcDelegate((hWnd, msg, wParam, lParam) =>
        {
            if (msg == WM_HOTKEY)
            {
                var id = wParam.ToInt32();
                var label = "id " + id;
                foreach (var item in Registered)
                {
                    if (item.Id == id)
                    {
                        label = item.Label;
                        break;
                    }
                }

                Report(reportPath, "*** WM_HOTKEY RECEIVED for " + label);
                return IntPtr.Zero;
            }

            if (msg == WM_CLOSE)
            {
                PostQuitMessage(0);
                return IntPtr.Zero;
            }

            return DefWindowProcW(hWnd, msg, wParam, lParam);
        });

        var hInstance = GetModuleHandleW(null!);
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProcDelegate),
            hInstance = hInstance,
            lpszClassName = "HotkeyListenProbe"
        };

        _ = RegisterClassExW(ref wc);

        var hwnd = CreateWindowExW(0, "HotkeyListenProbe", "probe", 0, 0, 0, 0, 0,
            new IntPtr(HWND_MESSAGE), IntPtr.Zero, hInstance, IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
        {
            Report(reportPath, "CreateWindowEx failed: " + Marshal.GetLastWin32Error());
            return;
        }

        var id = 900;
        foreach (var (label, mods, vk) in combinations)
        {
            id++;
            if (RegisterHotKey(hwnd, id, mods | MOD_NOREPEAT, vk))
            {
                Registered.Add((id, label));
                Report(reportPath, "registered OK   : " + label);
            }
            else
            {
                Report(reportPath, "register FAILED : " + label + " (win32 error " + Marshal.GetLastWin32Error() + ")");
            }
        }

        Report(reportPath, "--- now press the combinations; each delivery is appended below ---");

        var timer = new System.Threading.Timer(_ =>
        {
            foreach (var (rid, _) in Registered)
            {
                _ = UnregisterHotKey(hwnd, rid);
            }

            _ = DestroyWindow(hwnd);
            PostQuitMessage(0);
        }, null, seconds * 1000, Timeout.Infinite);

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        timer.Dispose();
        Report(reportPath, "probe end");

        GC.KeepAlive(wndProcDelegate);
    }

    private static void Report(string path, string line)
    {
        File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + Environment.NewLine);
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int w, int h, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
