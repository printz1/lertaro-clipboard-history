using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lertaro.PluginSdk;
using Lertaro.PluginSdk.Abstractions.Plugins;
using Lertaro.PluginSdk.Services;
using Lertaro.PluginSdk.Windows;

namespace Lertaro.Plugins.ClipboardHistory;

/// <summary>
/// 剪贴板历史面板。设计参照市面主流工具（Ditto / CopyQ / ClipClip / Win+V）的共识做法：
///
/// 1. 瞬态优先：打开即用，不做成"需要打理的窗口"（ClipClip 就是因为把用户拽进管理界面而被批评）；
/// 2. 键盘唯一路径：打开后直接打字就过滤、方向键选、Enter 粘贴，全程不碰鼠标；
/// 3. Enter 之后把焦点还给用户原来所在的窗口 —— 少一次手动切换，这是"实用"的关键；
/// 4. 时间分组（今天/昨天/本周/更早）+ 相对时间（刚刚 / 5 分钟前），不用读时间戳；
/// 5. 类型筛选 chips 立在顶部，文件与图片接入后自动生效；
/// 6. 常驻快捷键提示，降低学习成本。
/// </summary>
internal static class ClipboardPanel
{
    private const int MaxRows = 150;

    /// <summary>输入去抖：快速打字时合并刷新，避免每次击键都重建整个列表。</summary>
    private const int RefreshDebounceMs = 90;

    private static PluginWindow? _window;
    private static ListBox? _list;
    private static TextBox? _search;
    private static TextBlock? _watermark;
    private static TextBlock? _hint;
    private static IntPtr _previousForeground;
    private static ClipType _typeFilter = ClipType.All;
    private static System.Windows.Threading.DispatcherTimer? _refreshTimer;
    private static bool _rebuilding;

    private static Brush _accentBrush = Brushes.Transparent;
    private static ListBoxItem? _highlighted;

    private static Popup? _flyout;
    private static ListBoxItem? _flyoutItem;
    private static System.Windows.Threading.DispatcherTimer? _flyoutCloseTimer;
    private static System.Windows.Threading.DispatcherTimer? _flyoutOpenTimer;
    private static ListBoxItem? _openPendingItem;
    private static ClipboardIndexEntry? _openPendingEntry;
    private static ClipboardStore? _openPendingStore;
    private static NativeMethods.POINT _cursorAtEnter;

    private static ComboBox? _sourceCombo;
    private static string? _sourceFilter;
    private static string _sourceSignature = string.Empty;
    private static bool _suppressComboEvents;
    private static Button? _closeButton;

    private static DateTime? _timeFilterFrom;
    private static DateTime? _timeFilterTo;
    private static string? _timeFilterLabel;
    private static readonly List<TimelineRow> _timelineRows = [];
    private static int _timelineHighlight = -1;

    /// <summary>时间线里的一行：既用于渲染，也用于键盘导航（↑↓ + Enter）。</summary>
    private sealed record TimelineRow(Border Row, DateTime? From, DateTime? To, string Label);

    private static Popup? _timelinePopup;
    private static ListBoxItem? _timelineHeader;
    private static Brush? _themeSurface;
    private static Brush? _themeBorderBrush;
    private static Brush? _themeText;
    private static Brush? _themeHover;
    private static Brush? _themeSelected;
    private const string TimeHeaderTag = "cb-time-header";
    private static bool _copyBusy;

    /// <summary>缩略图缓存：id → 解码好的 44px 位图（Freeze 过，跨线程安全）。</summary>
    private static readonly Dictionary<long, BitmapSource> Thumbs = [];

    /// <summary>并发 2 路解码：避免打开面板瞬间 150 张图同时挤进 WIC。</summary>
    private static readonly SemaphoreSlim ThumbGate = new(2, 2);

    /// <summary>上一次渲染的结果签名：内容没变就不重建视觉树。</summary>
    private static string _lastSignature = string.Empty;

    private static readonly List<Row> Rows = [];

    private enum ClipType
    {
        All,
        Text,
        File,
        Image
    }

    private sealed class Row
    {
        internal ClipboardIndexEntry Entry = null!;
        internal ListBoxItem Container = null!;
        internal Border Accent = null!;
        internal Image? Thumbnail;
    }

    /// <summary>打开面板；已经打开则关闭（Win+V 式开关）。</summary>
    internal static void Toggle()
    {
        try
        {
            var app = Application.Current;
            if (app is null)
            {
                ClipboardListener.Log("no WPF Application available, falling back to search window", LogLevel.Warn);
                FallBackToSearchWindow();
                return;
            }

            if (!app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(new Action(Toggle));
                return;
            }

            if (_window is not null)
            {
                _window.Close();
                return;
            }

            ShowPanel();
        }
        catch (Exception ex)
        {
            ClipboardListener.Log("panel toggle failed: " + ex, LogLevel.Error);
        }
    }

    private static void FallBackToSearchWindow()
    {
        var keywords = ClipboardSettings.Current.TriggerKeywords;
        var keyword = keywords.Count > 0 ? keywords[0] : "cb";

        SearchWindowService.ShowWindow(keyword);

        try
        {
            SearchQueryService.ChangeQuery(keyword);
        }
        catch (Exception ex)
        {
            ClipboardListener.Log("could not move caret to end of query: " + ex.Message, LogLevel.Debug);
        }

        SearchWindowService.FocusQueryTextBox();
    }

