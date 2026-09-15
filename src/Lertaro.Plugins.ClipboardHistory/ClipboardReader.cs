using System.Runtime.InteropServices;

namespace Lertaro.Plugins.ClipboardHistory;

/// <summary>一次剪贴板读取的结果。</summary>
internal readonly record struct ClipboardReadResult(string? Text, bool IsExcluded, string? SkipReason)
{
    internal static ClipboardReadResult Empty { get; } = new(null, false, null);

    internal static ClipboardReadResult Excluded(string reason) => new(null, true, reason);
}

/// <summary>
/// 剪贴板读取 + 敏感内容识别。
/// 敏感内容判定对齐 CopyQ / Windows 官方语义（Win+V 与密码管理器都用这套标记）。
/// Ditto 因为只认了 "Clipboard Viewer Ignore"，在 2025 年底被报过隐私问题，这里四个格式全认。
/// </summary>
internal static class ClipboardReader
{
    private const int OpenRetryCount = 4;
    private const int OpenRetryDelayMs = 25;

    /// <summary>超过这个长度直接丢弃，避免把巨型文本拖进内存和界面。</summary>
    internal const int MaxTextLength = 100_000;

    // 老格式，ClipMate 时代就存在，KeePass 2.x 等仍在使用
    private static readonly uint FmtClipboardViewerIgnore = SafeRegister("Clipboard Viewer Ignore");

    // Windows 10+ 云剪贴板 / 剪贴板历史引入的三个格式
    private static readonly uint FmtExcludeContentFromMonitor = SafeRegister("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint FmtCanIncludeInClipboardHistory = SafeRegister("CanIncludeInClipboardHistory");
    private static readonly uint FmtCanUploadToCloudClipboard = SafeRegister("CanUploadToCloudClipboard");

    // 浏览器/聊天软件复制图片时常带的完整 PNG 文件格式
    private static readonly uint FmtPng = SafeRegister("PNG");

    private static uint SafeRegister(string name)
    {
        try
        {
            return NativeMethods.RegisterClipboardFormatW(name);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return 0;
        }
    }

    /// <summary>
    /// 读取当前剪贴板文本。剪贴板被占用时短重试；
    /// 命中任何"排除"格式则返回 IsExcluded = true 且不带内容。
    /// </summary>
    internal static ClipboardReadResult Read()
    {
        for (var attempt = 0; attempt < OpenRetryCount; attempt++)
        {
            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(OpenRetryDelayMs);
                continue;
            }

            try
            {
                if (CheckExcluded(out var reason))
                {
                    return ClipboardReadResult.Excluded(reason);
                }

                if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_UNICODETEXT))
                {
                    return ClipboardReadResult.Empty;
                }

                return new ClipboardReadResult(ReadUnicodeText(), false, null);
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }

