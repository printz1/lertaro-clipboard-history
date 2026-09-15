using System.Windows.Media;
using Lertaro.PluginSdk;
using Lertaro.PluginSdk.Abstractions;
using Lertaro.PluginSdk.Abstractions.Plugins;
using Lertaro.PluginSdk.Services;

namespace Lertaro.Plugins.ClipboardHistory;

/// <summary>
/// 全局热键入口：模拟 Win+V 的体验 —— 按一下就打开宿主搜索窗并预填触发词。
/// 用 SDK 现成的 SearchWindowService.ShowWindow(query) 实现，不自己抢热键、
/// 也不自己建窗口，避免同进程里跟宿主的热键钩子打架。
/// </summary>
internal static class ClipboardHotkeyAction
{
    /// <summary>默认热键。刻意不占 Win+V —— 那是 Windows 自带剪贴板历史的键位。</summary>
    internal const string DefaultHotkey = "Ctrl+Shift+V";

    private static readonly Lazy<ImageSource> IconSource = new(CreateIcon, isThreadSafe: true);

    /// <summary>16x16 的剪贴板轮廓图标，纯几何绘制，不依赖外部资源。</summary>
    internal static ImageSource Icon => IconSource.Value;

    internal static ImageSource CreateIcon()
    {
        var geometry = Geometry.Parse("M4,3 H12 M3,3 A1,1 0 0 0 2,4 V14 A1,1 0 0 0 3,15 H13 A1,1 0 0 0 14,14 V4 A1,1 0 0 0 13,3 H12 M6,2 H10 V4 H6 Z");
        var drawing = new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromRgb(0x5F, 0x5E, 0x5A)), 1.2), geometry);

        var group = new DrawingGroup();
        group.Children.Add(drawing);

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    /// <summary>
    /// 打开剪贴板面板（不是往搜索框里塞触发词）。source 只用于日志 ——
    /// 全局热键与 Ctrl+O 菜单是两条不同路径，必须能区分。
    /// </summary>
    internal static void Invoke(string source)
    {
        try
        {
            ClipboardListener.Log("panel requested via " + source, LogLevel.Debug);
            ClipboardPanel.Toggle();
        }
        catch (Exception ex)
        {
            ClipboardListener.Log("panel request via " + source + " failed: " + ex.Message, LogLevel.Warn);
        }
    }
}
