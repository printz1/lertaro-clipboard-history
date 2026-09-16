using System.IO;

namespace Lertaro.Plugins.ClipboardHistory;

/// <summary>条目种类。文本负载在内存字典里，图片负载在磁盘 PNG 文件里，文件条目的负载是路径清单。</summary>
internal enum ClipboardEntryKind
{
    Text,
    Image,
    File
}

/// <summary>
/// 常驻内存的索引条目：只保留预览与元数据，**不含负载**。
/// 文本负载交给字典（P1 在内存，P2 可以整体换成磁盘/SQLite），
/// 图片负载是缓存目录里的 PNG 文件（索引只留路径与像素尺寸）。
/// 这一层是稳定契约，将来换存储不用动检索逻辑。
///
/// 内存开销参考：每条约 (Preview 200 + SearchText 600) 字符 ≈ 2 KB，
/// 5000 条约 10 MB —— 这是容量上限的取值依据。
/// </summary>
internal sealed class ClipboardIndexEntry
{
    internal const int PreviewLength = 200;
    internal const int SearchTextLength = 600;

    internal ClipboardIndexEntry(long id, string text, string? sourceProcess, DateTime createdAt)
    {
        Id = id;
        Kind = ClipboardEntryKind.Text;
        SourceProcess = sourceProcess;
        CreatedAt = createdAt;
        Length = text.Length;
        Hash = HashOf(text);
        Preview = BuildPreview(text);
        SearchText = BuildSearchText(text);
    }

    /// <summary>图片条目。负载是磁盘上的 PNG 文件，索引只留路径与像素尺寸。</summary>
    internal ClipboardIndexEntry(
        long id,
        ulong hash,
        int pixelWidth,
        int pixelHeight,
        long payloadBytes,
        string imagePath,
        string? sourceProcess,
        DateTime createdAt)
    {
        Id = id;
        Kind = ClipboardEntryKind.Image;
        SourceProcess = sourceProcess;
        CreatedAt = createdAt;
        Length = payloadBytes;
        Hash = hash;
        ImagePath = imagePath;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        Preview = "[图片] " + pixelWidth + "\u00D7" + pixelHeight;

        // 图片条目没有可检索的正文；给一组固定关键词，让"图"、"image"能把它们搜出来
        SearchText = "图片 image png 图像";
    }

    /// <summary>
    /// 文件条目。负载是换行分隔的路径清单（不读文件内容，体积小，直接进负载层）；
    /// 文件名进 SearchText，按文件名就能搜到这条复制。
    /// </summary>
    internal ClipboardIndexEntry(
        long id,
        ulong hash,
        int fileCount,
        string pathList,
        string? sourceProcess,
        DateTime createdAt)
    {
        Id = id;
        Kind = ClipboardEntryKind.File;
        SourceProcess = sourceProcess;
        CreatedAt = createdAt;
        Length = fileCount;
        Hash = hash;

        var first = pathList.Split('\n')[0];
        var firstName = Path.GetFileName(first.TrimEnd('\\'));
        Preview = "[文件] " + fileCount + " 项 · " + firstName + (fileCount > 1 ? " 等" : string.Empty);

        // 文件名进检索文本：按文件名就能搜到这条复制
        var searchText = "文件 file " + pathList.Replace('\n', ' ');
        SearchText = searchText[..Math.Min(searchText.Length, SearchTextLength)].ToLowerInvariant();
    }

    internal ClipboardEntryKind Kind { get; }

    internal long Id { get; }

    /// <summary>列表标题：文本为首行截断，图片为"[图片] 宽×高"。</summary>
    internal string Preview { get; }

    /// <summary>小写化的检索文本，仅供第一级廉价子串过滤使用。</summary>
    internal string SearchText { get; }

    /// <summary>文本为字符数，图片为 PNG 文件字节数。</summary>
    internal long Length { get; }

    /// <summary>内容指纹，用于去重。FNV-1a 64 位：零分配、够快，冲突概率可忽略。</summary>
    internal ulong Hash { get; }

    internal string? SourceProcess { get; }

    internal DateTime CreatedAt { get; }

    internal bool IsPinned { get; set; }

    /// <summary>
    /// 收藏：与置顶语义不同 —— 收藏条目永不淘汰（不过期、不受容量/预算限制）、
    /// 持久化永久保留，除非用户显式取消或删除；置顶只影响排序。
    /// </summary>
    internal bool IsFavorite { get; set; }

    /// <summary>
    /// 待办：默认永久置顶，且与收藏同级豁免淘汰（未完成的事不该被清掉），可设提醒时间。
    /// </summary>
    internal bool IsTodo { get; set; }

    /// <summary>置顶到期时间。null = 无期限置顶（仅排序）。</summary>
    internal DateTime? PinnedUntil { get; set; }

