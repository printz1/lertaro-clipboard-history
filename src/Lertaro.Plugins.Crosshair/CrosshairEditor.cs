using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lertaro.PluginSdk.Windows;

namespace Lertaro.Plugins.Crosshair;

/// <summary>
/// 准星编辑器：界面与参数 1:1 对齐 okiaimx.com 的准星设置面板 ——
/// 左栏「颜色 / 描边 / 中心点 + 预览」，右栏「内部线条 / 外部线条」，
/// 底部「代码（VALORANT 兼容）」可粘贴代码应用、也可复制出去。
/// 宿主的通用设置面板放不了自定义控件，所以体验在这里做；
/// 通用面板只保留等价的文本字段作为后备。
///
/// 窗口壳用 SDK 的 <see cref="PluginWindow"/>（对照开发指南）：主题、DPI、任务栏与
/// Alt+Tab 都由宿主统一处理，插件不再自己造裸窗口。
/// </summary>
internal sealed class CrosshairEditor
{
    private PluginWindow? _window;

    private const double PreviewSize = 180;

    private readonly CrosshairSettings _draft;
    private readonly Canvas _preview = new();
    private readonly List<Action> _syncers = [];
    private bool _syncing;

    private ComboBox? _colorBox;
    private StackPanel? _hexRow;
    private TextBox? _hexBox;
    private TextBox? _codeBox;
    private TextBlock? _codeStatus;
    private CheckBox? _visibleCheck;
    private TextBox? _offsetXBox;
    private TextBox? _offsetYBox;
    private TextBox? _hotkeyBox;

    private static readonly string[] ColorNames =
    [
        "白色", "绿色", "黄绿色", "绿黄色", "黄色", "青色", "粉色", "红色", "自定义"
    ];

    internal CrosshairEditor()
    {
        _draft = CrosshairSettings.Current.Clone();
        SyncControls();
        RenderPreview();
    }

    /// <summary>
    /// 用宿主主题化的 <see cref="PluginWindow"/> 打开编辑器（Dialog 模式：置顶且不进 Alt+Tab）。
    /// 复用一个实例即可，关闭后引用置空。
    /// </summary>
    internal void Show()
    {
        var body = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = BuildBody()
        };