    private static void ShowPanel()
    {
        var store = ClipboardHistoryPlugin.Store;
        if (store is null)
        {
            return;
        }

        // 记住用户按热键时所在的窗口，复制完把焦点还回去
        try
        {
            _previousForeground = NativeMethods.GetForegroundWindow();
        }
        catch
        {
            _previousForeground = IntPtr.Zero;
        }

        var isDark = IsDarkTheme();
        var accent = isDark
            ? Color.FromRgb(0x5D, 0xCA, 0xA5)
            : Color.FromRgb(0x0F, 0x6E, 0x56);

        _accentBrush = new SolidColorBrush(accent);
        _accentBrush.Freeze();
        _highlighted = null;
        _lastSignature = string.Empty;
        _sourceFilter = null;
        _sourceSignature = string.Empty;

        var root = new DockPanel { LastChildFill = true };

        // ---- 顶部：搜索框 + 来源下拉 + 类型筛选 ----
        var header = new StackPanel { Margin = new Thickness(12, 12, 12, 6) };

        var searchRow = new Grid();
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var searchHost = new Grid();
        _search = new TextBox
        {
            FontSize = 14,
            Padding = new Thickness(8, 6, 8, 6),
            BorderThickness = new Thickness(1)
        };

        _watermark = new TextBlock
        {
            Text = "直接输入即过滤，↑↓ 选择，Enter 粘贴",
            FontSize = 14,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.45,
            IsHitTestVisible = false
        };

        _search.TextChanged += (_, _) =>
        {
            UpdateWatermark();
            ScheduleRefresh(store);
        };

        searchHost.Children.Add(_search);
        searchHost.Children.Add(_watermark);
        Grid.SetColumn(searchHost, 0);
        searchRow.Children.Add(searchHost);

        // 来源下拉筛选器：列出历史里出现过的应用，与关键词/类型筛选叠加
        _sourceCombo = new ComboBox
        {
            Width = 150,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };

        _sourceCombo.SelectionChanged += (_, _) =>
        {
            if (_suppressComboEvents)
            {
                return;
            }

            _sourceFilter = _sourceCombo.SelectedIndex > 0 ? _sourceCombo.SelectedItem as string : null;
            Refresh(store);
        };

        Grid.SetColumn(_sourceCombo, 1);
        searchRow.Children.Add(_sourceCombo);
        header.Children.Add(searchRow);

        var chips = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0)
        };

        foreach (var (label, type) in new[]
        {
            ("全部", ClipType.All),
            ("文本", ClipType.Text),
            ("文件", ClipType.File),
            ("图片", ClipType.Image)
        })
        {
            chips.Children.Add(MakeChip(label, type, store, accent));
        }

        header.Children.Add(chips);

        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // ---- 底部提示：放进 Footer 与关闭按钮同一行（提示居中收紧、按钮靠右）----
        // Footer 是横向 StackPanel：给提示固定宽度 + TextAlignment.Center 实现视觉居中
        _hint = new TextBlock
        {
            FontSize = 12,
            Width = 640,
            Margin = new Thickness(12, 0, 12, 0),
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.65,
            Text = "↑↓ 选择 · Enter 粘贴 · 右键更多操作 · 悬停看全文 · Ctrl+F 收藏 · Ctrl+T 待办 · Ctrl+P 固定 · Ctrl+Del 删除"
        };

        // ---- 中部：列表（行高固定两行；全文走悬停浮窗，业内通行的 Ditto/CopyQ 模式）----
        _list = new ListBox
        {
            Margin = new Thickness(12, 0, 12, 0),
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        _list.SelectionChanged += (_, _) =>
        {
            UpdateAccents();
            CloseFlyout();
        };
        _list.MouseDoubleClick += (_, _) => CopySelected(store);

        root.Children.Add(_list);

        var window = new PluginWindow("剪贴板历史", 780, 560, PluginWindowMode.Dialog)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };

        window.ContentHostControl.Content = root;

        window.PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape when _timelinePopup is { IsOpen: true }:
                    CloseTimeline();
                    e.Handled = true;
                    break;

                case Key.Escape:
                    window.Close();
                    e.Handled = true;
                    break;

                case Key.Enter when _timelinePopup is { IsOpen: true }:
                    ActivateTimelineHighlight(store);
                    e.Handled = true;
                    break;

                case Key.Enter:
                    CopySelected(store);
                    e.Handled = true;
                    break;

                // 时间线打开时方向键用于在区间里选择
                case Key.Down when _timelinePopup is { IsOpen: true }:
                    MoveTimelineHighlight(1);
                    e.Handled = true;
                    break;

                case Key.Up when _timelinePopup is { IsOpen: true }:
                    MoveTimelineHighlight(-1);
                    e.Handled = true;
                    break;

                // 焦点在搜索框里也要能用方向键选，所以在这一层拦
                case Key.Down:
                    MoveSelection(1);
                    e.Handled = true;
                    break;

                case Key.Up:
                    MoveSelection(-1);
                    e.Handled = true;
                    break;

                case Key.P when (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control:
                    TogglePin(store);
                    e.Handled = true;
                    break;

                case Key.F when (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control:
                    ToggleFavorite(store);
                    e.Handled = true;
                    break;

                case Key.T when (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control:
                    ToggleTodo(store);
                    e.Handled = true;
                    break;

                case Key.Delete when (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control:
                    DeleteSelected(store);
                    e.Handled = true;
                    break;
            }
        };

        // 点击时间线以外的地方时收起时间线（点时间分组头本身交给它自己做开关切换）
        window.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_timelinePopup is not { IsOpen: true })
            {
                return;
            }

            var source = e.OriginalSource as DependencyObject;
            while (source is not null)
            {
                if (source is Border { Tag: string tag } && tag == TimeHeaderTag)
                {
                    return;
                }

                source = System.Windows.Media.VisualTreeHelper.GetParent(source);
            }

            CloseTimeline();
        };

        window.Closed += (_, _) =>
        {
            _refreshTimer?.Stop();
            _refreshTimer = null;

            _window = null;
            _list = null;
            _search = null;
            _watermark = null;
            _hint = null;
            _sourceCombo = null;
            _sourceFilter = null;
            _highlighted = null;
            Rows.Clear();
            Thumbs.Clear();
            CloseFlyout();
            CloseTimeline();
            ClipboardListener.Log("panel closed", LogLevel.Debug);
        };

        var closeButton = new Button
        {
            Content = "关闭 (Esc)",
            MinWidth = 100,
            Margin = new Thickness(6, 0, 4, 0)
        };

        _closeButton = closeButton;
        closeButton.Click += (_, _) => window.Close();

        // 提示条与关闭按钮同行；日志记录 Footer 实际面板类型，便于针对性调整布局
        window.Footer.Children.Insert(0, _hint);
        window.Footer.Children.Add(closeButton);
        ClipboardListener.Log("footer panel type: " + window.Footer.GetType().Name, LogLevel.Debug);

        _window = window;
        Refresh(store);
        window.Show();

        _search.Focus();
        UpdateWatermark();

        // 来源下拉的主题要等宿主把搜索框画出来之后才能采样
        _window.Dispatcher.BeginInvoke(new Action(ApplySourceComboTheme), System.Windows.Threading.DispatcherPriority.Loaded);

        // 默认来源强制回到"全部来源"（显式兜底，不依赖重建路径）
        _suppressComboEvents = true;
        try
        {
            if (_sourceCombo is not null)
            {
                _sourceCombo.SelectedIndex = 0;
            }
        }
        finally
        {
            _suppressComboEvents = false;
        }

        ClipboardListener.Log("panel shown, previous foreground captured", LogLevel.Debug);
    }

    /// <summary>类型筛选 chip。文件与图片类型在后续版本接入数据后即可用。</summary>
    private static UIElement MakeChip(string label, ClipType type, ClipboardStore store, Color accent)
    {
        var active = _typeFilter == type;

        var text = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = active ? new SolidColorBrush(accent) : SystemColors.ControlTextBrush
        };

        var chip = new Border
        {
            Child = text,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 0),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = active ? new SolidColorBrush(accent) : Brushes.Transparent,
            Background = active ? new SolidColorBrush(Color.FromArgb(28, accent.R, accent.G, accent.B)) : Brushes.Transparent,
            Cursor = Cursors.Hand
        };

        chip.MouseLeftButtonUp += (_, _) =>
        {
            _typeFilter = type;
            RebuildChips(store, accent);
            Refresh(store);
        };

        return chip;
    }

    private static void RebuildChips(ClipboardStore store, Color accent)
    {
        // chips 容器是 header 的第二个子元素
        if (_window?.ContentHostControl.Content is DockPanel root
            && root.Children.Count > 0
            && root.Children[0] is StackPanel header
            && header.Children.Count > 1
            && header.Children[1] is StackPanel chips)
        {
            chips.Children.Clear();
            foreach (var (label, type) in new[]
            {
                ("全部", ClipType.All),
                ("文本", ClipType.Text),
                ("文件", ClipType.File),
                ("图片", ClipType.Image)
            })
            {
                chips.Children.Add(MakeChip(label, type, store, accent));
            }
        }
    }

    /// <summary>
    /// 去抖刷新：打字时只记一次待办，停手 RefreshDebounceMs 之后才真正重建列表。
    /// 重建一行要 5 个 WPF 元素，150 行就是 750 个 —— 每次击键都做会明显发涩。
    /// </summary>
    private static void ScheduleRefresh(ClipboardStore store)
    {
        if (_refreshTimer is null)
        {
            _refreshTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(RefreshDebounceMs)
            };

            _refreshTimer.Tick += (_, _) =>
            {
                _refreshTimer?.Stop();
                Refresh(store);
            };
        }

        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private static void Refresh(ClipboardStore store)
    {
        if (_list is null)
        {
            return;
        }

        // 直接调用（固定 / 删除 / 打字后立刻回车）时取消挂起的去抖刷新，避免同一结果重建两次
        _refreshTimer?.Stop();

        FillSourceCombo(store);

        var filter = _search?.Text?.Trim() ?? string.Empty;
        if (_sourceFilter is not null)
        {
            // 可视化来源筛选：复用 from: 记号语法，与文本关键词自然叠加
            filter = filter.Length > 0 ? filter + " from:" + _sourceFilter : "from:" + _sourceFilter;
        }

        var matched = store.Search(filter.Length == 0 ? null : filter, MaxRows, IsMatch);

        // 一次遍历同时完成：类型过滤、固定/普通拆组、渲染签名。
        // 签名与上次一致就不动视觉树（输错又删掉、Ctrl+P 后同序等场景），整棵子树重建就此免掉。
        var favorites = new List<ClipboardIndexEntry>();
        var todos = new List<ClipboardIndexEntry>();
        var pinned = new List<ClipboardIndexEntry>();
        var normal = new List<ClipboardIndexEntry>();
        var signature = new StringBuilder(512);
        var visible = 0;

        foreach (var entry in matched)
        {
            if (!PassesTypeFilter(entry))
            {
                continue;
            }

            // 时间筛选（区间）：只作用于普通条目 —— 收藏和固定组始终可见
            if (_timeFilterFrom is not null
                && !entry.IsFavorite
                && !entry.IsPinned
                && (entry.CreatedAt < _timeFilterFrom.Value
                    || entry.CreatedAt > (_timeFilterTo ?? DateTime.MaxValue)))
            {
                continue;
            }

            visible++;
            signature.Append(entry.Id).Append(',');
            (entry.IsTodo ? todos : entry.IsFavorite ? favorites : entry.IsPinned ? pinned : normal).Add(entry);
        }

        signature.Append('|').Append(visible).Append('|').Append(store.PinnedCount);
        signature.Append('|').Append(_timeFilterFrom?.Ticks ?? 0).Append('-').Append(_timeFilterTo?.Ticks ?? 0);
        var sign = signature.ToString();

        if (sign == _lastSignature && _list.Items.Count > 0)
        {
            UpdateHint(store);
            return;
        }

        _lastSignature = sign;

        _rebuilding = true;
        try
        {
            _list.Items.Clear();
            Rows.Clear();
            _highlighted = null;

            // 分组顺序：待办（最紧急）→ 收藏（永久保留）→ 固定（仅排序）→ 时间分组
            if (todos.Count > 0)
            {
                SortTodos(todos);
                _list.Items.Add(MakeGroupHeader("待办", todos.Count, store, clickable: false));
                foreach (var entry in todos)
                {
                    AddRow(entry, store);
                }
            }

            if (favorites.Count > 0)
            {
                _list.Items.Add(MakeGroupHeader("收藏", favorites.Count, store, clickable: false));
                foreach (var entry in favorites)
                {
                    AddRow(entry, store);
                }
            }

            // 固定的条目单列一组放最上面 —— 和主流工具的"收藏条"一致，常用内容不用滚
            if (pinned.Count > 0)
            {
                _list.Items.Add(MakeGroupHeader("已固定", pinned.Count, store, clickable: false));
                foreach (var entry in pinned)
                {
                    AddRow(entry, store);
                }
            }

            var groupCounts = new Dictionary<string, int>();
            foreach (var entry in normal)
            {
                var label = GroupLabel(entry.CreatedAt);
                groupCounts[label] = groupCounts.TryGetValue(label, out var n) ? n + 1 : 1;
            }

            string? currentGroup = null;
            foreach (var entry in normal)
            {
                var group = GroupLabel(entry.CreatedAt);
                if (group != currentGroup)
                {
                    currentGroup = group;
                    _list.Items.Add(MakeGroupHeader(group, groupCounts.TryGetValue(group, out var c) ? c : 0, store, clickable: true));
                }

                AddRow(entry, store);
            }

            if (Rows.Count > 0)
            {
                _list.SelectedItem = Rows[0].Container;
            }
        }
        finally
        {
            _rebuilding = false;
        }

        UpdateAccents();
        UpdateHint(store);
    }

    private static void UpdateHint(ClipboardStore store)
    {
        if (_hint is null)
        {
            return;
        }

        var favorites = 0;
        foreach (var entry in store.Snapshot())
        {
            if (entry.IsFavorite)
            {
                favorites++;
            }
        }

        var badges = string.Empty;
        if (favorites > 0 || store.PinnedCount > 0)
        {
            badges = "（"
                + (favorites > 0 ? "收藏 " + favorites : string.Empty)
                + (favorites > 0 && store.PinnedCount > 0 ? " · " : string.Empty)
                + (store.PinnedCount > 0 ? "固定 " + store.PinnedCount : string.Empty)
                + "）";
        }

        _hint.Text = (_timeFilterLabel is null ? string.Empty : "已筛选 " + _timeFilterLabel + " · ")
            + "共 " + store.Count + " 条" + badges
            + " · ↑↓ 选择 · Enter 粘贴 · Ctrl+F 收藏 · Ctrl+P 固定 · Ctrl+Del 删除";
    }

    private static void AddRow(ClipboardIndexEntry entry, ClipboardStore store)
    {
        if (_list is null)
        {
            return;
        }

        var (item, accent, thumbnail) = MakeRow(entry, store);
        _list.Items.Add(item);
        Rows.Add(new Row { Entry = entry, Container = item, Accent = accent, Thumbnail = thumbnail });

        if (entry.Kind == ClipboardEntryKind.Image)
        {
            RequestThumbnail(entry);
        }
    }

    private static ListBoxItem MakeGroupHeader(string label, int count, ClipboardStore store, bool clickable)
    {
        var text = new TextBlock
        {
            Text = clickable ? label + " · " + count + "  ▾" : label + " · " + count,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Opacity = 0.55,
            Margin = new Thickness(2, 6, 2, 2)
        };

        var item = new ListBoxItem
        {
            Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        if (!clickable)
        {
            // 静态头（已固定）不需要交互
            item.IsEnabled = false;
            item.Content = text;
            return item;
        }

        // 可点击的时间头必须保持启用 —— v0.8.4 曾把 !clickable 写反，
        // 时间头被禁用后收不到任何鼠标事件，时间线永远弹不出来。
        // 吞掉 ListBoxItem 默认的悬停/选中高亮，视觉交给 border 自己
        item.Resources[SystemColors.HighlightBrushKey] = Brushes.Transparent;
        item.Resources[SystemColors.ControlBrushKey] = Brushes.Transparent;

        // 时间分组头可点击：点开时间线按天筛选。Border 截获点击，避免触发列表选中
        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4, 2, 4, 2),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = text,
            Tag = TimeHeaderTag
        };

        border.MouseEnter += (_, _) => border.Background = _themeHover ?? Brushes.Transparent;
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        // Down 只拦截不放行：防止冒泡触发列表选中/双击复制并关面板（v0.8.7 的 bug）
        border.MouseLeftButtonDown += (_, e) => e.Handled = true;
        // 切换时间线放在 Up —— Down 会被宿主根拦截，chips 证明 Up 可达
        border.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ToggleTimeline(border, item, store);
        };

        item.Content = border;
        return item;
    }

    /// <summary>时间线：按天列出历史（带条数），点某天只看那天，"全部时间"恢复。</summary>
    private static void ToggleTimeline(Border headerBorder, ListBoxItem headerItem, ClipboardStore store)
    {
        if (_timelinePopup is not null && _timelinePopup.IsOpen && ReferenceEquals(_timelineHeader, headerItem))
        {
            CloseTimeline();
            return;
        }

        OpenTimeline(headerBorder, headerItem, store);
    }

    private static void OpenTimeline(Border headerBorder, ListBoxItem headerItem, ClipboardStore store)
    {
        CloseTimeline();
        CloseFlyout();
        // 清掉可能残留的旧错误提示（比如上一次写回失败留下的），避免误导
        UpdateHint(store);

        var days = new Dictionary<DateTime, int>();
        var oldest = DateTime.Today;
        var maxCount = 1;
        foreach (var entry in store.Snapshot())
        {
            var date = entry.CreatedAt.Date;
            days[date] = days.TryGetValue(date, out var n) ? n + 1 : 1;
            if (date < oldest)
            {
                oldest = date;
            }
        }

        foreach (var n in days.Values)
        {
            if (n > maxCount)
            {
                maxCount = n;
            }
        }

        var today = DateTime.Today;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var prevMonthStart = monthStart.AddMonths(-1);

        var root = new StackPanel { MinWidth = 240 };

        // ---- 快选区：相对区间，覆盖绝大多数场景，一眼可选 ----
        root.Children.Add(QuickRow("全部时间", null, null, store));
        root.Children.Add(QuickRow("今天", today, today, store));
        root.Children.Add(QuickRow("昨天", today.AddDays(-1), today.AddDays(-1), store));
        root.Children.Add(QuickRow("最近 7 天", today.AddDays(-6), today, store));
        root.Children.Add(QuickRow("最近 30 天", today.AddDays(-29), today, store));
        root.Children.Add(QuickRow("本月", monthStart, today, store));
        root.Children.Add(QuickRow("上月", prevMonthStart, monthStart.AddDays(-1), store));

        root.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(8, 6, 8, 6),
            Background = _themeBorderBrush ?? SystemColors.ControlLightBrush
        });

        // ---- 按天列表，按月分组；条数用条形长度表达，一眼看出哪天剪得多 ----
        string? currentMonth = null;
        foreach (var day in days.Keys.OrderByDescending(d => d))
        {
            var monthLabel = day.Year == today.Year ? day.Month + " 月" : day.Year + " 年 " + day.Month + " 月";
            if (monthLabel != currentMonth)
            {
                currentMonth = monthLabel;
                root.Children.Add(new TextBlock
                {
                    Text = monthLabel,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Opacity = 0.55,
                    Margin = new Thickness(10, 8, 10, 2),
                    Foreground = _themeText ?? SystemColors.ControlTextBrush
                });
            }

            root.Children.Add(DayRow(day, days[day], maxCount, store));
        }

        // ---- 底部：说明时间跨度的由来（保留策略决定，不是界面限制）----
        root.Children.Add(new TextBlock
        {
            Text = "历史最早到 " + oldest.ToString("yyyy-MM-dd")
                + "（保留 " + RetentionLabel() + "，可在设置中调整）",
            FontSize = 11,
            Opacity = 0.5,
            Margin = new Thickness(10, 8, 10, 2),
            TextWrapping = TextWrapping.Wrap
        });

        var viewer = new ScrollViewer
        {
            MaxHeight = 380,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root
        };

        var host = new Border
        {
            Background = _themeSurface ?? SystemColors.WindowBrush,
            BorderBrush = _themeBorderBrush ?? SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(5),
            Child = viewer
        };

        // StaysOpen=true：不捕获鼠标；关闭由"点击面板其他位置 / Esc / 选中日期"驱动
        _timelinePopup = new Popup
        {
            PlacementTarget = headerItem,
            Placement = PlacementMode.Right,
            HorizontalOffset = 4,
            StaysOpen = true,
            AllowsTransparency = true,
            Child = host
        };

        _timelineHeader = headerItem;
        _timelinePopup.IsOpen = true;
        ClipboardListener.Log("timeline opened: " + days.Count + " day(s)", LogLevel.Debug);
    }

    /// <summary>快选行：相对区间（今天/最近 7 天/本月…），点击即筛选，再点一次取消。</summary>
    private static UIElement QuickRow(string label, DateTime? from, DateTime? to, ClipboardStore store)
    {
        var selected = IsCurrentFilter(from, to);
        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(2, 1, 2, 1),
            Background = selected ? _themeSelected ?? Brushes.Transparent : Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = label,
                FontSize = 12.5,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = _themeText ?? SystemColors.ControlTextBrush
            }
        };

        AttachTimelineRow(border, from, to, label, store);
        return border;
    }

    /// <summary>某一天的筛选行：日期 + 条形（条数相对长度）+ 条数。</summary>
    private static UIElement DayRow(DateTime day, int count, int maxCount, ClipboardStore store)
    {
        var from = day;
        var to = day.AddDays(1).AddTicks(-1);
        var selected = IsCurrentFilter(from, to);
        var textBrush = _themeText ?? SystemColors.ControlTextBrush;

        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        panel.Children.Add(new TextBlock
        {
            Text = DayLabel(day),
            FontSize = 12.5,
            Width = 82,
            Foreground = textBrush,
            FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal
        });

        panel.Children.Add(new Border
        {
            Height = 6,
            Width = Math.Max(3, Math.Round(70.0 * count / maxCount)),
            CornerRadius = new CornerRadius(3),
            Background = _themeSelected ?? Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });

        panel.Children.Add(new TextBlock
        {
            Text = count.ToString(),
            FontSize = 11.5,
            Width = 34,
            TextAlignment = TextAlignment.Right,
            Opacity = 0.6,
            Foreground = textBrush
        });

        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(2, 1, 2, 1),
            Background = selected ? _themeSelected ?? Brushes.Transparent : Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = panel
        };

        AttachTimelineRow(border, from, to, DayLabel(day), store);
        return border;
    }

    /// <summary>统一挂交互并登记到键盘导航表（↑↓ + Enter 用）。</summary>
    private static void AttachTimelineRow(Border border, DateTime? from, DateTime? to, string label, ClipboardStore store)
    {
        var selected = IsCurrentFilter(from, to);
        _timelineRows.Add(new TimelineRow(border, from, to, label));

        border.MouseEnter += (_, _) => { if (!selected) border.Background = _themeHover ?? Brushes.Transparent; };
        border.MouseLeave += (_, _) => { if (!selected) border.Background = Brushes.Transparent; };
        border.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ApplyTimeFilter(from, to, label, store);
        };
    }

    /// <summary>应用时间筛选；再次点击当前区间即取消（"全部时间"直接清除）。</summary>
    private static void ApplyTimeFilter(DateTime? from, DateTime? to, string label, ClipboardStore store)
    {
        if (from is not null && IsCurrentFilter(from, to))
        {
            _timeFilterFrom = null;
            _timeFilterTo = null;
            _timeFilterLabel = null;
        }
        else
        {
            _timeFilterFrom = from;
            _timeFilterTo = to;
            _timeFilterLabel = from is null ? null : label;
        }

        CloseTimeline();
        Refresh(store);
    }

    private static bool IsCurrentFilter(DateTime? from, DateTime? to)
        => _timeFilterFrom == from && _timeFilterTo == to;

    private static void MoveTimelineHighlight(int delta)
    {
        if (_timelineRows.Count == 0)
        {
            return;
        }

        var next = Math.Clamp(_timelineHighlight + delta, 0, _timelineRows.Count - 1);
        if (next == _timelineHighlight)
        {
            return;
        }

        if (_timelineHighlight >= 0 && _timelineHighlight < _timelineRows.Count)
        {
            var previous = _timelineRows[_timelineHighlight];
            if (!IsCurrentFilter(previous.From, previous.To))
            {
                previous.Row.Background = Brushes.Transparent;
            }
        }

        _timelineHighlight = next;
        _timelineRows[next].Row.Background = _themeHover ?? Brushes.Transparent;
        _timelineRows[next].Row.BringIntoView();
    }

    private static void ActivateTimelineHighlight(ClipboardStore store)
    {
        if (_timelineHighlight < 0 || _timelineHighlight >= _timelineRows.Count)
        {
            return;
        }

        var row = _timelineRows[_timelineHighlight];
        ApplyTimeFilter(row.From, row.To, row.Label, store);
    }

    private static string RetentionLabel()
    {
        var days = ClipboardSettings.Current.RetentionDays;
        return days <= 0 ? "不限时" : days + " 天";
    }

    private static string DayLabel(DateTime date)
    {
        var today = DateTime.Today;
        if (date == today)
        {
            return "今天";
        }

        if (date == today.AddDays(-1))
        {
            return "昨天";
        }

        var week = "日一二三四五六";
        var text = date.ToString("MM-dd") + " 周" + week[(int)date.DayOfWeek];
        return date.Year == today.Year ? text : date.Year + "-" + text;
    }

    private static void CloseTimeline()
    {
        if (_timelinePopup is not null)
        {
            _timelinePopup.IsOpen = false;
        }

        _timelinePopup = null;
        _timelineHeader = null;
        _timelineRows.Clear();
        _timelineHighlight = -1;
    }

    private static (ListBoxItem Item, Border Accent, Image? Thumbnail) MakeRow(ClipboardIndexEntry entry, ClipboardStore store)
    {
        var accent = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(2),
            Background = Brushes.Transparent,
            Margin = new Thickness(0, 0, 8, 0)
        };

        var preview = new TextBlock
        {
            Text = (entry.IsFavorite ? "★ " : "") + entry.Preview,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };

        var meta = new TextBlock
        {
            Text = Meta(entry),
            FontSize = 11.5,
            Opacity = 0.6,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var textColumn = new StackPanel();
        textColumn.Children.Add(preview);
        textColumn.Children.Add(meta);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(accent, 0);
        grid.Children.Add(accent);

        Image? thumbnail = null;
        if (entry.Kind == ClipboardEntryKind.Image)
        {
            thumbnail = new Image
            {
                Height = 44,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Visibility = Visibility.Collapsed // 解码完成后显示，避免空框
            };

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(thumbnail, 1);
            grid.Children.Add(thumbnail);
        }

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(textColumn, grid.ColumnDefinitions.Count - 1);
        grid.Children.Add(textColumn);

        var item = new ListBoxItem
        {
            Content = grid,
            Padding = new Thickness(8, 6, 8, 6),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Tag = entry
        };

        AttachHoverFlyout(item, entry, store);
        item.ContextMenu = BuildRowMenu(entry, store);

        return (item, accent, thumbnail);
    }

    /// <summary>
    /// 缩略图异步解码：背景线程 2 路并发，DecodePixelHeight 只解到目标高度。
    /// 完成后回到 UI 线程，行还活着才回填；缓存命中则立即上屏。
    /// </summary>
    private static void RequestThumbnail(ClipboardIndexEntry entry)
    {
        if (Thumbs.TryGetValue(entry.Id, out var ready))
        {
            ApplyThumbnail(entry.Id, ready);
            return;
        }

        var path = entry.ImagePath;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        Task.Run(async () =>
        {
            await ThumbGate.WaitAsync();
            try
            {
                if (!Thumbs.TryGetValue(entry.Id, out var source))
                {
                    source = ClipboardImageCache.LoadThumbnail(path, 44);
                    if (source is not null)
                    {
                        Thumbs[entry.Id] = source;
                    }
                }

                if (source is not null)
                {
                    ApplyThumbnail(entry.Id, source);
                }
            }
            finally
            {
                ThumbGate.Release();
            }
        });
    }

    private static void ApplyThumbnail(long id, BitmapSource source)
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(() => ApplyThumbnail(id, source));
            return;
        }

        foreach (var row in Rows)
        {
            if (row.Entry.Id == id && row.Thumbnail is not null)
            {
                row.Thumbnail.Source = source;
                row.Thumbnail.Visibility = Visibility.Visible;
            }
        }
    }

    /// <summary>
    /// 只更新"旧选中"和"新选中"两行 —— 之前每次选择都遍历全部行并 new 一遍 SolidColorBrush，
    /// 150 行就是 300 次分配，选择时会有可感的毛刺。
    /// </summary>
    private static void UpdateAccents()
    {
        if (_list is null || _rebuilding)
        {
            return;
        }

        var selected = _list.SelectedItem as ListBoxItem;

        if (_highlighted is not null && !ReferenceEquals(_highlighted, selected))
        {
            foreach (var row in Rows)
            {
                if (ReferenceEquals(row.Container, _highlighted))
                {
                    row.Accent.Background = Brushes.Transparent;
                    _highlighted.FontWeight = FontWeights.Normal;
                    break;
                }
            }
        }

        if (selected is not null)
        {
            foreach (var row in Rows)
            {
                if (ReferenceEquals(row.Container, selected))
                {
                    row.Accent.Background = _accentBrush;
                    selected.FontWeight = FontWeights.SemiBold;
                    break;
                }
            }
        }

        _highlighted = selected;
    }

    /// <summary>
    /// 悬停浮窗（Ditto/CopyQ 的通行模式）：悬停 450ms 且期间鼠标真实移动过才弹出，
    /// 鼠标可以移进浮窗继续操作 —— 文本/文件是只读文本框，可选中、可 Ctrl+C 复制；
    /// 图片显示 400px 大图。关闭时机：鼠标同时离开条目和浮窗（250ms 宽限），
    /// 或切换选中 / 列表刷新 / 关闭面板。
    /// </summary>
    private static void AttachHoverFlyout(ListBoxItem item, ClipboardIndexEntry entry, ClipboardStore store)
    {
        item.MouseEnter += (_, _) => StartFlyoutOpenTimer(item, entry, store);
        item.MouseLeave += (_, _) =>
        {
            CancelFlyoutOpen(item);
            ScheduleFlyoutClose(item);
        };
    }

    /// <summary>
    /// 延迟开浮窗。为什么不能在 MouseEnter 里直接开：
    /// 列表刷新或滚动时，新行会出现在静止的光标下方，WPF 照样触发 MouseEnter ——
    /// 不校验"鼠标真的动过"就会出现"没碰它也弹、还关不掉"。
    /// </summary>
    private static void StartFlyoutOpenTimer(ListBoxItem item, ClipboardIndexEntry entry, ClipboardStore store)
    {
        if (!ClipboardSettings.Current.HoverPreview)
        {
            return;
        }

        NativeMethods.GetCursorPos(out _cursorAtEnter);
        _openPendingItem = item;
        _openPendingEntry = entry;
        _openPendingStore = store;

        if (_flyoutOpenTimer is null)
        {
            _flyoutOpenTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(450)
            };

            _flyoutOpenTimer.Tick += (_, _) =>
            {
                _flyoutOpenTimer?.Stop();

                var pendingItem = _openPendingItem;
                var pendingEntry = _openPendingEntry;
                var pendingStore = _openPendingStore;
                _openPendingItem = null;
                _openPendingEntry = null;
                _openPendingStore = null;

                if (pendingItem is null || pendingEntry is null || pendingStore is null)
                {
                    return;
                }

                if (!ClipboardSettings.Current.HoverPreview || !pendingItem.IsMouseOver)
                {
                    return;
                }

                // 光标停在原地没动：是列表滚进来/重建出来的假 MouseEnter，不弹
                NativeMethods.GetCursorPos(out var now);
                if (now.X == _cursorAtEnter.X && now.Y == _cursorAtEnter.Y)
                {
                    return;
                }

                ShowFlyout(pendingItem, pendingEntry, pendingStore);
            };
        }

        _flyoutOpenTimer.Stop();
        _flyoutOpenTimer.Start();
    }

    private static void CancelFlyoutOpen(ListBoxItem item)
    {
        if (_openPendingItem is not null && ReferenceEquals(_openPendingItem, item))
        {
            _flyoutOpenTimer?.Stop();
            _openPendingItem = null;
            _openPendingEntry = null;
            _openPendingStore = null;
        }
    }

    private static void ShowFlyout(ListBoxItem item, ClipboardIndexEntry entry, ClipboardStore store)
    {
        CloseFlyout();

        UIElement content;
        if (entry.Kind == ClipboardEntryKind.Image)
        {
            content = BuildImageFlyout(entry);
        }
        else
        {
            var text = store.TryGetText(entry.Id);

            // 行内已能看全的（单行短文本，预览即全文）不弹浮窗，避免重复展示
            if (string.IsNullOrEmpty(text) || text == entry.Preview)
            {
                return;
            }

            content = new TextBox
            {
                Text = text,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                FontSize = 12.5,
                MaxWidth = 544,
                MaxHeight = 404,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
        }

        var host = new Border
        {
            Child = content,
            Background = SystemColors.WindowBrush,
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            MaxWidth = 560
        };

        // StaysOpen=true：绝不捕获鼠标，否则条目收不到 MouseLeave，浮窗就关不掉
        _flyout = new Popup
        {
            PlacementTarget = item,
            Placement = PlacementMode.Right,
            HorizontalOffset = 8,
            StaysOpen = true,
            Child = host
        };

        _flyout.MouseEnter += (_, _) => _flyoutCloseTimer?.Stop();
        _flyout.MouseLeave += (_, _) => ScheduleFlyoutClose(item);

        _flyoutItem = item;
        _flyout.IsOpen = true;
    }

    private static void ScheduleFlyoutClose(ListBoxItem owner)
    {
        if (_flyout is null || !ReferenceEquals(_flyoutItem, owner) || !_flyout.IsOpen)
        {
            return;
        }

        if (_flyoutCloseTimer is null)
        {
            _flyoutCloseTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };

            _flyoutCloseTimer.Tick += (_, _) =>
            {
                _flyoutCloseTimer?.Stop();
                if (_flyout is not null && _flyoutItem is not null && !_flyout.IsMouseOver && !_flyoutItem.IsMouseOver)
                {
                    CloseFlyout();
                }
            };
        }

        _flyoutCloseTimer.Stop();
        _flyoutCloseTimer.Start();
    }

    private static void CloseFlyout()
    {
        _flyoutCloseTimer?.Stop();
        if (_flyout is not null)
        {
            _flyout.IsOpen = false;
        }

        _flyout = null;
        _flyoutItem = null;
    }

    /// <summary>
    /// 图片浮窗查看器：Ctrl+滚轮缩放（1x-8x），普通滚轮/滚动条上下滚动（长图跟文本一样），
    /// 双击还原。缩放用 LayoutTransform —— 测量尺寸随缩放变化，ScrollViewer 才能滚到放大后的区域。
    /// </summary>
    private static UIElement BuildImageFlyout(ClipboardIndexEntry entry)
    {
        var source = string.IsNullOrEmpty(entry.ImagePath)
            ? null
            : ClipboardImageCache.DecodeImageForFlyout(entry.ImagePath, entry.PixelWidth, entry.PixelHeight);

        if (source is null)
        {
            return new TextBlock { Text = "（图片已不在缓存中）" };
        }

        var transform = new ScaleTransform(1, 1);
        var image = new Image
        {
            Source = source,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            LayoutTransform = transform
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        var zoom = 1.0;
        var viewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxWidth = 544,
            MaxHeight = 400,
            Background = SystemColors.WindowBrush,
            Content = image
        };

        viewer.PreviewMouseWheel += (_, e) =>
        {
            // 普通滚轮留给滚动（长图往下看，跟文本一致）；Ctrl+滚轮才是缩放
            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
            {
                return;
            }

            e.Handled = true;
            zoom = Math.Clamp(zoom * (e.Delta > 0 ? 1.25 : 0.8), 1.0, 8.0);
            transform.ScaleX = zoom;
            transform.ScaleY = zoom;
        };

        image.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                zoom = 1.0;
                transform.ScaleX = 1;
                transform.ScaleY = 1;
            }
        };

        var hint = new TextBlock
        {
            Text = "Ctrl+滚轮 缩放  ·  滚动条/滚轮 上下看  ·  双击还原",
            FontSize = 11,
            Opacity = 0.65,
            Margin = new Thickness(0, 6, 0, 0)
        };

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(hint, Dock.Bottom);
        root.Children.Add(hint);
        root.Children.Add(viewer);

        return root;
    }

    /// <summary>
    /// 来源下拉选项：历史里出现过的全部应用名。选项集合没变化时不动控件，
    /// 避免每次击键都重建下拉。手动输入的 from: 记号与下拉选择互不冲突（叠加生效）。
    /// </summary>
    private static void FillSourceCombo(ClipboardStore store)
    {
        if (_sourceCombo is null)
        {
            return;
        }

        var sources = store.SourceProcesses();
        var signature = string.Join("\u0001", sources);

        if (signature == _sourceSignature && _sourceCombo.Items.Count > 0)
        {
            return;
        }

        _sourceSignature = signature;
        _suppressComboEvents = true;
        try
        {
            _sourceCombo.Items.Clear();
            _sourceCombo.Items.Add("全部来源");
            foreach (var source in sources)
            {
                _sourceCombo.Items.Add(source);
            }

            var index = _sourceFilter is null ? 0 : sources.FindIndex(s => string.Equals(s, _sourceFilter, StringComparison.OrdinalIgnoreCase));
            _sourceCombo.SelectedIndex = index >= 0 ? index + 1 : 0;
            if (index < 0)
            {
                _sourceFilter = null; // 手动输入的来源已不在历史里，回退到全部
            }
        }
        finally
        {
            _suppressComboEvents = false;
        }
    }

    /// <summary>
    /// 给来源下拉套用与面板一致的主题：直接从搜索框"采样"宿主渲染出来的
    /// 背景/边框/圆角/文字色（宿主怎么画搜索框，下拉就怎么长），
    /// 采样不到再按深浅色主题取兜底色。
    /// </summary>
    private static void ApplySourceComboTheme()
    {
        if (_sourceCombo is null || _search is null)
        {
            return;
        }

        var searchBorder = FindFirstBorder(_search);
        var isDark = IsDarkTheme();

        var surface = _search.Background ?? searchBorder?.Background
            ?? (isDark ? HexBrush("#2B2B2B") : SystemColors.WindowBrush);
        var borderBrush = _search.BorderBrush ?? searchBorder?.BorderBrush
            ?? (isDark ? HexBrush("#454545") : HexBrush("#DDDDDD"));
        var textBrush = _search.Foreground
            ?? (isDark ? HexBrush("#E6E6E6") : SystemColors.ControlTextBrush);

        var radius = 8.0;
        if (searchBorder is not null && searchBorder.CornerRadius.TopLeft > 0)
        {
            radius = searchBorder.CornerRadius.TopLeft;
        }

        var accent = ((SolidColorBrush)_accentBrush).Color;

        _themeSurface = surface;
        _themeBorderBrush = borderBrush;
        _themeText = textBrush;
        _themeHover = new SolidColorBrush(Color.FromArgb(30, accent.R, accent.G, accent.B));
        _themeSelected = new SolidColorBrush(Color.FromArgb(64, accent.R, accent.G, accent.B));

        // 注册到窗口级资源：来源下拉与底部关闭按钮共用同一套主题
        var windowResources = _window?.Resources;
        if (windowResources is not null)
        {
            windowResources["CbSurface"] = surface;
            windowResources["CbBorder"] = borderBrush;
            windowResources["CbText"] = textBrush;
            windowResources["CbHover"] = _themeHover;
            windowResources["CbSelected"] = _themeSelected;
            windowResources["CbRadius"] = new CornerRadius(radius);
        }

        _sourceCombo.Foreground = textBrush;
        _sourceCombo.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(ComboTemplateXaml);
        _sourceCombo.ItemContainerStyle = (Style)System.Windows.Markup.XamlReader.Parse(ComboItemStyleXaml);

        if (_closeButton is not null)
        {
            _closeButton.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(CloseButtonTemplateXaml);
            _closeButton.Foreground = textBrush;
            _closeButton.Background = surface; // 模板用 TemplateBinding Background，不赋值就是默认灰底
        }
    }

    private static Border? FindFirstBorder(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Border border)
            {
                return border;
            }

            var found = FindFirstBorder(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static Brush HexBrush(string hex)
        => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    private const string ComboTemplateXaml = """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         TargetType="{x:Type ComboBox}">
          <Grid>
            <ToggleButton Focusable="False" ClickMode="Press"
                          IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
              <ToggleButton.Template>
                <ControlTemplate TargetType="{x:Type ToggleButton}">
                  <Border CornerRadius="{DynamicResource CbRadius}"
                          Background="{DynamicResource CbSurface}"
                          BorderBrush="{DynamicResource CbBorder}"
                          BorderThickness="1" />
                </ControlTemplate>
              </ToggleButton.Template>
            </ToggleButton>
            <ContentPresenter Content="{TemplateBinding SelectionBoxItem}"
                              ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"
                              Margin="10,5,26,5" VerticalAlignment="Center"
                              TextElement.Foreground="{DynamicResource CbText}"
                              IsHitTestVisible="False" />
            <Path Data="M 0 0 L 4 4 L 8 0 Z" Fill="{DynamicResource CbText}"
                  HorizontalAlignment="Right" VerticalAlignment="Center"
                  Margin="0,0,10,0" IsHitTestVisible="False" />
            <Popup x:Name="PART_Popup" IsOpen="{TemplateBinding IsDropDownOpen}"
                   AllowsTransparency="True" Placement="Bottom" Focusable="False"
                   PopupAnimation="Fade">
              <Border CornerRadius="{DynamicResource CbCorner}"
                      Background="{DynamicResource CbSurface}"
                      BorderBrush="{DynamicResource CbBorder}"
                      BorderThickness="1" Margin="0,2,0,0"
                      MinWidth="{TemplateBinding ActualWidth}" MaxHeight="320">
                <ScrollViewer VerticalScrollBarVisibility="Auto">
                  <ItemsPresenter Margin="2" />
                </ScrollViewer>
              </Border>
            </Popup>
          </Grid>
        </ControlTemplate>
        """;

    private const string ComboItemStyleXaml = """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
               TargetType="{x:Type ComboBoxItem}">
          <Setter Property="Foreground" Value="{DynamicResource CbText}" />
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="{x:Type ComboBoxItem}">
                <Border x:Name="B" CornerRadius="6" Padding="8,5" Margin="4,1" Background="Transparent">
                  <ContentPresenter />
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsHighlighted" Value="True">
                    <Setter TargetName="B" Property="Background" Value="{DynamicResource CbHover}" />
                  </Trigger>
                  <Trigger Property="IsSelected" Value="True">
                    <Setter TargetName="B" Property="Background" Value="{DynamicResource CbSelected}" />
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """;

    private const string CloseButtonTemplateXaml = """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         TargetType="{x:Type Button}">
          <Border x:Name="B" CornerRadius="{DynamicResource CbRadius}"
                  Background="{TemplateBinding Background}"
                  BorderBrush="{DynamicResource CbBorder}" BorderThickness="1"
                  Padding="10,5">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="B" Property="Background" Value="{DynamicResource CbHover}" />
            </Trigger>
            <Trigger Property="IsPressed" Value="True">
              <Setter TargetName="B" Property="Background" Value="{DynamicResource CbSelected}" />
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """;

    private static void UpdateWatermark()
    {
        if (_watermark is not null && _search is not null)
        {
            _watermark.Visibility = string.IsNullOrEmpty(_search.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private static void MoveSelection(int delta)
    {
        if (_list is null || Rows.Count == 0)
        {
            return;
        }

        // 直接按 Rows 索引移动，天然跳过分组标题
        var current = 0;
        for (var i = 0; i < Rows.Count; i++)
        {
            if (ReferenceEquals(Rows[i].Container, _list.SelectedItem))
            {
                current = i;
                break;
            }
        }

        var next = Math.Clamp(current + Math.Sign(delta), 0, Rows.Count - 1);
        _list.SelectedItem = Rows[next].Container;
        _list.ScrollIntoView(Rows[next].Container);
    }

    private static void CopySelected(ClipboardStore store)
    {
        // 打字后立刻回车：去抖可能还没触发，先补一次刷新，
        // 确保粘的是过滤结果里当前选中的那条，而不是旧列表的残留选中
        if (_refreshTimer is { IsEnabled: true })
        {
            Refresh(store);
        }

        if (_list?.SelectedItem is not ListBoxItem { Tag: ClipboardIndexEntry entry })
        {
            return;
        }

        if (entry.Kind == ClipboardEntryKind.Image)
        {
            CopyImageSelected(entry);
            return;
        }

        if (entry.Kind == ClipboardEntryKind.File)
        {
            CopyFilesSelected(store, entry);
            return;
        }

        var text = store.TryGetText(entry.Id);
        if (string.IsNullOrEmpty(text))
        {
            if (_hint is not null)
            {
                _hint.Text = "该条内容已不可用（可能已被淘汰）";
            }

            return;
        }

        // 写回放后台线程：外部进程占用剪贴板时重试窗口约 0.8 秒，不能让面板卡住
        if (_copyBusy)
        {
            return;
        }

        _copyBusy = true;
        if (_hint is not null)
        {
            _hint.Text = "正在写回，请稍候…";
        }

        Task.Run(() => ClipboardWriter.SetText(text))
            .ContinueWith(t =>
            {
                _copyBusy = false;
                if (!t.Result)
                {
                    if (_hint is not null)
                    {
                        _hint.Text = "写入剪贴板失败，请再试一次";
                    }

                    return;
                }

                ClipboardListener.Log("copied back from panel: " + text.Length + " chars", LogLevel.Info);
                _window?.Close();
                ReturnFocusToPreviousWindow();
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// 文件条目回写：负载是换行分隔的路径清单；写回时剔除已不存在的文件，
    /// 全部失效则提示。成功的场合若部分失效，把说明留在 hint 里。
    /// </summary>
    private static void CopyFilesSelected(ClipboardStore store, ClipboardIndexEntry entry)
    {
        var pathList = store.TryGetText(entry.Id);
        if (string.IsNullOrEmpty(pathList))
        {
            if (_hint is not null)
            {
                _hint.Text = "该条内容已不可用（可能已被淘汰）";
            }

            return;
        }

        var paths = pathList.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (!ClipboardWriter.SetFiles(paths, out var issue))
        {
            if (_hint is not null && issue is not null)
            {
                _hint.Text = issue;
            }

            return;
        }

        ClipboardListener.Log("copied file list back from panel: " + paths.Length + " path(s)", LogLevel.Info);
        _window?.Close();
        ReturnFocusToPreviousWindow();
    }

    /// <summary>
    /// 图片写回：PNG 解码 + BMP 重编码在后台线程做（大图几百毫秒，不能卡 UI），
    /// 完成后回 UI 线程写剪贴板并关窗还原焦点。防重入：写回期间再按 Enter 无效。
    /// </summary>
    private static void CopyImageSelected(ClipboardIndexEntry entry)
    {
        if (_copyBusy)
        {
            return;
        }

        if (string.IsNullOrEmpty(entry.ImagePath))
        {
            if (_hint is not null)
            {
                _hint.Text = "该图片已不在缓存中（可能已被淘汰）";
            }

            return;
        }

        _copyBusy = true;
        if (_hint is not null)
        {
            _hint.Text = "正在写回图片，请稍候…";
        }

        var path = entry.ImagePath;
        Task.Run(() => ClipboardImageCache.LoadDibForPaste(path))
            .ContinueWith(t =>
            {
                _copyBusy = false;
                var dib = t.Result;

                if (dib is null)
                {
                    if (_hint is not null)
                    {
                        _hint.Text = "图片文件已丢失，无法写回";
                    }

                    return;
                }

                if (!ClipboardWriter.SetDib(dib))
                {
                    if (_hint is not null)
                    {
                        _hint.Text = "写回剪贴板失败，请再试一次";
                    }

                    return;
                }

                ClipboardListener.Log("copied image back from panel: " + ClipboardIndexEntry.FormatBytes(dib.Length), LogLevel.Info);
                _window?.Close();
                ReturnFocusToPreviousWindow();
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>把焦点还给用户按热键时所在的窗口，这样可以直接 Ctrl+V。</summary>
    private static void ReturnFocusToPreviousWindow()
    {
        if (_previousForeground == IntPtr.Zero)
        {
            return;
        }

        try
        {
            _ = NativeMethods.SetForegroundWindow(_previousForeground);
        }
        catch (Exception ex)
        {
            ClipboardListener.Log("could not restore previous foreground window: " + ex.Message, LogLevel.Debug);
        }
    }

    private static void TogglePin(ClipboardStore store)
    {
        if (_list?.SelectedItem is not ListBoxItem { Tag: ClipboardIndexEntry entry })
        {
            return;
        }

        var pinned = store.TogglePin(entry.Id);
        ClipboardListener.Log("entry " + entry.Id + " pinned=" + pinned, LogLevel.Debug);
        Refresh(store);
    }

    /// <summary>Ctrl+F：切换收藏。收藏条目永不淘汰、随快照永久保留。</summary>
    private static void ToggleFavorite(ClipboardStore store)
    {
        if (_list?.SelectedItem is not ListBoxItem { Tag: ClipboardIndexEntry entry })
        {
            return;
        }

        var favorite = store.ToggleFavorite(entry.Id);
        ClipboardListener.Log("entry " + entry.Id + " favorite=" + favorite, LogLevel.Debug);
        Refresh(store);
    }

    private static void DeleteSelected(ClipboardStore store)
    {
        if (_list?.SelectedItem is not ListBoxItem { Tag: ClipboardIndexEntry entry })
        {
            return;
        }

        _ = store.Remove(entry.Id);
        ClipboardListener.Log("entry " + entry.Id + " removed from history", LogLevel.Debug);
        Refresh(store);
    }

    // ---- 待办 / 置顶时限 / 提醒 ----

    /// <summary>Ctrl+T：标记/取消待办（默认永久置顶，且豁免淘汰）。</summary>
    private static void ToggleTodo(ClipboardStore store)
    {
        if (_list?.SelectedItem is not ListBoxItem { Tag: ClipboardIndexEntry entry })
        {
            return;
        }

        var todo = store.ToggleTodo(entry.Id);
        ClipboardListener.Log("entry " + entry.Id + " todo=" + todo, LogLevel.Debug);
        ClipboardHistoryPlugin.Reminders?.Arm();
        Refresh(store);
    }

    private static void ApplyPin(ClipboardStore store, ClipboardIndexEntry entry, DateTime? until)
    {
        store.SetPin(entry.Id, until);
        ClipboardHistoryPlugin.Reminders?.Arm();
        Refresh(store);
    }

    private static void ApplyUnpin(ClipboardStore store, ClipboardIndexEntry entry)
    {
        store.ClearPin(entry.Id);
        ClipboardHistoryPlugin.Reminders?.Arm();
        Refresh(store);
    }

    private static void ApplyReminder(ClipboardStore store, ClipboardIndexEntry entry, DateTime? at)
    {
        store.SetReminder(entry.Id, at);
        ClipboardHistoryPlugin.Reminders?.Arm();
        Refresh(store);
    }

    /// <summary>
    /// 待办排序（设置"按紧急度排序"开启时）：有提醒的按时间升序 ——
    /// 越临近越靠上、已错过的排最顶；无提醒的按加入时间倒序排在其后。
    /// </summary>
    private static void SortTodos(List<ClipboardIndexEntry> todos)
    {
        if (!ClipboardSettings.Current.SortTodosByDue)
        {
            todos.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
            return;
        }

        todos.Sort((a, b) =>
        {
            var left = a.RemindAt;
            var right = b.RemindAt;
            if (left is not null && right is not null)
            {
                return left.Value.CompareTo(right.Value);
            }

            if (left is not null)
            {
                return -1;
            }

            if (right is not null)
            {
                return 1;
            }

            return b.CreatedAt.CompareTo(a.CreatedAt);
        });
    }

    /// <summary>
    /// 右键菜单：鼠标路径的完整操作入口（与快捷键等价）。
    /// 注意 ContextMenu 是独立视觉树，主题画刷要在菜单自己的资源里再注册一份。
    /// </summary>
    private static ContextMenu BuildRowMenu(ClipboardIndexEntry entry, ClipboardStore store)
    {
        var menu = new ContextMenu
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
            FontSize = 12.5,
            Background = _themeSurface ?? SystemColors.WindowBrush,
            BorderBrush = _themeBorderBrush ?? SystemColors.ControlDarkBrush,
            Foreground = _themeText ?? SystemColors.ControlTextBrush
        };

        menu.Items.Add(NewMenuItem("粘贴到刚才的窗口", () => CopySelected(store)));

        menu.Items.Add(NewMenuItem(entry.IsFavorite ? "取消收藏" : "收藏", () =>
        {
            store.ToggleFavorite(entry.Id);
            Refresh(store);
        }));

        menu.Items.Add(NewMenuItem(entry.IsTodo ? "取消待办" : "标记待办", () =>
        {
            store.ToggleTodo(entry.Id);
            ClipboardHistoryPlugin.Reminders?.Arm();
            Refresh(store);
        }));

        var pin = new MenuItem { Header = "置顶" };
        pin.Items.Add(NewMenuItem("永久", () => ApplyPin(store, entry, null)));
        pin.Items.Add(NewMenuItem("10 分钟", () => ApplyPin(store, entry, DateTime.Now.AddMinutes(10))));
        pin.Items.Add(NewMenuItem("1 小时", () => ApplyPin(store, entry, DateTime.Now.AddHours(1))));
        pin.Items.Add(NewMenuItem("今天 18:00", () => ApplyPin(store, entry, TodayAt(18))));
        pin.Items.Add(NewMenuItem("明天 9:00", () => ApplyPin(store, entry, DateTime.Today.AddDays(1).AddHours(9))));
        if (entry.IsPinned)
        {
            pin.Items.Add(new Separator());
            pin.Items.Add(NewMenuItem("取消置顶", () => ApplyUnpin(store, entry)));
        }

        menu.Items.Add(pin);

        var remind = new MenuItem { Header = "提醒" };
        remind.Items.Add(NewMenuItem("取消提醒", () => ApplyReminder(store, entry, null)));
        remind.Items.Add(NewMenuItem("10 分钟后", () => ApplyReminder(store, entry, DateTime.Now.AddMinutes(10))));
        remind.Items.Add(NewMenuItem("30 分钟后", () => ApplyReminder(store, entry, DateTime.Now.AddMinutes(30))));
        remind.Items.Add(NewMenuItem("1 小时后", () => ApplyReminder(store, entry, DateTime.Now.AddHours(1))));
        remind.Items.Add(NewMenuItem("今天 18:00", () => ApplyReminder(store, entry, TodayAt(18))));
        remind.Items.Add(NewMenuItem("明天 9:00", () => ApplyReminder(store, entry, DateTime.Today.AddDays(1).AddHours(9))));
        menu.Items.Add(remind);

        menu.Items.Add(new Separator());
        menu.Items.Add(NewMenuItem("删除", () =>
        {
            _ = store.Remove(entry.Id);
            Refresh(store);
        }));

        return menu;
    }

    private static MenuItem NewMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>今天某点；已过则顺延到明天同时刻。</summary>
    private static DateTime TodayAt(int hour)
    {
        var at = DateTime.Today.AddHours(hour);
        return at <= DateTime.Now ? at.AddDays(1) : at;
    }

    /// <summary>通知点击进入：打开面板并选中对应条目（被筛选隐藏时只打开面板）。</summary>
    internal static void OpenTo(long entryId)
    {
        var store = ClipboardHistoryPlugin.Store;
        if (store is null)
        {
            return;
        }

        ShowPanel();

        foreach (var row in Rows)
        {
            if (row.Entry.Id != entryId)
            {
                continue;
            }

            if (_list is not null)
            {
                _list.SelectedItem = row.Container;
                row.Container.BringIntoView();
            }

            break;
        }
    }

    /// <summary>提醒/到期导致数据变化时刷新已打开的面板。</summary>
    internal static void NotifyStoreChanged(ClipboardStore store)
    {
        if (_list is not null && _window is { IsVisible: true })
        {
            Refresh(store);
        }
    }

    private static bool PassesTypeFilter(ClipboardIndexEntry entry)
    {
        return _typeFilter switch
        {
            ClipType.All => true,
            ClipType.Text => entry.Kind == ClipboardEntryKind.Text,
            ClipType.Image => entry.Kind == ClipboardEntryKind.Image,
            ClipType.File => entry.Kind == ClipboardEntryKind.File,
            _ => false
        };
    }

    private static string GroupLabel(DateTime time)
    {
        var today = DateTime.Today;
        if (time.Date == today)
        {
            return "今天";
        }

        if (time.Date == today.AddDays(-1))
        {
            return "昨天";
        }

        return time.Date > today.AddDays(-7) ? "本周早些时候" : "更早";
    }

    private static string RelativeTime(DateTime time)
    {
        var delta = DateTime.Now - time;

        if (delta.TotalSeconds < 60)
        {
            return "刚刚";
        }

        if (delta.TotalMinutes < 60)
        {
            return (int)delta.TotalMinutes + " 分钟前";
        }

        var today = DateTime.Today;
        if (time.Date == today)
        {
            return time.ToString("HH:mm");
        }

        if (time.Date == today.AddDays(-1))
        {
            return "昨天 " + time.ToString("HH:mm");
        }

        return delta.TotalDays < 7
            ? time.ToString("MM-dd HH:mm")
            : time.ToString("yyyy-MM-dd HH:mm");
    }

    private static string Meta(ClipboardIndexEntry entry)
    {
        var parts = new List<string>(4)
        {
            RelativeTime(entry.CreatedAt),
            entry.Kind switch
            {
                ClipboardEntryKind.Image => entry.PixelWidth + "\u00D7" + entry.PixelHeight + " · " + ClipboardIndexEntry.FormatBytes(entry.Length),
                ClipboardEntryKind.File => entry.Length + " 个文件",
                _ => entry.Length.ToString("N0") + " 字符"
            }
        };

        if (ClipboardSettings.Current.ShowSourceProcess && !string.IsNullOrEmpty(entry.SourceProcess))
        {
            parts.Add(entry.SourceProcess!);
        }

        if (entry.IsTodo)
        {
            parts.Add("待办");
        }
        else if (entry.IsPinned)
        {
            parts.Add(entry.PinnedUntil is { } pinUntil
                ? "置顶至 " + pinUntil.ToString("MM-dd HH:mm")
                : "已固定");
        }

        if (entry.RemindAt is { } remindAt)
        {
            parts.Add("提醒 " + remindAt.ToString("MM-dd HH:mm"));
        }

        return string.Join("  ·  ", parts);
    }

    private static bool IsDarkTheme()
    {
        try
        {
            return ThemeService.IsDarkTheme;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsMatch(string filter, string text)
    {
        try
        {
            return FuzzyMatchService.IsMatch(filter, text);
        }
        catch
        {
            return text.Contains(filter, StringComparison.OrdinalIgnoreCase);
        }
    }
}
