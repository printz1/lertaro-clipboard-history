using System.IO;
using Lertaro.PluginSdk;

namespace Lertaro.Plugins.ClipboardHistory;

/// <summary>
/// 事件驱动的剪贴板监听。
///
/// 为什么不用 GetClipboardSequenceNumber 轮询：微软文档明确写了它不是通知机制、
/// 不应放在轮询循环里（轮询太稀会漏掉快速连续的复制，太密就是无谓开销）。
/// 这里按官方推荐用 AddClipboardFormatListener + WM_CLIPBOARDUPDATE：
/// 没有变化时零开销，每一次变化都会收到通知。
///
/// 线程模型（三层，互不阻塞）：
///   1. 消息线程：创建 HWND_MESSAGE 隐藏窗口 + 消息循环，只负责收通知；
///   2. 读取线程：收到通知后真正打开剪贴板读数据。
///      单独一条线程是因为延迟渲染（delayed rendering）会让 GetClipboardData
///      阻塞最长 30 秒，绝不能把它放在消息循环里，否则通知就没人收了；
///   3. 调用线程（宿主 UI）：只读内存里的历史，不碰剪贴板。
/// </summary>
internal sealed class ClipboardListener : IDisposable
{
    private const string WindowClassName = "LertaroClipboardHistoryListener";
    private const int HotkeyId = 0x4C43;

    /// <summary>自定义消息：在消息线程上重新注册热键（RegisterHotKey 必须在窗口所属线程调用）。</summary>
    private const uint WM_APP_RELOAD_HOTKEY = 0x8000 + 1;

    private static NativeMethods.WndProcDelegate? _wndProcRef; // 必须保持引用，否则委托会被 GC 回收
    private static string? _pendingHotkey;

    private readonly ClipboardStore _store;
    private readonly Func<bool>? _isPaused;
    private readonly Action? _onHotkeyPressed;
    private readonly string? _initSummary;
    private string? _hotkeyText;
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _messageThread;
    private readonly Thread _readerThread;
    private volatile bool _stopping;
    private IntPtr _hwnd;
    private bool _hotkeyRegistered;
    private Exception? _startupError;

    internal ClipboardListener(
        ClipboardStore store,
        Func<bool>? isPaused = null,
        string? hotkey = null,
        Action? onHotkeyPressed = null,
        string? initSummary = null)
    {
        _store = store;
        _isPaused = isPaused;
        _hotkeyText = hotkey;
        _onHotkeyPressed = onHotkeyPressed;
        _initSummary = initSummary;

        _messageThread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "LertaroClipboardListener"
        };

