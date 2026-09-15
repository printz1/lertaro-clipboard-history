using System.Runtime.InteropServices;

namespace Lertaro.Plugins.ClipboardHistory.SmokeTest;

/// <summary>
/// 测试用的剪贴板写入器。
/// 直接用 Win32 写入而不是 WPF 的 Clipboard 类：既能同时附带"排除"格式，
/// 也不需要 STA 线程。
/// </summary>
internal static class ClipboardTestHelper
{
    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint GMEM_ZEROINIT = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    private static readonly uint FmtExcludeContentFromMonitor =
        NativeMethods.RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");

    /// <summary>写一段文本；excluded = true 时同时附上"排除监控"格式，模拟密码管理器。</summary>
    internal static bool SetText(string text, bool excluded = false)    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(30);
                continue;
            }

            try
            {
                EmptyClipboard();

                if (excluded)
                {
                    var flagHandle = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (UIntPtr)8);
                    if (flagHandle != IntPtr.Zero)
                    {
                        _ = SetClipboardData(FmtExcludeContentFromMonitor, flagHandle);
                    }
                }

                var bytes = (text.Length + 1) * sizeof(char);
                var handle = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (UIntPtr)bytes);
                if (handle == IntPtr.Zero)
                {
                    return false;
                }

                var pointer = NativeMethods.GlobalLock(handle);
                if (pointer == IntPtr.Zero)
                {
                    _ = GlobalFree(handle);
                    return false;
                }

                try
                {
                    Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
                }
                finally
                {
                    NativeMethods.GlobalUnlock(handle);
                }

                // SetClipboardData 成功后所有权归系统，不能再 GlobalFree
                if (SetClipboardData(NativeMethods.CF_UNICODETEXT, handle) == IntPtr.Zero)
                {
                    _ = GlobalFree(handle);
                    return false;
                }

                return true;
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }

        return false;
    }

    /// <summary>剪贴板序列号：用来确认写入是否真的改变了剪贴板内容。</summary>
    internal static uint SequenceNumber() => NativeMethods.GetClipboardSequenceNumber();
}