        _window = new PluginWindow("准星编辑器（okiaimx / VALORANT 参数）", 840, 880, PluginWindowMode.Dialog)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            MinWidth = 700,
            MinHeight = 560
        };

        _window.ContentHostControl.Content = body;
        _window.ShowDialog();
        _window = null;
    }

    private UIElement BuildBody()
    {
        var root = new StackPanel { Margin = new Thickness(16) };

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel();
        var right = new StackPanel();
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 2);
        columns.Children.Add(left);
        columns.Children.Add(right);
        root.Children.Add(columns);

        BuildLeftColumn(left);
        BuildRightColumn(right);

        root.Children.Add(Divider());
        BuildCodeSection(root);
        root.Children.Add(Divider());
        BuildExtensions(root);
        BuildFooter(root);

        return root;
    }

    // ---- 左栏：预览 / 颜色 / 描边 / 中心点 ----

    private void BuildLeftColumn(StackPanel panel)
    {
        panel.Children.Add(Caption("预览"));
        var host = new Border
        {
            Height = PreviewSize,
            Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 4, 0, 2),
            ClipToBounds = true,
            Child = _preview
        };
        panel.Children.Add(host);
        panel.Children.Add(new TextBlock
        {
            Text = $"预览按 okiaimx 的 {CrosshairRenderer.PreviewScale:0.0} 倍绘制；游戏内 = 屏幕宽度 ÷ {CrosshairRenderer.ReferenceWidth:0}",
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            Foreground = SystemColors.ControlTextBrush,
            Margin = new Thickness(0, 0, 0, 4)
        });

        panel.Children.Add(Divider());
        panel.Children.Add(Caption("颜色"));

        _colorBox = new ComboBox
        {
            Margin = new Thickness(0, 4, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        foreach (var name in ColorNames)
        {
            _colorBox.Items.Add(name);
        }

        _colorBox.SelectionChanged += (_, _) =>
        {
            if (_syncing || _colorBox.SelectedIndex < 0)
            {
                return;
            }

            var index = _colorBox.SelectedIndex;
            _draft.Primary.Color = index;
            _draft.Primary.HexColor.Enabled = index == CrosshairCodec.CustomColorIndex;
            UpdateHexRowVisibility();
            RenderPreview();
        };
        panel.Children.Add(_colorBox);

        _hexRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 6)
        };
        _hexRow.Children.Add(new TextBlock
        {
            Text = "自定义颜色",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Foreground = SystemColors.ControlTextBrush
        });
        _hexBox = new TextBox { Width = 96, VerticalContentAlignment = VerticalAlignment.Center };
        _hexBox.TextChanged += (_, _) =>
        {
            if (_syncing)
            {
                return;
            }

            var hex8 = CrosshairCodec.Hex8(_hexBox.Text);
            if (hex8 is null)
            {
                return; // 输入过程中不合法就等下一次，不打断打字
            }

            _draft.Primary.HexColor.Value = hex8;
            _draft.Primary.HexColor.Enabled = true;
            RenderPreview();
        };
        _hexRow.Children.Add(_hexBox);
        _hexRow.Children.Add(new TextBlock
        {
            Text = "  如 #00FF88",
            FontSize = 11,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = SystemColors.ControlTextBrush
        });
        panel.Children.Add(_hexRow);

        panel.Children.Add(Divider());
        panel.Children.Add(Caption("描边"));
        AddCheck(
            panel,
            "显示",
            () => _draft.Primary.Outlines.Enabled,
            v => _draft.Primary.Outlines.Enabled = v);
        AddSlider(
            panel,
            "粗细",
            CrosshairLimits.OutlineWidthMin,
            CrosshairLimits.OutlineWidthMax,
            1,
            () => _draft.Primary.Outlines.Width,
            v => _draft.Primary.Outlines.Width = v);
        AddSlider(
            panel,
            "不透明度",
            CrosshairLimits.AlphaMin,
            CrosshairLimits.AlphaMax,
            0.05,
            () => _draft.Primary.Outlines.Alpha,
            v => _draft.Primary.Outlines.Alpha = v);

        panel.Children.Add(Divider());
        panel.Children.Add(Caption("中心点"));
        AddCheck(
            panel,
            "显示",
            () => _draft.Primary.Dot.Enabled,
            v => _draft.Primary.Dot.Enabled = v);
        AddSlider(
            panel,
            "大小",
            CrosshairLimits.DotWidthMin,
            CrosshairLimits.DotWidthMax,
            1,
            () => _draft.Primary.Dot.Width,
            v => _draft.Primary.Dot.Width = v);
        AddSlider(
            panel,
            "不透明度",
            CrosshairLimits.AlphaMin,
            CrosshairLimits.AlphaMax,
            0.05,
            () => _draft.Primary.Dot.Alpha,
            v => _draft.Primary.Dot.Alpha = v);
    }

    // ---- 右栏：内部线条 / 外部线条 ----

    private void BuildRightColumn(StackPanel panel)
    {
        panel.Children.Add(Caption("内部线条"));
        AddLinesSection(
            panel,
            _draft.Primary.Inner,
            lengthMax: CrosshairLimits.InnerLengthMax,
            offsetMax: CrosshairLimits.InnerOffsetMax);

        panel.Children.Add(Divider());
        panel.Children.Add(Caption("外部线条"));
        AddLinesSection(
            panel,
            _draft.Primary.Outer,
            lengthMax: CrosshairLimits.OuterLengthMax,
            offsetMax: CrosshairLimits.OuterOffsetMax);
    }

    private void AddLinesSection(StackPanel panel, CrosshairLines lines, double lengthMax, double offsetMax)
    {
        AddCheck(panel, "显示", () => lines.Enabled, v => lines.Enabled = v);
        AddSlider(panel, "粗细", CrosshairLimits.LineWidthMin, CrosshairLimits.LineWidthMax, 1,
            () => lines.Width, v => lines.Width = v);
        AddSlider(panel, "长度", 0, lengthMax, 1, () => lines.Length, v => lines.Length = v);
        AddSlider(panel, "偏移", 0, offsetMax, 1, () => lines.Offset, v => lines.Offset = v);
        AddSlider(panel, "不透明度", CrosshairLimits.AlphaMin, CrosshairLimits.AlphaMax, 0.05,
            () => lines.Alpha, v => lines.Alpha = v);
        AddCheck(panel, "单独设置竖线", () => lines.Vertical.Enabled, v => lines.Vertical.Enabled = v);
        AddSlider(panel, "竖线长度", 0, CrosshairLimits.VerticalLengthMax, 1,
            () => lines.Vertical.Length, v => lines.Vertical.Length = v);
    }

    // ---- 代码（VALORANT 兼容）----

    private void BuildCodeSection(StackPanel panel)
    {
        panel.Children.Add(Caption("代码（VALORANT 兼容）"));

        var row = new Grid { Margin = new Thickness(0, 4, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _codeBox = new TextBox
        {
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        Grid.SetColumn(_codeBox, 0);
        row.Children.Add(_codeBox);

        var apply = new Button { Content = "应用", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 0, 6, 0) };
        apply.Click += (_, _) => ApplyCodeInput();
        Grid.SetColumn(apply, 1);
        row.Children.Add(apply);

        var copy = new Button { Content = "复制", Padding = new Thickness(16, 4, 16, 4) };
        copy.Click += (_, _) => CopyCode();
        Grid.SetColumn(copy, 2);
        row.Children.Add(copy);

        panel.Children.Add(row);

        _codeStatus = new TextBlock
        {
            FontSize = 11.5,
            Opacity = 0.75,
            Margin = new Thickness(0, 0, 0, 4),
            Foreground = SystemColors.ControlTextBrush
        };
        panel.Children.Add(_codeStatus);

        var reset = new Button
        {
            Content = "恢复默认",
            Padding = new Thickness(16, 4, 16, 4),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        reset.Click += (_, _) =>
        {
            _draft.ResetCrosshair();
            SyncControls();
            RenderPreview();
            SetCodeStatus("已恢复默认");
        };
        panel.Children.Add(reset);
    }

    private void ApplyCodeInput()
    {
        var code = _codeBox?.Text ?? string.Empty;
        if (!CrosshairSettings.IsPlausibleCode(code))
        {
            SetCodeStatus("代码不正确");
            return;
        }

        CrosshairCodec.Import(code, _draft);
        SyncControls();
        RenderPreview();
        SetCodeStatus("已应用");
    }

    private void CopyCode()
    {
        try
        {
            Clipboard.SetText(CrosshairCodec.Export(_draft));
            SetCodeStatus("已复制");
        }
        catch
        {
            SetCodeStatus("复制失败");
        }
    }

    private void SetCodeStatus(string text)
    {
        if (_codeStatus is not null)
        {
            _codeStatus.Text = text;
        }
    }

    // ---- 扩展项（okiais 没有，本插件独有）----

    private void BuildExtensions(StackPanel panel)
    {
        panel.Children.Add(Caption("扩展（本插件独有，不影响 VALORANT 代码）"));

        _visibleCheck = new CheckBox
        {
            Content = "显示准星（总开关）",
            Margin = new Thickness(0, 4, 0, 4)
        };
        _visibleCheck.Checked += (_, _) => _draft.Visible = true;
        _visibleCheck.Unchecked += (_, _) => _draft.Visible = false;
        panel.Children.Add(_visibleCheck);

        var offsetRow = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
        offsetRow.Children.Add(new TextBlock
        {
            Text = "位置偏移 X ",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = SystemColors.ControlTextBrush
        });
        _offsetXBox = new TextBox { Width = 56, VerticalContentAlignment = VerticalAlignment.Center };
        offsetRow.Children.Add(_offsetXBox);
        offsetRow.Children.Add(new TextBlock
        {
            Text = "  Y ",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = SystemColors.ControlTextBrush
        });
        _offsetYBox = new TextBox { Width = 56, VerticalContentAlignment = VerticalAlignment.Center };
        offsetRow.Children.Add(_offsetYBox);
        offsetRow.Children.Add(new TextBlock
        {
            Text = "   （0 = 屏幕正中）",
            FontSize = 11,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = SystemColors.ControlTextBrush
        });
        panel.Children.Add(offsetRow);

        AddSlider(panel, "整体不透明度 %", 10, 100, 5, () => _draft.Opacity, v => _draft.Opacity = (int)v);

        var hotkeyRow = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
        hotkeyRow.Children.Add(new TextBlock
        {
            Text = "全局热键 ",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = SystemColors.ControlTextBrush
        });
        _hotkeyBox = new TextBox { Width = 140, VerticalContentAlignment = VerticalAlignment.Center };
        hotkeyRow.Children.Add(_hotkeyBox);
        hotkeyRow.Children.Add(new TextBlock
        {
            Text = "   如 Ctrl+Alt+C，留空不注册",
            FontSize = 11,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = SystemColors.ControlTextBrush
        });
        panel.Children.Add(hotkeyRow);
    }

    private void BuildFooter(StackPanel panel)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };

        var cancel = new Button { Content = "取消", Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => _window?.Close();
        row.Children.Add(cancel);

        var save = new Button { Content = "保存并应用", Padding = new Thickness(18, 6, 18, 6), FontSize = 13 };
        save.Click += (_, _) =>
        {
            _ = int.TryParse(_offsetXBox?.Text, out var ox);
            _ = int.TryParse(_offsetYBox?.Text, out var oy);
            _draft.OffsetX = Math.Clamp(ox, -4096, 4096);
            _draft.OffsetY = Math.Clamp(oy, -4096, 4096);
            _draft.Hotkey = _hotkeyBox?.Text?.Trim() ?? string.Empty;

            var current = CrosshairSettings.Current;
            current.ApplyFrom(_draft); // Clamp + Save + 清洗参数

            CrosshairPlugin.ApplySettingsLive();
            _window?.Close();
        };
        row.Children.Add(save);

        panel.Children.Add(row);
    }

    // ---- 控件工厂 ----

    private void AddCheck(StackPanel panel, string label, Func<bool> get, Action<bool> set)
    {
        var box = new CheckBox
        {
            Content = label,
            Margin = new Thickness(0, 4, 0, 2)
        };
        box.Checked += (_, _) =>
        {
            if (_syncing)
            {
                return;
            }

            set(true);
            RenderPreview();
        };
        box.Unchecked += (_, _) =>
        {
            if (_syncing)
            {
                return;
            }

            set(false);
            RenderPreview();
        };

        panel.Children.Add(box);
        _syncers.Add(() => box.IsChecked = get());
    }

    private void AddSlider(StackPanel panel, string label, double min, double max, double step, Func<double> get, Action<double> set)
    {
        var row = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };

        var head = new DockPanel { LastChildFill = false };
        var valueText = new TextBlock
        {
            Text = Format(get(), step),
            FontWeight = FontWeights.SemiBold,
            Foreground = SystemColors.ControlTextBrush
        };
        DockPanel.SetDock(valueText, Dock.Right);
        head.Children.Add(valueText);
        head.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = SystemColors.ControlTextBrush
        });
        row.Children.Add(head);

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = get(),
            TickFrequency = step,
            SmallChange = step,
            LargeChange = step * 4,
            IsSnapToTickEnabled = true,
            Margin = new Thickness(0, 2, 0, 4)
        };
        slider.ValueChanged += (_, _) =>
        {
            if (_syncing)
            {
                return;
            }

            set(slider.Value);
            valueText.Text = Format(slider.Value, step);
            RenderPreview();
        };
        row.Children.Add(slider);

        panel.Children.Add(row);

        _syncers.Add(() =>
        {
            var value = Math.Clamp(get(), min, max);
            slider.Value = value;
            valueText.Text = Format(value, step);
        });
    }

    /// <summary>整数步长显示整数，小数步长显示两位小数（与 okiais 一致）。</summary>
    private static string Format(double value, double step) =>
        Math.Abs(step - Math.Round(step)) < 0.000001
            ? Math.Round(value).ToString("0")
            : value.ToString("0.00");

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 6, 0, 2),
        Foreground = SystemColors.ControlTextBrush
    };

    private static Border Divider() => new()
    {
        Height = 1,
        Background = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)),
        Margin = new Thickness(0, 8, 0, 2)
    };

    // ---- 回填与预览 ----

    /// <summary>把 _draft 回填到所有控件（粘贴代码、恢复默认之后调用）。</summary>
    private void SyncControls()
    {
        _syncing = true;
        try
        {
            foreach (var sync in _syncers)
            {
                sync();
            }

            if (_colorBox is not null)
            {
                _colorBox.SelectedIndex = Math.Clamp(_draft.Primary.Color, 0, ColorNames.Length - 1);
            }

            if (_visibleCheck is not null)
            {
                _visibleCheck.IsChecked = _draft.Visible;
            }

            if (_offsetXBox is not null)
            {
                _offsetXBox.Text = _draft.OffsetX.ToString();
            }

            if (_offsetYBox is not null)
            {
                _offsetYBox.Text = _draft.OffsetY.ToString();
            }

            if (_hotkeyBox is not null)
            {
                _hotkeyBox.Text = _draft.Hotkey;
            }

            if (_hexBox is not null)
            {
                _hexBox.Text = CrosshairCodec.Hex6(_draft.Primary.HexColor.Value);
            }

            if (_codeBox is not null)
            {
                _codeBox.Text = CrosshairCodec.Export(_draft);
            }

            UpdateHexRowVisibility();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void UpdateHexRowVisibility()
    {
        if (_hexRow is not null)
        {
            _hexRow.Visibility = _draft.Primary.Color == CrosshairCodec.CustomColorIndex
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void RenderPreview()
    {
        _preview.Children.Clear();
        var center = PreviewSize / 2;
        CrosshairRenderer.Draw(_preview, center, center, _draft, CrosshairRenderer.PreviewScale);

        // 参数一动就同步代码框（正在输入代码时不打断）
        if (_codeBox is not null && !_syncing && !_codeBox.IsKeyboardFocusWithin)
        {
            _codeBox.Text = CrosshairCodec.Export(_draft);
        }
    }
}
