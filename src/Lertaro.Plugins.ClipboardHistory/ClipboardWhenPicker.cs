using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Lertaro.Plugins.ClipboardHistory;

/// <summary>
/// 置顶时限 / 提醒时间的选择弹窗。
///
/// 设计要点：
///   1. 不用菜单的三级子菜单 —— WPF 子菜单在鼠标离开二级项时自动收起，点不到；
///      自绘 Popup（StaysOpen=true，不捕获鼠标）允许鼠标自由移入并停留。
///   2. **可视化选择**：月历点日期 + 时/分两列点选，不需要用户手输文本
///      （用户明确反馈"不该让用户自己手动输入，还要自己确认有没有输对"）。
///   3. 快捷项（相对时间）点击立即生效，覆盖高频场景。
/// </summary>
internal static class ClipboardWhenPicker
{
    private static Popup? _popup;
    private static Action<DateTime?>? _onPick;

    internal static bool IsOpen => _popup is { IsOpen: true };

    /// <summary>打开选择器。<paramref name="onPick"/> 收到 null 表示"清除"。</summary>
    internal static void Open(
        FrameworkElement anchor,
        string title,
        string clearLabel,
        DateTime? current,
        Action<DateTime?> onPick,
        IEnumerable<(string Label, Func<DateTime?> Value)>? extraOptions = null)
    {
        Close();
        _onPick = onPick;

        var today = DateTime.Today;
        var selectedDate = current?.Date ?? today;
        var hour = current?.Hour ?? 9;
        var minute = current?.Minute ?? 0;
        minute = minute / 5 * 5; // 分钟按 5 分钟对齐

        var root = new StackPanel { MinWidth = 272 };

        // 时/分两列的容器：必须最早声明 —— 它们会被后面定义的局部函数与早期 lambda 捕获，
        // 声明太晚会触发 CS0165（在赋值前被闭包引用）
        var hourColumn = new StackPanel();
        var minuteColumn = new StackPanel();

        root.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Opacity = 0.6,
            Margin = new Thickness(10, 4, 10, 4),
            Foreground = ClipboardPanel.ThemeText
        });

        // ---- 快捷项：点击立即生效（相对时间在点击那一刻计算）----
        // 快捷项排两列，压缩高度（原来一列 12 条会把按钮挤出屏幕）
        var quickGrid = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(4, 0, 4, 2)
        };

        if (extraOptions is not null)
        {
            foreach (var (label, factory) in extraOptions)
            {
                quickGrid.Children.Add(QuickChip(label, () => Commit(factory())));
            }
        }

        foreach (var (label, factory) in QuickOptions())
        {
            quickGrid.Children.Add(QuickChip(label, () => Commit(factory())));
        }

        root.Children.Add(quickGrid);
        root.Children.Add(Separator());

        // ---- 选定结果预览 ----
        var preview = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(10, 2, 10, 4),
            TextWrapping = TextWrapping.Wrap,
            Foreground = ClipboardPanel.ThemeAccent,
            Text = Describe(selectedDate, hour, minute)
        };

        void UpdatePreview()
        {
            var at = selectedDate.Date.AddHours(hour).AddMinutes(minute);
            preview.Text = at <= DateTime.Now
                ? "不能早于当前时间（已选 " + at.ToString("MM-dd HH:mm") + "）"
                : Describe(selectedDate, hour, minute);
        }

        // ---- 月历 ----
        var monthCursor = new DateTime(selectedDate.Year, selectedDate.Month, 1);
        var monthLabel = new TextBlock
        {
            Text = string.Empty,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
            Foreground = ClipboardPanel.ThemeText
        };

        var dayGrid = new Grid { Margin = new Thickness(6, 2, 6, 4) };
        for (var c = 0; c < 7; c++)
        {
            dayGrid.ColumnDefinitions.Add(new ColumnDefinition());
        }

        void RebuildCalendar()
        {
            dayGrid.Children.Clear();
            dayGrid.RowDefinitions.Clear();
            monthLabel.Text = monthCursor.ToString("yyyy 年 M 月");

            var week = new[] { "一", "二", "三", "四", "五", "六", "日" };
            dayGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var i = 0; i < 7; i++)
            {
                var head = new TextBlock
                {
                    Text = week[i],
                    FontSize = 10.5,
                    Opacity = 0.5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = ClipboardPanel.ThemeText
                };
                Grid.SetRow(head, 0);
                Grid.SetColumn(head, i);
                dayGrid.Children.Add(head);
            }

            var offset = ((int)monthCursor.DayOfWeek + 6) % 7; // 周一为一周之首
            var daysInMonth = DateTime.DaysInMonth(monthCursor.Year, monthCursor.Month);

            for (var d = 1; d <= daysInMonth; d++)
            {
                var date = new DateTime(monthCursor.Year, monthCursor.Month, d);
                var cellRow = 1 + ((offset + d - 1) / 7);
                var cellCol = (offset + d - 1) % 7;

                while (dayGrid.RowDefinitions.Count <= cellRow)
                {
                    dayGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                }

                var isSelected = date == selectedDate;
                var isPast = date < today;

                // 提醒/置顶不接受过去的时间：过去日期置灰且不可点（"不限跨度"指未来不设上限）
                var cell = new Border
                {
                    Width = 32,
                    Height = 24,
                    CornerRadius = new CornerRadius(6),
                    Background = isSelected ? ClipboardPanel.ThemeSelected : Brushes.Transparent,
                    Cursor = isPast ? Cursors.Arrow : Cursors.Hand,
                    Child = new TextBlock
                    {
                        Text = d.ToString(),
                        FontSize = 11.5,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Opacity = isPast ? 0.3 : 1,
                        Foreground = date == today && !isSelected
                            ? ClipboardPanel.ThemeAccent
                            : ClipboardPanel.ThemeText
                    }
                };

                if (!isPast)
                {
                    cell.MouseEnter += (_, _) =>
                    {
                        if (date != selectedDate)
                        {
                            cell.Background = ClipboardPanel.ThemeHover;
                        }
                    };
                    cell.MouseLeave += (_, _) =>
                    {
                        if (date != selectedDate)
                        {
                            cell.Background = Brushes.Transparent;
                        }
                    };
                    cell.MouseLeftButtonUp += (_, e) =>
                    {
                        e.Handled = true;
                        selectedDate = date;
                        UpdatePreview();
                        RebuildCalendar();
                        RefreshTimeSelection();
                    };
                }

                Grid.SetRow(cell, cellRow);
                Grid.SetColumn(cell, cellCol);
                dayGrid.Children.Add(cell);
            }
        }

        var monthNav = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(6, 2, 6, 0)
        };

        monthNav.Children.Add(NavButton("«", () =>
        {
            monthCursor = monthCursor.AddYears(-1);
            RebuildCalendar();
        }));
        monthNav.Children.Add(NavButton("‹", () =>
        {
            monthCursor = monthCursor.AddMonths(-1);
            RebuildCalendar();
        }));
        monthNav.Children.Add(monthLabel);
        monthNav.Children.Add(NavButton("›", () =>
        {
            monthCursor = monthCursor.AddMonths(1);
            RebuildCalendar();
        }));
        monthNav.Children.Add(NavButton("»", () =>
        {
            monthCursor = monthCursor.AddYears(1);
            RebuildCalendar();
        }));

        root.Children.Add(monthNav);
        root.Children.Add(dayGrid);

        // ---- 时 / 分 两列点选（0-59 全量；今天已过去的时段禁用）----
        void RefreshTimeSelection()
        {
            hourColumn.Children.Clear();
            minuteColumn.Children.Clear();

            var now = DateTime.Now;
            var isToday = selectedDate.Date == now.Date;

            for (var h = 0; h < 24; h++)
            {
                var captured = h;
                var enabled = !isToday || captured > now.Hour;
                hourColumn.Children.Add(TimeCell(
                    captured.ToString("00"),
                    captured == hour,
                    enabled,
                    () =>
                    {
                        hour = captured;
                        UpdatePreview();
                        RefreshTimeSelection();
                    }));
            }

            for (var m = 0; m < 60; m++)
            {
                var captured = m;
                var enabled = !isToday
                    || hour > now.Hour
                    || (hour == now.Hour && captured > now.Minute);

                minuteColumn.Children.Add(TimeCell(
                    captured.ToString("00"),
                    captured == minute,
                    enabled,
                    () =>
                    {
                        minute = captured;
                        UpdatePreview();
                        RefreshTimeSelection();
                    }));
            }
        }

        var timeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(6, 6, 6, 2)
        };

        timeRow.Children.Add(ColumnCaption("时"));
        timeRow.Children.Add(new ScrollViewer
        {
            Height = 128,
            Width = 46,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = hourColumn
        });
        timeRow.Children.Add(ColumnCaption("分"));
        timeRow.Children.Add(new ScrollViewer
        {
            Height = 128,
            Width = 46,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = minuteColumn
        });

        root.Children.Add(timeRow);

        RebuildCalendar();
        RefreshTimeSelection();

        // ---- 底部固定区：预览 + 按钮（不随内容滚动，永远点得到）----
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(6, 2, 6, 2)
        };

        buttonRow.Children.Add(PickerButton(clearLabel, () => Commit(null), primary: false));
        buttonRow.Children.Add(PickerButton("确定", () =>
        {
            var at = selectedDate.Date.AddHours(hour).AddMinutes(minute);
            if (at <= DateTime.Now)
            {
                preview.Text = "不能早于当前时间，请重新选择";
                return;
            }

            Commit(at);
        }, primary: true));

        var footer = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        footer.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(8, 0, 8, 4),
            Background = ClipboardPanel.ThemeBorder
        });
        footer.Children.Add(preview);
        footer.Children.Add(buttonRow);

        // 工作区高度兜底：弹窗绝不超出屏幕，超出部分交给中部滚动
        var maxHeight = Math.Max(360, SystemParameters.WorkArea.Height - 120);

        var host = new DockPanel { LastChildFill = true, MaxHeight = maxHeight };
        DockPanel.SetDock(footer, Dock.Bottom);
        host.Children.Add(footer);
        host.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root
        });

        var surface = new Border
        {
            Background = ClipboardPanel.ThemeSurface,
            BorderBrush = ClipboardPanel.ThemeBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(5),
            Child = host
        };

        _popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Right,
            HorizontalOffset = 6,
            StaysOpen = true, // 不捕获鼠标：鼠标可自由移入
            AllowsTransparency = true,
            Child = surface
        };

        _popup.IsOpen = true;
    }

    /// <summary>
    /// 快捷项来自设置（可自由配置）。点击那一刻才解析 —— 相对时间（"5 分钟后"）
    /// 因此永远相对当下计算，不会因为弹窗开着不动而产生偏移。
    /// 无法解析的条目只记日志、不显示，避免设置里写错就整块消失。
    /// </summary>
    private static (string Label, Func<DateTime?> Value)[] QuickOptions()
    {
        var list = new List<(string Label, Func<DateTime?> Value)>();
        foreach (var text in ClipboardSettings.Current.WhenQuickOptions)
        {
            var captured = text;
            list.Add((captured, () => Parse(captured)));
        }

        return [.. list];
    }

    private static string Describe(DateTime date, int hour, int minute)
    {
        var at = date.Date.AddHours(hour).AddMinutes(minute);
        var dayWord = at.Date == DateTime.Today
            ? "今天"
            : at.Date == DateTime.Today.AddDays(1)
                ? "明天"
                : at.Date == DateTime.Today.AddDays(-1)
                    ? "昨天"
                    : at.ToString("yyyy-MM-dd");

        return "已选：" + dayWord + " " + at.ToString("HH:mm") + "（" + at.ToString("dddd", new System.Globalization.CultureInfo("zh-CN")) + "）";
    }

    private static UIElement Separator() => new Border
    {
        Height = 1,
        Margin = new Thickness(8, 6, 8, 4),
        Background = ClipboardPanel.ThemeBorder
    };

    private static UIElement ColumnCaption(string text) => new TextBlock
    {
        Text = text,
        FontSize = 11,
        Opacity = 0.6,
        Margin = new Thickness(0, 0, 4, 0),
        VerticalAlignment = VerticalAlignment.Top,
        Foreground = ClipboardPanel.ThemeText
    };

    private static UIElement NavButton(string glyph, Action onClick)
    {
        var button = new Border
        {
            Padding = new Thickness(8, 1, 8, 1),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = glyph,
                FontSize = 14,
                Foreground = ClipboardPanel.ThemeText
            }
        };

        button.MouseEnter += (_, _) => button.Background = ClipboardPanel.ThemeHover;
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };

        return button;
    }

    /// <summary>快捷项 chip（两列排布，压缩弹窗高度）。</summary>
    private static UIElement QuickChip(string label, Action onClick)
    {
        var chip = new Border
        {
            Width = 118,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(2, 2, 2, 2),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = label,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ClipboardPanel.ThemeText
            }
        };

        chip.MouseEnter += (_, _) => chip.Background = ClipboardPanel.ThemeHover;
        chip.MouseLeave += (_, _) => chip.Background = Brushes.Transparent;
        chip.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };

        return chip;
    }

    private static UIElement TimeCell(string label, bool selected, bool enabled, Action onClick)
    {
        var cell = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(0, 3, 0, 3),
            Margin = new Thickness(1),
            Background = selected ? ClipboardPanel.ThemeSelected : Brushes.Transparent,
            Cursor = enabled ? Cursors.Hand : Cursors.Arrow,
            Child = new TextBlock
            {
                Text = label,
                FontSize = 11.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                Opacity = enabled ? 1 : 0.28,
                Foreground = ClipboardPanel.ThemeText,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal
            }
        };

        if (enabled)
        {
            cell.MouseEnter += (_, _) =>
            {
                if (!selected)
                {
                    cell.Background = ClipboardPanel.ThemeHover;
                }
            };
            cell.MouseLeave += (_, _) =>
            {
                if (!selected)
                {
                    cell.Background = Brushes.Transparent;
                }
            };
            cell.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                onClick();
            };
        }

        return cell;
    }

    private static UIElement PickerButton(string label, Action onClick, bool primary)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = primary ? ClipboardPanel.ThemeAccent : ClipboardPanel.ThemeText,
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal
        };

        var button = new Border
        {
            Child = text,
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(6, 0, 0, 0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = primary ? ClipboardPanel.ThemeAccent : ClipboardPanel.ThemeBorder,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand
        };

        button.MouseEnter += (_, _) => button.Background = ClipboardPanel.ThemeHover;
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };

        return button;
    }

    private static UIElement Row(string label, Action onClick)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(2, 1, 2, 1),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = label,
                FontSize = 12.5,
                Foreground = ClipboardPanel.ThemeText
            }
        };

        border.MouseEnter += (_, _) => border.Background = ClipboardPanel.ThemeHover;
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        border.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };

        return border;
    }

    private static void Commit(DateTime? value)
    {
        var callback = _onPick;
        Close();
        callback?.Invoke(value);
    }

    /// <summary>
    /// 解析快捷项文本（设置里可写）：空 = 清除；14:30（已过顺延明天）/
    /// 明天·后天 [+HH:mm] / N 分钟后·小时后·天后 / yyyy-MM-dd HH:mm /
    /// M-d HH:mm / M月d日 HH:mm（无年份且已过 → 明年）。
    /// </summary>
    internal static DateTime? Parse(string text)
    {
        var value = text.Trim();
        if (value.Length == 0)
        {
            return null;
        }

        var relative = Regex.Match(value, @"^(\d+)\s*(分钟?|小时?|天)\s*(后|之后|以后)(此时)?$");
        if (relative.Success)
        {
            var amount = int.Parse(relative.Groups[1].Value, CultureInfo.InvariantCulture);
            var unit = relative.Groups[2].Value;
            if (unit.StartsWith('分'))
            {
                return DateTime.Now.AddMinutes(amount);
            }

            return unit.StartsWith('小') ? DateTime.Now.AddHours(amount) : DateTime.Now.AddDays(amount);
        }

        var dayWord = Regex.Match(value, @"^(今天|明天|后天)\s*(\d{1,2})[:：](\d{2})$");
        if (dayWord.Success)
        {
            var day = dayWord.Groups[1].Value switch
            {
                "明天" => DateTime.Today.AddDays(1),
                "后天" => DateTime.Today.AddDays(2),
                _ => DateTime.Today
            };

            return day
                .AddHours(int.Parse(dayWord.Groups[2].Value, CultureInfo.InvariantCulture))
                .AddMinutes(int.Parse(dayWord.Groups[3].Value, CultureInfo.InvariantCulture));
        }

        if (value is "明天" or "后天" or "今天")
        {
            return value switch
            {
                "明天" => DateTime.Today.AddDays(1).AddHours(9),
                "后天" => DateTime.Today.AddDays(2).AddHours(9),
                _ => DateTime.Now.AddHours(1)
            };
        }

        var timeOnly = Regex.Match(value, @"^(\d{1,2})[:：](\d{2})$");
        if (timeOnly.Success)
        {
            var at = DateTime.Today
                .AddHours(int.Parse(timeOnly.Groups[1].Value, CultureInfo.InvariantCulture))
                .AddMinutes(int.Parse(timeOnly.Groups[2].Value, CultureInfo.InvariantCulture));
            return at <= DateTime.Now ? at.AddDays(1) : at;
        }

        var patterns = new[]
        {
            @"^(\d{4})[-/](\d{1,2})[-/](\d{1,2})\s+(\d{1,2})[:：](\d{2})$",
            @"^(\d{1,2})[-/](\d{1,2})\s+(\d{1,2})[:：](\d{2})$",
            @"^(\d{1,2})月(\d{1,2})日\s*(\d{1,2})[:：](\d{2})$"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(value, pattern);
            if (!match.Success)
            {
                continue;
            }

            try
            {
                var groups = match.Groups;
                int year;
                int month;
                int day;
                int hour;
                int minute;
                if (groups.Count == 6)
                {
                    year = int.Parse(groups[1].Value, CultureInfo.InvariantCulture);
                    month = int.Parse(groups[2].Value, CultureInfo.InvariantCulture);
                    day = int.Parse(groups[3].Value, CultureInfo.InvariantCulture);
                    hour = int.Parse(groups[4].Value, CultureInfo.InvariantCulture);
                    minute = int.Parse(groups[5].Value, CultureInfo.InvariantCulture);
                }
                else
                {
                    year = DateTime.Today.Year;
                    month = int.Parse(groups[1].Value, CultureInfo.InvariantCulture);
                    day = int.Parse(groups[2].Value, CultureInfo.InvariantCulture);
                    hour = int.Parse(groups[3].Value, CultureInfo.InvariantCulture);
                    minute = int.Parse(groups[4].Value, CultureInfo.InvariantCulture);
                }

                var at = new DateTime(year, month, day, hour, minute, 0);
                if (at <= DateTime.Now && groups.Count == 5)
                {
                    at = at.AddYears(1);
                }

                return at;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }

    internal static void Close()
    {
        if (_popup is not null)
        {
            _popup.IsOpen = false;
        }

        _popup = null;
        _onPick = null;
    }
}
