using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Lertaro.Plugins.ClipboardHistory;

/// <summary>
/// 通知浮窗：右下角、置顶显示、不抢焦点（ShowActivated=false）、6 秒自动消失。
/// 点击浮窗打开剪贴板面板 —— 系统 Toast 需要 24 MB WinRT 依赖且点击回调做不了，
/// 因此这里自绘（零依赖、可点击、不受免打扰影响）。
/// </summary>
internal sealed class ClipboardToast : Window
{
    private const double ToastWidth = 340;
    private const double ToastHeight = 104;
    private const double EdgeMargin = 12; // 不能叫 Margin：会遮蔽 FrameworkElement.Margin
    private const double Gap = 8;

    private static readonly List<ClipboardToast> Active = [];
    private readonly DispatcherTimer _closeTimer;

    private ClipboardToast(ClipboardDueEvent due)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Manual;
        Width = ToastWidth;
        Height = ToastHeight;
        Title = "剪贴板提醒";

        var isRemind = due.Kind == ClipboardDueKind.Remind;
        var accent = isRemind
            ? Color.FromRgb(0xE0, 0x8A, 0x2E)
            : Color.FromRgb(0x5A, 0x8F, 0xD6);

        var bar = new Border
        {
            Width = 4,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(accent),
            Margin = new Thickness(0, 0, 10, 0)
        };

        var title = new TextBlock
        {
            Text = isRemind ? "待办提醒" : "置顶已到期",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(accent)
        };

        var body = new TextBlock
        {
            Text = due.Preview,
            FontSize = 13,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 36,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var hint = new TextBlock
        {
            Text = "点击查看该条目",
            FontSize = 11,
            Opacity = 0.55,
            Margin = new Thickness(0, 6, 0, 0)
        };

        var text = new StackPanel();
        text.Children.Add(title);
        text.Children.Add(body);
        text.Children.Add(hint);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(bar);
        row.Children.Add(text);

        var surface = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Background = SystemColors.WindowBrush,
            BorderBrush = new SolidColorBrush(accent),
            BorderThickness = new Thickness(1),
            Child = row,
            Cursor = Cursors.Hand
        };

        surface.MouseLeftButtonUp += (_, _) =>
        {
            CloseToast();
            ClipboardPanel.OpenTo(due.EntryId);
        };

        Content = surface;

        var close = new Button
        {
            Content = "×",
            Width = 20,
            Height = 20,
            FontSize = 12,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 6, 0),
            ToolTip = "关闭"
        };
        close.Click += (_, _) => CloseToast();

        // 关闭按钮浮在卡片上层（Grid 叠加）
        var layer = new Grid();
        layer.Children.Add(surface);
        layer.Children.Add(close);
        Content = layer;

        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _closeTimer.Tick += (_, _) => CloseToast();
        _closeTimer.Start();

        Closed += (_, _) =>
        {
            Active.Remove(this);
            Relayout();
        };
    }

    /// <summary>弹出一条通知（含提示音，可在设置中关闭）。必须在 UI 线程调用。</summary>
    internal static void Show(ClipboardDueEvent due)
    {
        if (ClipboardSettings.Current.ReminderSound)
        {
            NativeMethods.MessageBeep(NativeMethods.MbIconAsterisk);
        }

        var toast = new ClipboardToast(due);
        Active.Add(toast);
        toast.Show();
        Relayout();
    }

    /// <summary>从右下角向上堆叠（最多 4 条，超出丢弃最旧的）。</summary>
    private static void Relayout()
    {
        while (Active.Count > 4)
        {
            Active[0].Close();
        }

        var area = SystemParameters.WorkArea;
        for (var i = 0; i < Active.Count; i++)
        {
            var toast = Active[i];
            toast.Left = area.Right - ToastWidth - EdgeMargin;
            toast.Top = area.Bottom - EdgeMargin - ToastHeight - (i * (ToastHeight + Gap));
        }
    }

    /// <summary>
    /// 关闭本条通知。注意：不能命名为 Close —— 会遮蔽 <see cref="Window.Close"/> 造成无限递归。
    /// </summary>
    private void CloseToast()
    {
        _closeTimer.Stop();
        Close();
    }
}