    /// <summary>提醒时间（单次）。到点触发一次提醒后由 Store 清空。</summary>
    internal DateTime? RemindAt { get; set; }

    /// <summary>待办已归档（完成）。归档 ≠ 删除：内容与记录保留，只是从进行中的待办里收起。</summary>
    internal bool IsTodoArchived { get; set; }

    internal DateTime? ArchivedAt { get; set; }

    /// <summary>
    /// 该条只存在于"收藏/待办"独立存储（不在历史里）。用于：取消最后一个标记时，
    /// 若它已不属于历史，就从列表移除（内容只在 marks 存储里的条目不该继续占位）。
    /// </summary>
    internal bool MarksOnly { get; set; }

    // ---- 图片专属（文本条目为默认值） ----

    internal string ImagePath { get; } = string.Empty;

    internal int PixelWidth { get; }

    internal int PixelHeight { get; }

    internal string Subtitle
    {
        get
        {
            var size = Kind switch
            {
                ClipboardEntryKind.Image => FormatBytes(Length),
                ClipboardEntryKind.File => Length + " 个文件",
                _ => Length.ToString("N0") + " 字符"
            };

            var text = CreatedAt.ToString("HH:mm:ss") + "  ·  " + size;
            if (!string.IsNullOrEmpty(SourceProcess))
            {
                text += "  ·  " + SourceProcess;
            }

            if (IsTodo)
            {
                text = (IsTodoArchived ? "已归档  ·  " : "待办  ·  ") + text;
            }
            else if (IsFavorite)
            {
                text = "已收藏  ·  " + text;
            }
            else if (IsPinned)
            {
                text = "已固定  ·  " + text;
            }

            if (RemindAt is { } remind)
            {
                text += "  ·  提醒 " + remind.ToString("MM-dd HH:mm");
            }

            if (PinnedUntil is { } until)
            {
                text += "  ·  置顶至 " + until.ToString("MM-dd HH:mm");
            }

            return text;
        }
    }

    internal static string FormatBytes(long bytes)
    {
        return bytes >= 1024 * 1024
            ? (bytes / 1048576.0).ToString("0.#") + " MB"
            : Math.Max(1, (long)Math.Round(bytes / 1024.0)) + " KB";
    }

    private static string BuildPreview(string text)
    {
        var line = text;
        var breakIndex = text.IndexOfAny(['\r', '\n']);
        if (breakIndex >= 0)
        {
            line = text[..breakIndex];
        }

        line = line.Trim();
        if (line.Length == 0)
        {
            line = text.Trim();
        }

        if (line.Length == 0)
        {
            return "(空白内容)";
        }

        // 关键防线：\r\n 之外的换行字符（U+2028/U+2029 行分隔符、\v、\f 等）
        // IndexOfAny 抓不到，但 WPF TextBlock 会把它们当显式换行渲染，
        // 行高会随内容爆炸。一律替换成空格，保证列表行高恒定。
        line = SanitizeLine(line);

        return line.Length <= PreviewLength ? line : line[..PreviewLength] + "...";
    }

    internal static string SanitizeLine(string line)
    {
        var needsReplace = false;
        foreach (var ch in line)
        {
            if (ch is '\r' or '\n' or '\v' or '\f' or '\u2028' or '\u2029')
            {
                needsReplace = true;
                break;
            }
        }

        if (!needsReplace)
        {
            return line;
        }

        return string.Create(line.Length, line, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var ch = source[i];
                span[i] = ch is '\r' or '\n' or '\v' or '\f' or '\u2028' or '\u2029' ? ' ' : ch;
            }
        });
    }

    private static string BuildSearchText(string text)
    {
        var length = Math.Min(text.Length, SearchTextLength);
        return text[..length].ToLowerInvariant();
    }

    /// <summary>FNV-1a 64 位散列。不做加密用途，只做去重指纹（零分配）。</summary>
    internal static ulong HashOf(string value)
    {
        const ulong OffsetBasis = 14695981039346656037UL;
        const ulong Prime = 1099511628211UL;

        var hash = OffsetBasis;
        foreach (var ch in value)
        {
            hash ^= (byte)(ch & 0xFF);
            hash *= Prime;
            hash ^= (byte)(ch >> 8);
            hash *= Prime;
        }

        return hash;
    }

    /// <summary>字节版指纹，用于图片去重（对原始 DIB/PNG 字节计算，同一张图重复复制会被并成一条）。</summary>
    internal static ulong HashOfBytes(byte[] value)
    {
        const ulong OffsetBasis = 14695981039346656037UL;
        const ulong Prime = 1099511628211UL;

        var hash = OffsetBasis;
        foreach (var b in value)
        {
            hash ^= b;
            hash *= Prime;
        }

        return hash;
    }
}
