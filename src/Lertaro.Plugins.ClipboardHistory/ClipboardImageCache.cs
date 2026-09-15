using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lertaro.PluginSdk;

namespace Lertaro.Plugins.ClipboardHistory;

/// <summary>
/// 图片负载的编解码与缓存管理。所有方法都可在任意线程调用（WPF Imaging 的
/// 解码/编码本身不受 STA 限制，产物 Freeze 后跨线程安全）。
///
/// 为什么落盘 PNG 而不是把 DIB 存内存：一张 4K 截图的 DIB 约 33 MB，常驻内存
/// 几张就把 64M 负载预算吃光；PNG 落盘后索引只留路径，淘汰策略照常生效。
/// </summary>
internal static class ClipboardImageCache
{
    /// <summary>缓存目录总上限：超过后按最旧优先删除，防止长期使用无限膨胀。</summary>
    private const long CacheSizeLimit = 200L * 1024 * 1024;

    private static string CacheDirectory => Path.Combine(ClipboardSettings.SettingsDirectory, "images");

    /// <summary>
    /// 把剪贴板位图数据落盘为 PNG。isPng 时数据就是完整 PNG 文件，直接解码；
    /// 否则按 DIB 处理：补一个 14 字节的 BITMAPFILEHEADER 让 WPF 的 BMP 解码器能读。
    /// 统一转成 Bgr32 后编码：一是固定回写格式，二是让指纹只取决于像素内容 ——
    /// 回写再采集（DIB 重建的字节与原始 DIB 不同）也能和原条目对上去重。
    /// 返回 (路径, 像素宽, 像素高, 像素指纹)，失败返回路径 null。
    /// </summary>
    internal static (string? Path, int Width, int Height, ulong Hash) SaveAsPng(byte[] data, bool isPng)
    {
        try
        {
            var source = isPng ? DecodePng(data) : DecodeDib(data);
            if (source is null)
            {
                return (null, 0, 0, 0);
            }

            var normalized = source.Format == PixelFormats.Bgr32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);

            var width = normalized.PixelWidth;
            var height = normalized.PixelHeight;
            if (width <= 0 || height <= 0)
            {
                return (null, 0, 0, 0);
            }

            var hash = HashPixels(normalized);

            var dir = CacheDirectory;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png");

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(normalized));

            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }

            return (path, width, height, hash);
        }
        catch (Exception ex)
        {
            ClipboardListener.Log("image encode failed: " + ex.Message, LogLevel.Warn);
            return (null, 0, 0, 0);
        }
    }

    /// <summary>对解码后的像素缓冲做 FNV-1a。Bgr32 的 stride 恒为宽×4，无对齐填充，哈希稳定。</summary>
    private static ulong HashPixels(BitmapSource source)
    {
        var stride = source.PixelWidth * 4; // Bgr32 = 4 字节/像素
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        return ClipboardIndexEntry.HashOfBytes(pixels);
    }

    /// <summary>DIB 没有文件头，补上 BITMAPFILEHEADER 才能交给标准 BMP 解码器。</summary>
    private static BitmapSource? DecodeDib(byte[] dib)
    {
        if (dib.Length < 40)
        {
            return null;
        }

        var headerSize = ReadInt32(dib, 0);
        if (headerSize is < 40 or > 200 || dib.Length <= headerSize)
        {
            return null;
        }

        var bitCount = ReadUInt16(dib, 14);
        var colorsUsed = ReadInt32(dib, 32);
        var paletteEntries = bitCount <= 8 ? (colorsUsed == 0 ? 1 << bitCount : colorsUsed) : 0;
        var offBits = 14 + headerSize + paletteEntries * 4;

        var bmp = new byte[14 + dib.Length];
        bmp[0] = 0x42; // 'B'
        bmp[1] = 0x4D; // 'M'
        WriteInt32(bmp, 2, bmp.Length);
        WriteInt32(bmp, 10, offBits);
        Buffer.BlockCopy(dib, 0, bmp, 14, dib.Length);

        var decoder = new BmpBitmapDecoder(
            new MemoryStream(bmp),
            BitmapCreateOptions.None,
            BitmapCacheOption.OnLoad);

        if (decoder.Frames.Count == 0)
        {
            return null;
        }

        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    private static BitmapSource? DecodePng(byte[] data)
    {
        var decoder = new PngBitmapDecoder(
            new MemoryStream(data),
            BitmapCreateOptions.None,
            BitmapCacheOption.OnLoad);

        if (decoder.Frames.Count == 0)
        {
            return null;
        }

        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    /// <summary>
    /// 解码小尺寸缩略图（DecodePixelHeight 让 WIC 只解到目标高度，不解全图，4K 截图也只要几毫秒）。
    /// 文件缺失或损坏返回 null，由调用方保持占位。
    /// </summary>
    internal static BitmapSource? LoadThumbnail(string path, int decodePixelHeight)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelHeight = decodePixelHeight;
            image.StreamSource = new MemoryStream(File.ReadAllBytes(path));
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            ClipboardListener.Log("thumbnail decode failed: " + ex.Message, LogLevel.Debug);
            return null;
        }
    }

    /// <summary>
    /// 浮窗大图解码：宽 560 起步（原图更小则用原宽）；
    /// 超高长图限制解码高度 ≤ 8192px，防止几十 MB 的位图进内存。
    /// </summary>
    internal static BitmapSource? DecodeImageForFlyout(string path, int pixelWidth, int pixelHeight)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var decodeWidth = pixelWidth > 0 ? Math.Min(560, pixelWidth) : 560;
            if (pixelWidth > 0 && pixelHeight > 0)
            {
                var decodeHeight = (long)pixelHeight * decodeWidth / pixelWidth;
                if (decodeHeight > 8192)
                {
                    decodeWidth = Math.Max(120, (int)(8192L * pixelWidth / pixelHeight));
                }
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = decodeWidth;
            image.StreamSource = new MemoryStream(File.ReadAllBytes(path));
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            ClipboardListener.Log("flyout image decode failed: " + ex.Message, LogLevel.Debug);
            return null;
        }
    }

    /// <summary>
    /// 粘贴回写：PNG 解码 → 统一转 Bgr32 → 编码 BMP → 去掉 14 字节文件头还原成 CF_DIB。
    /// 32bpp BI_RGB 是 Windows 剪贴板图片事实上的通用形态，几乎所有应用都认。
    /// </summary>
    internal static byte[]? LoadDibForPaste(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var decoder = new PngBitmapDecoder(
                new MemoryStream(File.ReadAllBytes(path)),
                BitmapCreateOptions.None,
                BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0)
            {
                return null;
            }

            var converted = new FormatConvertedBitmap(
                decoder.Frames[0],
                PixelFormats.Bgr32,
                null,
                0);

            var encoder = new BmpBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(converted));

            using var stream = new MemoryStream();
            encoder.Save(stream);
            var bmp = stream.ToArray();

            if (bmp.Length <= 14)
            {
                return null;
            }

            var dib = new byte[bmp.Length - 14];
            Buffer.BlockCopy(bmp, 14, dib, 0, dib.Length);
            return dib;
        }
        catch (Exception ex)
        {
            ClipboardListener.Log("image decode for paste failed: " + ex.Message, LogLevel.Warn);
            return null;
        }
    }

    /// <summary>
    /// 启动清扫：历史只存内存，重启后磁盘上必然全是孤儿文件，全部删掉；
    /// 另外总量超过上限时按最旧优先裁剪。扫描结果只写一条日志。
    /// </summary>
    internal static void SweepCache(IReadOnlyCollection<string> referencedPaths)
    {
        try
        {
            var dir = CacheDirectory;
            if (!Directory.Exists(dir))
            {
                return;
            }

            var referenced = referencedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            var keep = new List<(string Path, DateTime Modified)>();

            foreach (var file in Directory.EnumerateFiles(dir, "*.png"))
            {
                if (!referenced.Contains(file))
                {
                    DeleteBestEffort(file);
                    continue;
                }

                keep.Add((file, File.GetLastWriteTimeUtc(file)));
                total += new FileInfo(file).Length;
            }

            var removed = 0;
            if (total > CacheSizeLimit)
            {
                foreach (var (path, _) in keep.OrderBy(k => k.Modified))
                {
                    if (total <= CacheSizeLimit)
                    {
                        break;
                    }

                    var size = new FileInfo(path).Length;
                    if (DeleteBestEffort(path))
                    {
                        total -= size;
                        removed++;
                    }
                }
            }

            if (removed > 0)
            {
                ClipboardListener.Log(
                    "image cache trimmed: removed " + removed + " file(s), now " +
                    ClipboardIndexEntry.FormatBytes(total), LogLevel.Info);
            }
        }
        catch (Exception ex)
        {
            ClipboardListener.Log("image cache sweep failed: " + ex.Message, LogLevel.Warn);
        }
    }

    private static bool DeleteBestEffort(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int ReadInt32(byte[] data, int offset)
        => data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);

    private static ushort ReadUInt16(byte[] data, int offset)
        => (ushort)(data[offset] | (data[offset + 1] << 8));

    private static void WriteInt32(byte[] data, int offset, int value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
    }
}