        _readerThread = new Thread(ReaderLoop)
        {
            IsBackground = true,
            Name = "LertaroClipboardReader"
        };
    }

    internal int CaptureCount;
    internal int ExcludedCount;

    /// <summary>收到的 WM_CLIPBOARDUPDATE 次数（诊断链路断点用）。</summary>
    internal int NotificationCount;

    /// <summary>真正进入读取流程的次数。</summary>
    internal int ReadCount;

    /// <summary>读了但没拿到文本的次数（无文本格式 / 剪贴板被占用 / 超长被丢）。</summary>
    internal int EmptyReadCount;

    /// <summary>全局热键是否注册成功（供日志与自检使用）。</summary>
    internal bool HotkeyRegistered => _hotkeyRegistered;

    /// <summary>
    /// 让配置面板改完热键后立即生效，不必重启宿主。
    /// RegisterHotKey 必须在拥有窗口的线程上调用，所以这里只投递消息、由消息线程执行。
    /// </summary>
    internal bool UpdateHotkey(string? hotkey)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return false;
        }

        _pendingHotkey = hotkey;
        return NativeMethods.PostMessageW(_hwnd, WM_APP_RELOAD_HOTKEY, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>启动并等待监听窗口就绪。失败返回 false，由调用方回退到轮询。</summary>
    internal bool Start(TimeSpan timeout)
    {
        _messageThread.Start();
        _readerThread.Start();

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_startupError is not null)
            {
                Log("listener failed: " + _startupError.Message, LogLevel.Warn);
                return false;
            }

            if (_hwnd != IntPtr.Zero)
            {
                return true;
            }

            Thread.Sleep(20);
        }

        Log("listener startup timed out", LogLevel.Warn);
        return false;
    }

    private void MessageLoop()
    {
        try
        {
            var hInstance = NativeMethods.GetModuleHandleW(null);

            _wndProcRef = WndProc;
            var wndClass = new NativeMethods.WNDCLASSEX
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                style = 0,
                lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_wndProcRef),
                hInstance = hInstance,
                lpszClassName = WindowClassName
            };

            var atom = NativeMethods.RegisterClassExW(ref wndClass);
            if (atom == 0)
            {
                // 类已存在时也返回 0，这里不算致命
                var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                if (err != 1410 /* ERROR_CLASS_ALREADY_EXISTS */)
                {
                    Log("RegisterClassEx failed: " + err, LogLevel.Warn);
                }
            }

            _hwnd = NativeMethods.CreateWindowExW(
                0,
                WindowClassName,
                null,
                0,
                0, 0, 0, 0,
                NativeMethods.HWND_MESSAGE,
                IntPtr.Zero,
                hInstance,
                IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                _startupError = new InvalidOperationException(
                    "CreateWindowEx failed: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                return;
            }

            if (!NativeMethods.AddClipboardFormatListener(_hwnd))
            {
                _startupError = new InvalidOperationException(
                    "AddClipboardFormatListener failed: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                return;
            }

            ApplyHotkey();

            while (NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessageW(ref msg);
            }
        }
        catch (Exception ex)
        {
            _startupError ??= ex;
            Log("message loop crashed: " + ex.Message, LogLevel.Error);
        }
        finally
        {
            if (_hwnd != IntPtr.Zero)
            {
                if (_hotkeyRegistered)
                {
                    NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
                    _hotkeyRegistered = false;
                }

                NativeMethods.RemoveClipboardFormatListener(_hwnd);
                NativeMethods.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_CLIPBOARDUPDATE:
                Interlocked.Increment(ref NotificationCount);
                // 只置信号，不做任何耗时工作
                _signal.Set();
                return IntPtr.Zero;

            case NativeMethods.WM_HOTKEY:
                if (wParam.ToInt32() == HotkeyId)
                {
                    // 具体做什么由外部注入，监听器本身不认识"打开搜索窗"这类语义
                    _onHotkeyPressed?.Invoke();
                }

                return IntPtr.Zero;

            case WM_APP_RELOAD_HOTKEY:
                _hotkeyText = _pendingHotkey;
                ApplyHotkey();
                return IntPtr.Zero;

            case NativeMethods.WM_CLOSE:
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 由插件自己注册全局热键，而不是交给宿主 —— 这样 "Win+V" 这类宿主可能不认的组合也能用。
    /// 注册失败（例如系统重新启用了剪贴板历史而占用了 Win+V）会记 Warn，用户在日志里能看到。
    /// </summary>
    private void ApplyHotkey()
    {
        if (_hotkeyRegistered)
        {
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
            _hotkeyRegistered = false;
        }

        if (string.IsNullOrWhiteSpace(_hotkeyText))
        {
            return;
        }

        if (!HotkeyBinding.TryParse(_hotkeyText, out var binding) || !binding.IsValid)
        {
            Log("hotkey '" + _hotkeyText + "' could not be parsed, skipped", LogLevel.Warn);
            return;
        }

        if (NativeMethods.RegisterHotKey(_hwnd, HotkeyId, binding.Modifiers | NativeMethods.MOD_NOREPEAT, binding.VirtualKey))
        {
            _hotkeyRegistered = true;
            Log("global hotkey registered: " + binding.Normalized, LogLevel.Info);
            return;
        }

        var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        var hint = error == NativeMethods.ERROR_HOTKEY_ALREADY_REGISTERED
            ? " (already taken by another app; if this is Win+V, Windows clipboard history may have been enabled)"
            : string.Empty;

        Log(
            "global hotkey '" + binding.Normalized + "' registration FAILED, win32 error " + error + hint,
            LogLevel.Warn);
    }

    private void ReaderLoop()
    {
        while (!_stopping)
        {
            if (!_signal.WaitOne(1000))
            {
                continue;
            }

            // 合并堆积的通知：剪贴板只保留最新内容，中间状态没有意义
            while (_signal.WaitOne(0))
            {
            }

            if (_stopping)
            {
                break;
            }

            try
            {
                Capture();
            }
            catch (Exception ex)
            {
                Log("capture failed: " + ex.Message, LogLevel.Warn);
            }
        }
    }

    private void Capture()
    {
        if (_isPaused?.Invoke() == true)
        {
            return;
        }

        Interlocked.Increment(ref ReadCount);
        var result = ClipboardReader.Read();

        if (result.IsExcluded)
        {
            ExcludedCount++;
            Log("skipped sensitive content (" + result.SkipReason + ")", LogLevel.Debug);
            return;
        }

        var source = TryGetForegroundProcessName();
        var text = result.Text;

        if (string.IsNullOrEmpty(text))
        {
            // 没有文本不等于没有内容：截图/图片复制是位图，资源管理器复制是文件清单
            if (TryCaptureImage(source))
            {
                CaptureCount++;
                LogFirstCapture();
                return;
            }

            if (TryCaptureFiles(source))
            {
                CaptureCount++;
                LogFirstCapture();
                return;
            }

            Interlocked.Increment(ref EmptyReadCount);
            return;
        }

        if (_store.Add(text, source))
        {
            CaptureCount++;
            Log("captured " + text.Length + " chars from " + (source ?? "unknown")
                + " (store#" + _store.InstanceId + " entries=" + _store.Count + ")", LogLevel.Debug);
            LogFirstCapture();
        }
    }

    /// <summary>
    /// 构造期的初始化日志会被宿主吞掉（logger 尚未接线），
    /// 所以把恢复结果挂在第一次捕获时补一条 —— 这是判断"恢复是否成功"的可靠信号。
    /// </summary>
    private void LogFirstCapture()
    {
        if (CaptureCount == 1)
        {
            Log("first capture since start; " + (_initSummary ?? "no init summary"), LogLevel.Info);
        }
    }

    /// <summary>
    /// 图片采集：读位图 → 尺寸/大小把关 → 落盘 PNG → 进索引。
    /// 返回 true 表示"这次剪贴板变化已被图片路径处理"（包括被上限拦下），调用方不再计为空读。
    /// </summary>
    private bool TryCaptureImage(string? source)
    {
        var settings = ClipboardSettings.Current;
        if (!settings.CaptureImages)
        {
            return false;
        }

        var (data, isPng) = ClipboardReader.ReadImage();
        if (data is null || data.Length == 0)
        {
            return false;
        }

        var maxBytes = (long)settings.MaxImageMB * 1024 * 1024;
        if (data.Length > maxBytes)
        {
            Log("image skipped: " + ClipboardIndexEntry.FormatBytes(data.Length) + " exceeds limit " + settings.MaxImageMB + " MB", LogLevel.Debug);
            return true;
        }

        var (path, width, height, hash) = ClipboardImageCache.SaveAsPng(data, isPng);
        if (path is null)
        {
            return true;
        }

        var payloadBytes = new FileInfo(path).Length;

        if (_store.AddImage(path, hash, width, height, payloadBytes, source))
        {
            Log("captured image " + width + "x" + height + " (" + ClipboardIndexEntry.FormatBytes(payloadBytes) + ") from " + (source ?? "unknown")
                + " (store#" + _store.InstanceId + " entries=" + _store.Count + ")", LogLevel.Debug);
            return true;
        }

        // 重复图片：索引已把旧条目置顶，刚落盘的这份是冗余文件，删掉
        try
        {
            File.Delete(path);
        }
        catch
        {
            // 删不掉由启动清扫兜底
        }

        return true;
    }

    /// <summary>
    /// 文件采集：资源管理器/桌面复制文件时剪贴板里是 CF_HDROP。
    /// 只记录路径清单（不读文件内容）；单次上限 100 个路径 / 32K 字符。
    /// </summary>
    private bool TryCaptureFiles(string? source)
    {
        var paths = ClipboardReader.ReadFiles();
        if (paths is null || paths.Length == 0)
        {
            return false;
        }

        var pathList = string.Join("\n", paths);
        var hash = ClipboardIndexEntry.HashOf(pathList);

        if (_store.AddFiles(pathList, hash, paths.Length, source))
        {
            Log("captured " + paths.Length + " file(s) from " + (source ?? "unknown")
                + " (store#" + _store.InstanceId + " entries=" + _store.Count + ")", LogLevel.Debug);
        }

        // 重复清单：索引已置顶，无需处理
        return true;
    }

    private static string? TryGetForegroundProcessName()
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }

            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
            {
                return null;
            }

            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            var name = process.ProcessName;

            // 复制回填时前台窗口往往是宿主自己，过滤掉更干净
            return name.Equals("Lertaro.App", StringComparison.OrdinalIgnoreCase) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    internal static void Log(string message, LogLevel level)
    {
        try
        {
            Logger.Log("[ClipboardHistory] " + message, level);
        }
        catch
        {
            // 日志通道不可用时静默，绝不让日志本身把插件带崩
        }
    }

    public void Dispose()
    {
        _stopping = true;

        try
        {
            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch
        {
            // 忽略
        }

        _signal.Set();

        _messageThread.Join(TimeSpan.FromSeconds(2));
        _readerThread.Join(TimeSpan.FromSeconds(2));
        _signal.Dispose();
    }
}
