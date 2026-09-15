using System.IO;
using System.Runtime.InteropServices;
using Lertaro.PluginSdk;

namespace Lertaro.Plugins.ClipboardHistory;

/// <summary>
/// 把文本写回剪贴板。自建面板里"选中并复制"需要这个能力
/// （宿主的 ActionType=Copy 只作用于搜索结果项，不适用于我们自己的窗口）。
/// 写完会读回来校验 —— 实测这台机器上偶发出现"格式在但内容为空"，
/// 所以带回读校验与重试，失败会让调用方看到。
/// </summary>
internal static class ClipboardWriter
{
    private const int MaxAttempts = 10;
    private const int RetryDelayMs = 80;

    internal static bool SetText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        string? lastIssue = null;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            // TryWriteOnce 返回失败原因（null = 写入成功），写入成功还要看内容是否立刻被别人覆盖
            var issue = TryWriteOnce(text) ?? VerifyIssue(text);
            if (issue is null)
            {
                return true;
            }

            lastIssue = issue;
            Thread.Sleep(RetryDelayMs);
        }

        ClipboardListener.Log(
            "failed to write text back after " + MaxAttempts + " attempts; last issue: " + lastIssue,
            LogLevel.Warn);
        return false;
    }

    /// <summary>
    /// 把文件清单写回剪贴板（CF_HDROP）。路径清单来自历史条目，
    /// 写回前先剔除已不存在的文件 —— 全部失效则直接失败并说明原因。
    /// </summary>
    internal static bool SetFiles(string[] paths, out string? issue)
    {
        issue = null;

        if (paths is null || paths.Length == 0)
        {
            issue = "路径清单为空";
            return false;
        }

        var existing = paths.Where(File.Exists).ToArray();
        if (existing.Length == 0)
        {
            issue = "清单里的文件已全部不存在（可能被移动或删除）";
            return false;
        }

        if (existing.Length < paths.Length)
        {
            ClipboardListener.Log("file paste-back: " + (paths.Length - existing.Length) + " of " + paths.Length + " files no longer exist, writing the rest", LogLevel.Debug);
        }

        string? lastIssue = null;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var writeIssue = TryWriteHdropOnce(existing) ?? VerifyHdropIssue();
            if (writeIssue is null)
            {
                if (existing.Length < paths.Length)
                {
                    issue = "部分文件已不存在，已写入其余 " + existing.Length + " 个";
                }

                return true;
            }

            lastIssue = writeIssue;
            Thread.Sleep(RetryDelayMs);
        }

        ClipboardListener.Log(
            "failed to write file list back after " + MaxAttempts + " attempts; last issue: " + lastIssue,
            LogLevel.Warn);
        issue = "写回剪贴板失败";
        return false;
    }

    /// <summary>构造 DROPFILES 头（fWide=1，UTF-16 路径列表，双 null 结尾）+ 路径数据。</summary>
    private static string? TryWriteHdropOnce(string[] paths)
    {
        var headerBytes = 20;
        var dataBytes = 2; // 结尾双 null
        foreach (var path in paths)
        {
            dataBytes += (path.Length + 1) * 2;
        }

        if (!NativeMethods.OpenClipboard(IntPtr.Zero))
        {
            return "OpenClipboard failed (win32 error " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ")";
        }

        try
        {
            _ = NativeMethods.EmptyClipboard();

            var handle = NativeMethods.GlobalAlloc(
                NativeMethods.GMEM_MOVEABLE | NativeMethods.GMEM_ZEROINIT,
                (UIntPtr)(headerBytes + dataBytes));

            if (handle == IntPtr.Zero)
            {
                return "GlobalAlloc failed";
            }

            var pointer = NativeMethods.GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                _ = NativeMethods.GlobalFree(handle);
                return "GlobalLock failed";
            }

            try
            {
                Marshal.WriteInt32(pointer, 0, headerBytes);          // pFiles
                Marshal.WriteInt32(pointer, 4, 0);                    // pt.x
                Marshal.WriteInt32(pointer, 8, 0);                    // pt.y
                Marshal.WriteInt32(pointer, 12, 0);                   // fNC
                Marshal.WriteInt32(pointer, 16, 1);                   // fWide = TRUE

                var offset = headerBytes;
                foreach (var path in paths)
                {
                    foreach (var ch in path)
                    {
                        Marshal.WriteInt16(pointer, offset, (short)ch);
                        offset += 2;
                    }

                    Marshal.WriteInt16(pointer, offset, 0);
                    offset += 2;
                }

                Marshal.WriteInt16(pointer, offset, 0); // 列表结束
            }
            finally
            {
                _ = NativeMethods.GlobalUnlock(handle);
            }

            if (NativeMethods.SetClipboardData(NativeMethods.CF_HDROP, handle) == IntPtr.Zero)
            {
                _ = NativeMethods.GlobalFree(handle);
                return "SetClipboardData failed";
            }

            return null;
        }
        finally
        {
            _ = NativeMethods.CloseClipboard();
        }
    }

    private static string? VerifyHdropIssue()
    {
        try
        {
            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                return "OpenClipboard failed during verify (win32 error " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ")";
            }

            if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_HDROP))
            {
                return "CF_HDROP not present right after a successful write (overwritten by another process?)";
            }

            return null;
        }
        finally
        {
            _ = NativeMethods.CloseClipboard();
        }
    }

    /// <summary>
    /// 把 DIB 位图写回剪贴板（CF_DIB）。写完只校验格式存在 —— 位图回读比对成本高，
    /// 且 SetClipboardData 成功即表示系统接管了这块内存，出错概率远低于文本路径。
    /// </summary>
    internal static bool SetDib(byte[] dib)
    {
        if (dib is null || dib.Length == 0)
        {
            return false;
        }

        string? lastIssue = null;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var issue = TryWriteDibOnce(dib) ?? VerifyDibIssue();
            if (issue is null)
            {
                return true;
            }

            lastIssue = issue;
            Thread.Sleep(RetryDelayMs);
        }

        ClipboardListener.Log(
            "failed to write image back after " + MaxAttempts + " attempts; last issue: " + lastIssue,
            LogLevel.Warn);
        return false;
    }

    /// <summary>null = 写入成功；否则返回败在哪一步。</summary>
    private static string? TryWriteDibOnce(byte[] dib)
    {
        if (!NativeMethods.OpenClipboard(IntPtr.Zero))
        {
            return "OpenClipboard failed (win32 error " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ")";
        }

        try
        {
            _ = NativeMethods.EmptyClipboard();

            var handle = NativeMethods.GlobalAlloc(
                NativeMethods.GMEM_MOVEABLE,
                (UIntPtr)dib.Length);

            if (handle == IntPtr.Zero)
            {
                return "GlobalAlloc failed";
            }

            var pointer = NativeMethods.GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                _ = NativeMethods.GlobalFree(handle);
                return "GlobalLock failed";
            }

            try
            {
                Marshal.Copy(dib, 0, pointer, dib.Length);
            }
            finally
            {
                _ = NativeMethods.GlobalUnlock(handle);
            }

            if (NativeMethods.SetClipboardData(NativeMethods.CF_DIB, handle) == IntPtr.Zero)
            {
                _ = NativeMethods.GlobalFree(handle);
                return "SetClipboardData failed";
            }

            return null;
        }
        finally
        {
            _ = NativeMethods.CloseClipboard();
        }
    }

    private static string? VerifyDibIssue()
    {
        try
        {
            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                return "OpenClipboard failed during verify (win32 error " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ")";
            }

            if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_DIB))
            {
                return "CF_DIB not present right after a successful write (overwritten by another process?)";
            }

            return null;
        }
        finally
        {
            _ = NativeMethods.CloseClipboard();
        }
    }

    /// <summary>null = 写入成功；否则返回败在哪一步（打开失败/分配失败/放置失败），供日志定位。</summary>
    private static string? TryWriteOnce(string text)
    {
        if (!NativeMethods.OpenClipboard(IntPtr.Zero))
        {
            return "OpenClipboard failed (win32 error " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ")";
        }

        try
        {
            _ = NativeMethods.EmptyClipboard();

            var bytes = (text.Length + 1) * sizeof(char);
            var handle = NativeMethods.GlobalAlloc(
                NativeMethods.GMEM_MOVEABLE | NativeMethods.GMEM_ZEROINIT,
                (UIntPtr)bytes);

            if (handle == IntPtr.Zero)
            {
                return "GlobalAlloc failed";
            }

            var pointer = NativeMethods.GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                _ = NativeMethods.GlobalFree(handle);
                return "GlobalLock failed";
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
            }
            finally
            {
                _ = NativeMethods.GlobalUnlock(handle);
            }

            // SetClipboardData 成功后所有权归系统，不能再 GlobalFree
            if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, handle) == IntPtr.Zero)
            {
                _ = NativeMethods.GlobalFree(handle);
                return "SetClipboardData failed";
            }

            return null;
        }
        finally
        {
            _ = NativeMethods.CloseClipboard();
        }
    }

    /// <summary>
    /// 写后回读校验。失败时区分两种情况：
    /// 命中排除格式 = 有进程（如密码管理器/某剪贴板工具）在我们写入后立刻接管了剪贴板；
    /// 内容不一致 = 有进程覆写了剪贴板。这两种重试才有意义，都记进日志。
    /// </summary>
    private static string? VerifyIssue(string expected)
    {
        var read = ClipboardReader.Read();
        if (read.IsExcluded)
        {
            return "clipboard was taken over by another process right after the write (excluded-format content detected)";
        }

        if (string.IsNullOrEmpty(read.Text))
        {
            return "clipboard reads back empty after a successful write";
        }

        if (!string.Equals(read.Text, expected, StringComparison.Ordinal))
        {
            return "content was replaced by another process (wrote " + expected.Length + " chars, reads back " + read.Text!.Length + " chars)";
        }

        return null;
    }
}
