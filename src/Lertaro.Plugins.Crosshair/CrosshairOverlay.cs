using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Lertaro.Plugins.Crosshair;

/// <summary>
/// 准星叠加层：全屏（虚拟屏幕）、置顶、点击穿透、不抢焦点。
/// 穿透是硬要求 —— 准星挡在游戏画面上，任何一点命中测试都会吃掉瞄准点击。
/// WS_EX_TRANSPARENT + WS_EX_LAYERED + WS_EX_NOACTIVATE 让窗口对鼠标完全"不存在"。
/// </summary>
internal sealed class CrosshairOverlay : Window
{
    private static CrosshairOverlay? _instance;
    private readonly Canvas _canvas = new();

    private const int GwlExstyle = -20;
    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    private CrosshairOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        IsHitTestVisible = false;   // WPF 层就禁命中；Win32 层再加 WS_EX_TRANSPARENT 双保险
        Focusable = false;
        Background = Brushes.Transparent;
        Title = "屏幕准星";

        // 覆盖整个虚拟屏幕（所有显示器），准星画在主屏中心 + 用户偏移
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        Content = _canvas;
        SourceInitialized += ApplyClickThrough;
    }

    private void ApplyClickThrough(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, GwlExstyle)
            | WsExLayered
            | WsExTransparent
            | WsExNoActivate
            | WsExToolWindow;   // 不出现在 Alt+Tab
        _ = SetWindowLong(handle, GwlExstyle, style);
    }

    // ---- 进程级单例控制（必须在 UI 线程调用） ----

    internal static void ShowOverlay()
    {
        if (_instance is null)
        {
            _instance = new CrosshairOverlay();
        }

        _instance.Redraw();
        if (!_instance.IsVisible)
        {
            _instance.Show();
        }
    }

    internal static void HideOverlay()
    {
        if (_instance is { } alive)
        {
            alive.Close();
            _instance = null;
        }
    }

    internal static bool IsShown => _instance is { IsVisible: true };

    /// <summary>按当前设置重绘（设置面板"应用"后实时生效，不用重启）。</summary>
    internal static void Refresh()
    {
        if (_instance is { IsVisible: true })
        {
            _instance.Redraw();
        }
    }

    /// <summary>
    /// 按当前设置重绘。绘制逻辑统一在 CrosshairRenderer（编辑器预览共用同一实现，
    /// 保证"预览即所得"）。缩放基准取主屏宽度：与 okiaimx 的"屏幕宽度 ÷ 1920"一致，
    /// 4K 下准星同比放大，与游戏里的表现相同。
    /// </summary>
    private void Redraw()
    {
        _canvas.Children.Clear();

        var settings = CrosshairSettings.Current;
        var scale = CrosshairRenderer.ScaleForWidth(SystemParameters.PrimaryScreenWidth);

        CrosshairRenderer.Draw(
            _canvas,
            Width / 2 + settings.OffsetX,
            Height / 2 + settings.OffsetY,
            settings,
            scale);
    }
}
