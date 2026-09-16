using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Lertaro.Plugins.ClipboardHistory;

/// <summary>
/// 通知浮窗：右下角、置顶显示、不抢焦点（ShowActivated=false）、8 秒自动消失，点击跳转到条目。
///
/// 三个修正（用户实测反馈）：
///   1. 声音：改用 winmm PlaySound 播放系统通知音（MessageBeep 在部分声音方案下无声），
///      失败再回退 MessageBeep；开关沿用设置里的"提醒提示音"。
///   2. 配色：与主面板同一套主题 —— 优先用面板采样到的画刷，未打开过面板时按深浅色兜底。
///   3. 排版：按 左侧色条 / 标题行（含关闭） / 正文 / 提示 四段布局，不再用 Grid 叠加。
/// </summary>
internal sealed class ClipboardToast : Window
{
    private const double ToastWidth = 360;
    private const double ToastHeight = 108;
    private const double EdgeMargin = 12;
    private const double Gap = 8;

    private static readonly List<ClipboardToast> Active = [];
    private readonly DispatcherTimer _closeTimer;

    private ClipboardToast(ClipboardDueEvent due)
    {
        var isRemind = due.Kind == ClipboardDueKind.Remind;
        var accent = isRemind
            ? Color.FromRgb(0xE0, 0x8A, 0x2E)
            : Color.FromRgb(0x5A, 0x8F, 0xD6);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = ToastWidth;
        Height = ToastHeight;
        Title = "剪贴板提醒";

        // ---- 标题行：标题 + 关闭（同一行，不重叠）----
        var title = new TextBlock
        {
            Text = isRemind ? "待办提醒" : "置顶已到期",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(accent)
        };

        var close = new Border
        {
            Padding = new Thickness(6, 0, 4, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = new TextBlock
            {
                Text = "关闭",
                FontSize = 11,
                Opacity = 0.55,
                Foreground = ClipboardPanel.ThemeText
            }
        };

        close.MouseEnter += (_, _) => close.Opacity = 0.8;
        close.MouseLeave += (_, _) => close.Opacity = 1;
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            CloseToast();
        };

        var titleRow = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(close, Dock.Right);
        titleRow.Children.Add(close);
        titleRow.Children.Add(title);

        var body = new TextBlock
        {
            Text = due.Preview,
            FontSize = 13,
            Margin = new Thickness(0, 5, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 40,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ClipboardPanel.ThemeText
        };

        var hint = new TextBlock
        {
            Text = "点击打开面板并定位该条目 · 记录可在面板底部“通知”里回看",
            FontSize = 10.5,
            Opacity = 0.5,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = ClipboardPanel.ThemeText
        };

        var text = new StackPanel { Margin = new Thickness(11, 8, 10, 8) };
        text.Children.Add(titleRow);
        text.Children.Add(body);
        text.Children.Add(hint);

        var bar = new Border
        {
            Width = 4,
            Background = new SolidColorBrush(accent)
        };

        var layout = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(bar, Dock.Left);
        layout.Children.Add(bar);
        layout.Children.Add(text);

        var surface = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = ClipboardPanel.ThemeSurface,
            BorderBrush = ClipboardPanel.ThemeBorder,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = layout
        };

        surface.MouseLeftButtonUp += (_, _) =>
        {
            CloseToast();
            ClipboardPanel.OpenTo(due.EntryId);
        };

        Content = surface;

        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _closeTimer.Tick += (_, _) => CloseToast();
        _closeTimer.Start();

        Closed += (_, _) =>
        {
            Active.Remove(this);
            Relayout();
        };
    }

    /// <summary>弹出一条通知（含提示音设置与通知记录）。必须在 UI 线程调用。</summary>
    internal static void Show(ClipboardDueEvent due)
    {
        ClipboardNotificationLog.Add(due);

        if (ClipboardSettings.Current.ReminderSound)
        {
            ClipboardSounds.PlayReminder();
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

    /// <summary>关闭本条通知。不能命名为 Close —— 会遮蔽 Window.Close 造成无限递归。</summary>
    private void CloseToast()
    {
        _closeTimer.Stop();
        Close();
    }
}
