using System.IO;
using System.Text;
using Lertaro.PluginSdk;

namespace Lertaro.Plugins.Crosshair;

/// <summary>
/// 诊断日志：宿主的 <see cref="Logger"/> 只进 app.log 与设置中心的实时日志查看器，
/// 用户排查问题时需要一份能直接发过来的文本，所以这里额外把同样的内容落到
/// 插件数据目录下的 <c>debug.log</c>（与 settings.json 同目录）。
///
/// 约定：写日志本身永不抛异常、永不阻塞功能；文件超过上限就砍掉前半段。
/// 记录的内容刻意保持克制 —— 只在"设置读写、显示/隐藏决策、异常"这些关键节点写。
/// </summary>
internal static class CrosshairDiagnostics
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();

    internal static string LogPath => Path.Combine(CrosshairSettings.SettingsDirectory, "debug.log");

    internal static void Log(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";

        try
        {
            Logger.Log("[Crosshair] " + message, LogLevel.Info);
        }
        catch
        {
            // 宿主日志通道不可用：只写文件
        }

        try
        {
            lock (Gate)
            {
                var path = LogPath;
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                Trim(path);
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 写盘失败绝不能影响准星本身
        }
    }

    internal static void LogException(string where, Exception ex) =>
        Log($"{where} 抛异常: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    /// <summary>超过上限就保留后半段，避免日志无限膨胀。</summary>
    private static void Trim(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= MaxBytes)
            {
                return;
            }

            var text = File.ReadAllText(path, Encoding.UTF8);
            var keep = text.Length / 2;
            File.WriteAllText(path, "…（旧日志已截断）…" + Environment.NewLine + text[keep..], Encoding.UTF8);
        }
        catch
        {
            // 截断失败就继续追加
        }
    }
}