        return ClipboardReadResult.Empty;
    }

    private static bool CheckExcluded(out string reason)
    {
        reason = string.Empty;

        // 这两个格式只判"存在与否"，不看数据内容
        if (IsPresent(FmtClipboardViewerIgnore))
        {
            reason = "Clipboard Viewer Ignore";
            return true;
        }

        if (IsPresent(FmtExcludeContentFromMonitor))
        {
            reason = "ExcludeClipboardContentFromMonitorProcessing";
            return true;
        }

        // 这两个是 DWORD 值，等于 0 表示不允许计入历史 / 不允许上云
        if (IsPresent(FmtCanIncludeInClipboardHistory) && ReadDword(FmtCanIncludeInClipboardHistory) == 0)
        {
            reason = "CanIncludeInClipboardHistory = 0";
            return true;
        }

        if (IsPresent(FmtCanUploadToCloudClipboard) && ReadDword(FmtCanUploadToCloudClipboard) == 0)
        {
            reason = "CanUploadToCloudClipboard = 0";
            return true;
        }

        return false;
    }

    private static bool IsPresent(uint format)
        => format != 0 && NativeMethods.IsClipboardFormatAvailable(format);

    private static int? ReadDword(uint format)
    {
        if (!IsPresent(format))
        {
            return null;
        }

        var handle = NativeMethods.GetClipboardData(format);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var pointer = NativeMethods.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.ReadInt32(pointer);
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }
    }

    /// <summary>
    /// 读位图。优先级：CF_DIB（最通用，DIB 是"设备无关位图"原始数据）→ CF_DIBV5 → 注册格式 "PNG"。
    /// 命中排除格式时返回空 —— 敏感标记对所有内容类型一视同仁。
    /// </summary>
    internal static (byte[]? Data, bool IsPng) ReadImage()
    {
        for (var attempt = 0; attempt < OpenRetryCount; attempt++)
        {
            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(OpenRetryDelayMs);
                continue;
            }

            try
            {
                if (CheckExcluded(out _))
                {
                    return (null, false);
                }

                if (NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_DIB))
                {
                    return (ReadGlobal(NativeMethods.CF_DIB), false);
                }

                if (NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_DIBV5))
                {
                    return (ReadGlobal(NativeMethods.CF_DIBV5), false);
                }

                if (FmtPng != 0 && NativeMethods.IsClipboardFormatAvailable(FmtPng))
                {
                    return (ReadGlobal(FmtPng), true);
                }

                return (null, false);
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }

        return (null, false);
    }

    /// <summary>按格式读出整个内存块。先 GlobalSize 量大小，不猜长度。</summary>
    private static byte[]? ReadGlobal(uint format)
    {
        var handle = NativeMethods.GetClipboardData(format);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var pointer = NativeMethods.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var size = (long)NativeMethods.GlobalSize(handle);
            if (size <= 0 || size > MaxImageBytes)
            {
                return null;
            }

            var data = new byte[size];
            Marshal.Copy(pointer, data, 0, (int)size);
            return data;
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }
    }

    /// <summary>单张图片的读取上限（与设置里的单张上限一致，双保险）。</summary>
    internal const long MaxImageBytes = 200L * 1024 * 1024;

    /// <summary>文件清单的上限：路径总字符 32K、最多 100 个、原始数据 64 KB。</summary>
    internal const int MaxFileListChars = 32_000;
    internal const int MaxFileCount = 100;
    private const long MaxHdropBytes = 64 * 1024;

    /// <summary>
    /// 读文件清单（CF_HDROP，资源管理器/桌面复制文件的标准载体）。
    /// 只解析 DROPFILES 头 + 路径列表，不读文件内容 —— 路径清单体积小、
    /// 回写时原样构造回去即可。命中排除格式时返回 null。
    /// </summary>
    internal static string[]? ReadFiles()
    {
        for (var attempt = 0; attempt < OpenRetryCount; attempt++)
        {
            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(OpenRetryDelayMs);
                continue;
            }

            try
            {
                if (CheckExcluded(out _) || !NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_HDROP))
                {
                    return null;
                }

                var handle = NativeMethods.GetClipboardData(NativeMethods.CF_HDROP);
                if (handle == IntPtr.Zero)
                {
                    return null;
                }

                var pointer = NativeMethods.GlobalLock(handle);
                if (pointer == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    var size = (long)NativeMethods.GlobalSize(handle);
                    if (size < 20 || size > MaxHdropBytes)
                    {
                        return null;
                    }

                    var data = new byte[size];
                    Marshal.Copy(pointer, data, 0, (int)size);
                    return ParseHdrop(data);
                }
                finally
                {
                    NativeMethods.GlobalUnlock(handle);
                }
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }

        return null;
    }

    /// <summary>解析 DROPFILES：前 20 字节是头（pFiles=路径区偏移，fWide=是否 UTF-16），后面是双 null 结尾的路径列表。</summary>
    private static string[]? ParseHdrop(byte[] data)
    {
        var offset = ReadInt32(data, 0);
        var wide = ReadInt32(data, 16) != 0;
        if (offset < 20 || offset > data.Length)
        {
            return null;
        }

        var paths = new List<string>(16);

        if (wide)
        {
            var chars = new char[(data.Length - offset) / 2];
            Buffer.BlockCopy(data, offset, chars, 0, chars.Length * 2);

            var start = 0;
            for (var i = 0; i <= chars.Length; i++)
            {
                var endOfString = i == chars.Length;
                if (!endOfString && chars[i] != '\0')
                {
                    continue;
                }

                if (i == start)
                {
                    break; // 双 null：列表结束
                }

                paths.Add(new string(chars, start, i - start));
                if (paths.Count >= MaxFileCount || i + 1 > chars.Length)
                {
                    break;
                }

                start = i + 1;
            }
        }
        else
        {
            // ANSI 路径列表（老程序）：按系统默认编码解，尽力而为
            var text = System.Text.Encoding.Default.GetString(data, offset, data.Length - offset);
            foreach (var line in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                paths.Add(line);
                if (paths.Count >= MaxFileCount)
                {
                    break;
                }
            }
        }

        if (paths.Count == 0)
        {
            return null;
        }

        var total = 0;
        foreach (var p in paths)
        {
            total += p.Length;
        }

        return total > MaxFileListChars ? null : paths.ToArray();
    }

    private static int ReadInt32(byte[] data, int offset)
        => data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);

    /// <summary>
    /// 读 CF_UNICODETEXT。先用 lstrlenW 量长度再分配：
    /// 直接 PtrToStringUni 会在超长内容上先分配一大块内存，且没法提前拦截。
    /// </summary>
    private static string? ReadUnicodeText()
    {
        var handle = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var pointer = NativeMethods.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var length = NativeMethods.lstrlenW(pointer);
            if (length <= 0 || length > MaxTextLength)
            {
                return null;
            }

            return Marshal.PtrToStringUni(pointer, length);
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }
    }
}
