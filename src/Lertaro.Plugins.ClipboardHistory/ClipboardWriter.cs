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
    // 写回的预算要短：写本身是毫秒级，用户按下去就要"感觉立刻完成"。
    // 实测存在"写入成功但内容立刻被外部程序清空"的确定性干扰（对特定内容 25/25
    // 复现），对这类干扰重试毫无意义 —— 只会让人挂在"正在写回"上等 3 秒。
    // 所以：机械类失败（Open/Set）短重试；校验类失败（写完被改/被清）立刻报错。
    private const int MaxAttempts = 6;
    private const int RetryDelayMs = 50;

    /// <summary>
    /// 写回期间短暂抑制本进程的监听器回采（写回的内容本就来自历史，回采只会
    /// 产生重复条目和多余竞争）。跨进程的另一方监听器仍会收到更新，
    /// 但入库按指纹去重，只是把已有条目置顶，无害。
    /// </summary>
    internal static DateTime SuppressCaptureUntilUtc { get; private set; }

    /// <summary>最近一次成功写入时的剪贴板序列号。verify 失败时对比，判断是否有进程在写入后动过剪贴板。</summary>
    private static uint _lastWriteSeq;

    private static void MarkSuppression()
    {
        SuppressCaptureUntilUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(800);
    }

    internal static bool SetText(string text)
    {
        return SetText(text, out _);
    }

    /// <summary>带原因返回的写回：issue 为机械失败（打开/分配/放置）或校验失败（写完即被改动/清空）。</summary>
    internal static bool SetText(string text, out string? issue)
    {
        issue = null;

        if (string.IsNullOrEmpty(text))
        {
            issue = "内容为空";
            return false;
        }

        MarkSuppression();
        string? lastIssue = null;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var writeIssue = TryWriteOnce(text);
            if (writeIssue is null)
            {
                // 写入成功后校验一次。校验失败 = 有外部程序在写入后立刻改动/清空剪贴板
                // （实测存在对特定内容 25/25 确定性复现的清空行为），重试打不赢这种
                // 干扰，只会白白耗时 —— 立刻带原因失败，让用户重按一次即可。
                var verifyIssue = VerifyIssue(text);
                if (verifyIssue is null)
                {
                    return true;
                }

                ClipboardListener.Log(
                    "write-back verify failed: " + verifyIssue + " (attempts=" + (attempt + 1) + ")", LogLevel.Warn);
                issue = TranslateIssue(verifyIssue);
                return false;
            }

            lastIssue = writeIssue;

            // 机械失败（剪贴板被占用/内存分配失败）才值得短重试
            Thread.Sleep(RetryDelayMs);
        }

        ClipboardListener.Log(
            "failed to write text back after " + MaxAttempts + " attempts; last issue: " + lastIssue,
            LogLevel.Warn);
        issue = TranslateIssue(lastIssue ?? "unknown");
        return false;
    }

    /// <summary>把技术性失败原因翻译成用户能看懂的中文提示（日志里保留英文原文）。</summary>
    private static string TranslateIssue(string issue)
    {
        if (issue.StartsWith("OpenClipboard failed", StringComparison.Ordinal))
        {
            return "剪贴板被其他程序占用";
        }

        if (issue.StartsWith("clipboard reads back empty", StringComparison.Ordinal))
        {
            return "内容写入后被其他程序清空";
        }

        if (issue.StartsWith("content was replaced", StringComparison.Ordinal))
        {
            return "内容写入后被其他程序替换";
        }

        if (issue.StartsWith("clipboard was taken over", StringComparison.Ordinal))
        {
            return "内容写入后被其他程序接管";
        }

        if (issue.StartsWith("GlobalAlloc failed", StringComparison.Ordinal)
            || issue.StartsWith("GlobalLock failed", StringComparison.Ordinal)
            || issue.StartsWith("SetClipboardData failed", StringComparison.Ordinal))
        {
            return "系统内存操作失败";
        }

        return issue;
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

        MarkSuppression();
        string? lastIssue = null;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var writeIssue = TryWriteHdropOnce(existing);
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

    /// <summary>
    /// 把 DIB 位图写回剪贴板（CF_DIB）。只做写入，不做读回校验 ——
    /// SetClipboardData 成功即表示系统接管了这块内存；读回校验遇到
    /// "写入即被外部清空"的干扰只会造成无意义的重试与延迟。
    /// </summary>
    internal static bool SetDib(byte[] dib)
    {
        if (dib is null || dib.Length == 0)
        {
            return false;
        }

        MarkSuppression();
        string? lastIssue = null;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var issue = TryWriteDibOnce(dib);
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

            _lastWriteSeq = NativeMethods.GetClipboardSequenceNumber();
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

        // 关键修正：读回前剪贴板打不开（写入触发的更新让我们两条采集链路正在抢锁）
        // ≠ 内容被清空。此时无法完成校验，但写入本身已由系统确认成功 ——
        // 直接信任写入结果（用户实测：误报"被清空"时粘贴内容完好）。
        if (string.IsNullOrEmpty(read.Text) && !read.IsExcluded && ClipboardReader.LastOpenFailed)
        {
            ClipboardListener.Log("verify skipped: clipboard busy right after write, trusting the write", LogLevel.Debug);
            return null;
        }

        // 瞬态覆盖兜底：写入与校验只隔几毫秒，恰逢另一进程 Empty→Set 的中间窗口
        // （或监听链上的读取竞争）会读到"无文本格式"。等 60ms 复读一次，
        // 多数瞬态干扰能自愈；仍空才判失败并输出诊断数据。
        if (!read.IsExcluded && string.IsNullOrEmpty(read.Text))
        {
            Thread.Sleep(60);
            read = ClipboardReader.Read();
        }

        if (read.IsExcluded)
        {
            return "clipboard was taken over by another process right after the write (excluded-format content detected)";
        }

        if (string.IsNullOrEmpty(read.Text))
        {
            LogVerifyDiagnostics(expected);
            return "clipboard reads back empty after a successful write";
        }

        if (!string.Equals(read.Text, expected, StringComparison.Ordinal))
        {
            return "content was replaced by another process (wrote " + expected.Length + " chars, reads back " + read.Text!.Length + " chars)";
        }

        return null;
    }

    /// <summary>
    /// 校验失败时的现场取证：写入时 vs 此时的剪贴板序列号（变了 = 确有进程改写），
    /// 以及此刻剪贴板上还有哪些格式（有 HDROP = 被文件写入覆盖；空列表 = 被 Empty）。
    /// 有了这组数据就能定位"读回空"的真正来源，不用再猜。
    /// </summary>
    private static void LogVerifyDiagnostics(string expected)
    {
        try
        {
            var seqNow = NativeMethods.GetClipboardSequenceNumber();
            ClipboardListener.Log(
                "verify detail: wrote " + expected.Length + " chars, seqAtWrite=" + _lastWriteSeq
                + ", seqNow=" + seqNow + ", seqChanged=" + (seqNow != _lastWriteSeq)
                + ", formats=[" + DescribeClipboardFormats() + "]",
                LogLevel.Warn);
        }
        catch
        {
            // 诊断信息拿不到不影响主流程判定
        }
    }

    /// <summary>枚举当前剪贴板上的全部格式（标准格式给可读名，注册格式查系统登记名）。</summary>
    private static string DescribeClipboardFormats()
    {
        if (!NativeMethods.OpenClipboard(IntPtr.Zero))
        {
            return "open failed (win32 error " + Marshal.GetLastWin32Error() + ")";
        }

        try
        {
            var names = new List<string>();
            uint format = 0;
            while ((format = NativeMethods.EnumClipboardFormats(format)) != 0)
            {
                names.Add(FormatName(format));
            }

            return names.Count == 0 ? "EMPTY" : string.Join(", ", names);
        }
        finally
        {
            _ = NativeMethods.CloseClipboard();
        }
    }

    private static string FormatName(uint format)
    {
        var standard = format switch
        {
            1 => "CF_TEXT",
            2 => "CF_BITMAP",
            3 => "CF_METAFILEPICT",
            7 => "CF_OEMTEXT",
            8 => "CF_DIB",
            9 => "CF_PALETTE",
            13 => "CF_UNICODETEXT",
            14 => "CF_ENHMETAFILE",
            15 => "CF_HDROP",
            16 => "CF_LOCALE",
            17 => "CF_DIBV5",
            _ => null
        };

        if (standard is not null)
        {
            return standard;
        }

        if (format >= 0xC000)
        {
            var sb = new System.Text.StringBuilder(260);
            return NativeMethods.GetClipboardFormatNameW(format, sb, sb.Capacity) > 0
                ? sb.ToString()
                : "registered#" + format;
        }

        return "#" + format;
    }
}
