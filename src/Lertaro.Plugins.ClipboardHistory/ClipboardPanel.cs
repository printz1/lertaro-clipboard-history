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

    /// <summary>
    /// 顶部筛选 chip 那一行的容器。**直接持有引用**，不要靠"遍历窗口内容树"去找它：
    /// 那条路径依赖宿主 PluginWindow 怎么包 Content，宿主一变就静默失败，
    /// 结果就是 ★收藏 / ☐待办 两个 chip 永远建不出来（v0.12.0 实测踩过）。
    /// </summary>
    private static StackPanel? _chipsRow;
    private static TextBox? _search;
    private static TextBlock? _watermark;
    private static TextBlock? _hint;
    private static IntPtr _previousForeground;
    private static ClipType _typeFilter = ClipType.All;
    private static MarkFilter _markFilter = MarkFilter.None;
    private static System.Windows.Threading.DispatcherTimer? _refreshTimer;
    /// <summary>时间选择弹窗复用的主题画刷（与面板同一套采样值）。</summary>
    internal static Brush ThemeSurface => _themeSurface ?? SystemColors.WindowBrush;

    internal static Brush ThemeBorder => _themeBorderBrush ?? SystemColors.ControlDarkBrush;

    internal static Brush ThemeText => _themeText ?? SystemColors.ControlTextBrush;

    internal static Brush ThemeHover => _themeHover ?? Brushes.Transparent;

    internal static Brush ThemeSelected => _themeSelected ?? Brushes.Transparent;

    internal static Brush ThemeAccent => _accentBrush;

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
    private static Button? _notifyButton;
    private static Popup? _notifyPopup;
    private static bool _notifyTodayOnly = true;
    private static long _justArchivedId;

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

    /// <summary>状态筛选（与类型筛选正交、可叠加）。None = 不筛。</summary>
    private enum MarkFilter
    {
        None,
        Favorite,
        Todo
    }

    private sealed class Row
    {
        internal ClipboardIndexEntry Entry = null!;
        internal ListBoxItem Container = null!;
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

        // 每次打开面板都回到"全部"视图：不残留上次的收藏/待办/类型/时间筛选
        _typeFilter = ClipType.All;
        _markFilter = MarkFilter.None;
        _timeFilterFrom = null;
        _timeFilterTo = null;
        _timeFilterLabel = null;

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

        // chip 由 RebuildChips 统一重建（类型 + 状态筛选都在里面），这里只登记容器
        _chipsRow = chips;
        header.Children.Add(chips);

        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // ---- 底部提示：放进 Footer 与关闭按钮同一行（提示居中收紧、按钮靠右）----
        // Footer 是横向 StackPanel：给提示固定宽度 + TextAlignment.Center 实现视觉居中
        _hint = new TextBlock
        {
            FontSize = 12,
            Width = 430, // 底部现在有三个按钮（收藏与待办 / 通知 / 关闭），留出空间避免被裁切
            Margin = new Thickness(12, 0, 12, 0),
            TextAlignment = TextAlignment.Left,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.65,
            Text = "↑↓ 选择 · Enter 粘贴 · 右键更多操作 · Ctrl+F 收藏 · Ctrl+T 待办 · Ctrl+P 固定"
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
                case Key.Escape when _notifyPopup is { IsOpen: true }:
                    CloseNotificationLog();
                    e.Handled = true;
                    break;

                case Key.Escape when ClipboardWhenPicker.IsOpen:
                    ClipboardWhenPicker.Close();
                    e.Handled = true;
                    break;

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

        // 点击时间线/时间选择器以外的地方时收起它们（点分组头本身交给它自己做开关）
        window.PreviewMouseLeftButtonDown += (_, e) =>
        {
            var source = e.OriginalSource as DependencyObject;
            var inTimeHeader = false;
            for (var node = source; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
            {
                if (node is Border { Tag: string tag } && tag == TimeHeaderTag)
                {
                    inTimeHeader = true;
                    break;
                }
            }

            if (_timelinePopup is { IsOpen: true } && !inTimeHeader)
            {
                CloseTimeline();
            }

            // 选择器/菜单点外面即收起
            if (!inTimeHeader)
            {
                ClipboardWhenPicker.Close();
                CloseNotificationLog();
            }
        };

        window.Closed += (_, _) =>
        {
            _refreshTimer?.Stop();
            _refreshTimer = null;

            _window = null;
            _list = null;
            _chipsRow = null;
            _search = null;
            _watermark = null;
            _hint = null;
            _sourceCombo = null;
            _sourceFilter = null;
            _notifyButton = null;
            CloseNotificationLog();
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
        var notifyButton = new Button
        {
            // 一眼看到今天有多少通知
            Content = "通知 (今日 " + ClipboardNotificationLog.TodayCount + " / 共 " + ClipboardNotificationLog.Count + ")",
            MinWidth = 160,
            Margin = new Thickness(6, 0, 0, 0)
        };

        _notifyButton = notifyButton;
        notifyButton.Click += (_, _) => OpenNotificationLog(ClipboardHistoryPlugin.Store!);

        window.Footer.Children.Insert(0, _hint);
        window.Footer.Children.Add(notifyButton);
        window.Footer.Children.Add(closeButton);
        ClipboardListener.Log("footer panel type: " + window.Footer.GetType().Name, LogLevel.Debug);

        _window = window;
        RebuildChips(store, accent); // 初始就要把"收藏/待办"两个 chip 一起建出来（否则不点类型筛选它们不出现）
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
            Foreground = active ? new SolidColorBrush(accent) : _themeText ?? SystemColors.ControlTextBrush
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

        // 类型筛选在"收藏 / 待办"视图里照样生效（可以只看"图片收藏"），不再置灰
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
        var chips = _chipsRow;
        if (chips is null)
        {
            return;
        }

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

        // 分隔：右侧是"状态筛选"，与类型筛选正交、可叠加
        chips.Children.Add(new Border
        {
            Width = 1,
            Margin = new Thickness(4, 4, 10, 4),
            Background = _themeBorderBrush ?? SystemColors.ControlLightBrush
        });

        // 计数直接写在 chip 上：一眼能看到"我收藏了几条 / 还剩几件待办"，
        // 不然点进去之前完全不知道里面有没有东西
        var favorites = 0;
        var todos = 0;
        foreach (var entry in store.Snapshot())
        {
            if (entry.IsFavorite)
            {
                favorites++;
            }

            if (entry.IsTodo && !entry.IsTodoArchived)
            {
                todos++;
            }
        }

        chips.Children.Add(MakeMarkChip(ChipLabel("★ 收藏", favorites), MarkFilter.Favorite, store, accent));
        chips.Children.Add(MakeMarkChip(ChipLabel("☐ 待办", todos), MarkFilter.Todo, store, accent));
    }

    private static string ChipLabel(string label, int count) => count > 0 ? label + " " + count : label;

    /// <summary>状态筛选 chip（收藏 / 待办）：开关式，可与类型、来源、时间叠加。</summary>
    private static UIElement MakeMarkChip(string label, MarkFilter filter, ClipboardStore store, Color accent)
    {
        var active = _markFilter == filter;

        var text = new TextBlock
        {
            Text = label,
            FontSize = 12,
            FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = active ? new SolidColorBrush(accent) : _themeText ?? SystemColors.ControlTextBrush
        };

        var chip = new Border
        {
            Child = text,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 0),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = active ? new SolidColorBrush(accent) : Brushes.Transparent,
            Background = active
                ? new SolidColorBrush(Color.FromArgb(28, accent.R, accent.G, accent.B))
                : Brushes.Transparent,
            Cursor = Cursors.Hand
        };

        chip.MouseLeftButtonUp += (_, _) =>
        {
            _markFilter = active ? MarkFilter.None : filter; // 再点一次取消
            RebuildChips(store, accent);
            Refresh(store);
        };

        return chip;
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

        // 标记视图：点筛选行的 ★ 收藏 / ☐ 待办 进入；主界面同样会显示被标记的条目
        var marksView = _markFilter != MarkFilter.None;
        var todoView = _markFilter == MarkFilter.Todo;
        var matched = marksView
            ? store.Snapshot()
            : store.Search(filter.Length == 0 ? null : filter, MaxRows, IsMatch);

        // 一次遍历同时完成：类型过滤、分组、渲染签名。
        // 签名与上次一致就不动视觉树（输错又删掉等场景），整棵子树重建就此免掉。
        var todos = new List<ClipboardIndexEntry>();
        var favorites = new List<ClipboardIndexEntry>();
        var pinned = new List<ClipboardIndexEntry>();
        var normal = new List<ClipboardIndexEntry>();
        var archived = new List<ClipboardIndexEntry>();
        var signature = new StringBuilder(512);
        var visible = 0;

        foreach (var entry in matched)
        {
            if (marksView)
            {
                // 标记页：只收当前筛选对应的标记
                var wanted = todoView ? entry.IsTodo : entry.IsFavorite;
                if (!wanted)
                {
                    continue;
                }
            }
            else if (entry.IsTodoArchived)
            {
                // 已完成的待办不在主界面添噪音，只在"待办"视图的"已归档"里看
                continue;
            }

            // 类型筛选在两个视图里都生效（可以只看"图片收藏"）
            if (!PassesTypeFilter(entry))
            {
                continue;
            }

            // 时间筛选（区间）：对收藏 / 待办 / 置顶不生效 —— 重要的东西不该被"看今天"筛没了
            var timeExempt = entry.IsFavorite || entry.IsTodo || entry.IsPinned;
            if (_timeFilterFrom is not null
                && !timeExempt
                && (entry.CreatedAt < _timeFilterFrom.Value
                    || entry.CreatedAt > (_timeFilterTo ?? DateTime.MaxValue)))
            {
                continue;
            }

            visible++;

            // 签名带上标记位：只改标记（Ctrl+F / Ctrl+T / Ctrl+P）时列表顺序可能没变，
            // 不带标记位就会因为"签名没变"跳过重建，行首的 ★/☐ 要等下一次刷新才出现
            signature.Append(entry.Id)
                .Append(entry.IsFavorite ? 'F' : '-')
                .Append(entry.IsTodo ? 'T' : '-')
                .Append(entry.IsPinned ? 'P' : '-')
                .Append(entry.IsTodoArchived ? 'A' : '-')
                .Append(',');

            if (marksView)
            {
                if (!todoView)
                {
                    favorites.Add(entry);
                }
                else if (entry.IsTodoArchived)
                {
                    archived.Add(entry);
                }
                else
                {
                    todos.Add(entry);
                }

                continue;
            }

            // 主界面：被标记的内容也留在列表里（归到顶部固定分组），不再"收藏完就消失"
            if (entry.IsTodo)
            {
                todos.Add(entry);
            }
            else if (entry.IsFavorite)
            {
                favorites.Add(entry);
            }
            else if (entry.IsPinned)
            {
                pinned.Add(entry);
            }
            else
            {
                normal.Add(entry);
            }
        }

        // 待办按紧急度排序（有提醒的按时间升序）—— 主界面的待办分组与待办视图一致
        SortTodos(todos);

        // 标记视图里类型/来源/搜索都不参与筛选 —— 一并置灰，行为与视觉一致
        if (_search is not null)
        {
            _search.IsEnabled = !marksView;
        }

        if (_sourceCombo is not null)
        {
            _sourceCombo.IsEnabled = !marksView;
        }

        // 自检日志：标记条目为什么没出现在主界面，看这一行就能定位（类型筛选/时间筛选/标记计数）
        ClipboardListener.Log(
            "panel refresh: total=" + store.Count + " type=" + _typeFilter + " mark=" + _markFilter
                + " time=" + (_timeFilterFrom is null ? "off" : "on")
                + " source=" + (_sourceFilter ?? "-")
                + " visible=" + visible + " pinned=" + pinned.Count + " archived=" + archived.Count
                + " normal=" + normal.Count
                + " marksInStore=" + CountMarked(store),
            LogLevel.Debug);

        signature.Append('|').Append(visible).Append('|').Append(store.PinnedCount);
        signature.Append('|').Append((int)_markFilter);
        signature.Append('|').Append(_timeFilterFrom?.Ticks ?? 0).Append('-').Append(_timeFilterTo?.Ticks ?? 0);
        var sign = signature.ToString();

        if (sign == _lastSignature && _list.Items.Count > 0)
        {
            UpdateHint(store, visible);
            return;
        }

        _lastSignature = sign;

        _rebuilding = true;
        try
        {
            _list.Items.Clear();
            Rows.Clear();
            _highlighted = null;

            if (marksView)
            {
                // 收藏视图：一条"收藏"段；待办视图：待办 + 已归档 两段
                if (!todoView)
                {
                    if (favorites.Count > 0)
                    {
                        _list.Items.Add(MakeGroupHeader("收藏", favorites.Count, store, clickable: false));
                        foreach (var entry in favorites)
                        {
                            AddRow(entry, store);
                        }
                    }
                }
                else
                {
                    if (todos.Count > 0)
                    {
                        _list.Items.Add(MakeGroupHeader("待办", todos.Count, store, clickable: false));
                        foreach (var entry in todos)
                        {
                            AddRow(entry, store);
                        }
                    }

                    if (archived.Count > 0)
                    {
                        _list.Items.Add(MakeGroupHeader("已归档", archived.Count, store, clickable: false));
                        foreach (var entry in archived)
                        {
                            AddRow(entry, store);
                        }
                    }
                }
            }
            else
            {
            // 收藏 / 待办 / 置顶 各自成组放最上面：标记过的条目仍然留在主界面
            //（旧行为是"收藏后从主列表消失"，只会让人以为东西丢了），时间轴里不重复出现
            if (todos.Count > 0)
            {
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

            if (pinned.Count > 0)
            {
                _list.Items.Add(MakeGroupHeader("置顶", pinned.Count, store, clickable: false));
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
        UpdateHint(store, visible);
    }

    /// <summary>
    /// 底部提示：显示多少条 / 库里共多少条 + 收藏·待办·置顶计数。
    /// visible 传 -1 表示"用当前已渲染的行数"（时间线重开时刷提示用）。
    /// </summary>
    private static void UpdateHint(ClipboardStore store, int visible = -1)
    {
        if (_hint is null)
        {
            return;
        }

        var favorites = 0;
        var todos = 0;
        foreach (var entry in store.Snapshot())
        {
            if (entry.IsFavorite)
            {
                favorites++;
            }

            if (entry.IsTodo && !entry.IsTodoArchived)
            {
                todos++;
            }
        }

        var parts = new List<string>(3);
        if (favorites > 0)
        {
            parts.Add("收藏 " + favorites);
        }

        if (todos > 0)
        {
            parts.Add("待办 " + todos);
        }

        if (store.PinnedCount > 0)
        {
            parts.Add("置顶 " + store.PinnedCount);
        }

        var badges = parts.Count > 0 ? "（" + string.Join(" · ", parts) + "）" : string.Empty;
        var markings = _markFilter == MarkFilter.Favorite
            ? "【收藏】"
            : _markFilter == MarkFilter.Todo ? "【待办】" : string.Empty;

        // 筛选时仍写"共 N 条"会让人以为筛选没生效，所以直接说"显示 X / 共 N 条"
        _hint.Text = markings
            + (_timeFilterLabel is null ? string.Empty : "已筛选 " + _timeFilterLabel + " · ")
            + "显示 " + (visible >= 0 ? visible : Rows.Count) + " / 共 " + store.Count + " 条" + badges;
    }

    private static void AddRow(ClipboardIndexEntry entry, ClipboardStore store)
    {
        if (_list is null)
        {
            return;
        }

        var (item, thumbnail) = MakeRow(entry, store);
        _list.Items.Add(item);
        Rows.Add(new Row { Entry = entry, Container = item, Thumbnail = thumbnail });

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

    private static (ListBoxItem Item, Image? Thumbnail) MakeRow(ClipboardIndexEntry entry, ClipboardStore store)
    {
        // 这里**绝对不要**再放"选中行强调竖条"之类的固定宽度元素：
        // 默认 HorizontalAlignment=Stretch 时，带固定 Width 的元素会在它所在的 Auto 列里被
        // 水平居中，而 Auto 列的宽度是"整条文本的期望宽度"，于是 3px 的条子会跑到行中间，
        // 看起来就是一条莫名其妙的绿线（用户已经反馈过一次"不知道这是什么、很影响"）。
        // 选中反馈交给宿主 ListBoxItem 自带的行高亮 + 下面 UpdateAccents 的字重变化。
        var preview = new TextBlock
        {
            Text = entry.Preview,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };

        if (entry.IsTodoArchived)
        {
            preview.TextDecorations = TextDecorations.Strikethrough;
            preview.Opacity = 0.55;
        }

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

        // 行首标记：★ 只作展示；☐ 是可点的小热区（点击 = 归档 / 取消归档）
        var markers = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top
        };

        if (entry.IsFavorite)
        {
            markers.Children.Add(new TextBlock
            {
                Text = "★",
                FontSize = 13,
                Margin = new Thickness(0, 0, 4, 0),
                Foreground = _accentBrush
            });
        }

        if (entry.IsTodo)
        {
            markers.Children.Add(MakeTodoBox(entry, store));
        }

        // 列布局：0 = 行首标记（没标记就不占列）｜1 = 图片缩略图｜末列（星号）= 文字。
        // 文字必须待在星号列，TextTrimming 才会按可用宽度截断；早先把 rowLayout 直接塞进
        // Auto 列（Grid.SetColumn 设的是它的子元素，不起作用），文本于是被撑到"整条文本的
        // 期望宽度"再裁掉，图片行的缩略图还会被挤到文字右边。
        var grid = new Grid();

        if (markers.Children.Count > 0)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(markers, grid.ColumnDefinitions.Count - 1);
            grid.Children.Add(markers);
        }

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
            Grid.SetColumn(thumbnail, grid.ColumnDefinitions.Count - 1);
            grid.Children.Add(thumbnail);
        }

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(textColumn, grid.ColumnDefinitions.Count - 1);
        grid.Children.Add(textColumn);

        // 刚归档的那一行：文字淡下去作为反馈（不再画横杠 —— 那也是一条绿线）
        if (_justArchivedId == entry.Id)
        {
            _justArchivedId = 0;
            var fade = new System.Windows.Media.Animation.DoubleAnimation(
                1.0, 0.55, TimeSpan.FromMilliseconds(240));
            preview.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        var item = new ListBoxItem
        {
            Content = grid,
            Padding = new Thickness(8, 6, 8, 6),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Tag = entry
        };

        AttachHoverFlyout(item, entry, store);
        item.ContextMenu = BuildRowMenu(entry, store);

        return (item, thumbnail);
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
    /// 只动"旧选中"和"新选中"两行：选中反馈 = 宿主自带的行高亮 + 字重 SemiBold。
    /// **不再画任何强调色元素**（那条 3px 竖条会被布局居中到行中间，正是用户反馈的绿线）。
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
            _highlighted.FontWeight = FontWeights.Normal;
        }

        if (selected is not null)
        {
            selected.FontWeight = FontWeights.SemiBold;
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

        if (_notifyButton is not null)
        {
            _notifyButton.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(CloseButtonTemplateXaml);
            _notifyButton.Foreground = textBrush;
            _notifyButton.Background = surface;
        }

        // 主题采样发生在 Loaded 之后（要等宿主把搜索框画出来才能采到真实配色），
        // 晚于首次 RebuildChips，所以这里再建一次 chip：深色主题下 chip 文字才会是亮色。
        if (ClipboardHistoryPlugin.Store is { } store && _accentBrush is SolidColorBrush accentBrush)
        {
            RebuildChips(store, accentBrush.Color);
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

    /// <summary>
    /// 通知记录：浮窗 8 秒就消失，这里让用户事后还能看到"刚才提醒了什么"
    /// （用户明确反馈："如果他消失了，我没有任何地方能看到他的通知"）。
    /// </summary>
    private static void OpenNotificationLog(ClipboardStore store)
    {
        CloseNotificationLog();
        CloseTimeline();
        ClipboardWhenPicker.Close();

        var all = ClipboardNotificationLog.Snapshot();
        var today = DateTime.Today;
        var entries = _notifyTodayOnly
            ? all.Where(item => new DateTime(item.At).Date == today).ToList()
            : all;

        var root = new StackPanel { MinWidth = 340 };

        // 今日 / 全部 切换（默认今日，一眼看到今天有多少条）
        var scopeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 2, 8, 4)
        };

        scopeRow.Children.Add(ScopeChip("今日 " + all.Count(i => new DateTime(i.At).Date == today), _notifyTodayOnly, () =>
        {
            _notifyTodayOnly = true;
            CloseNotificationLog();
            OpenNotificationLog(store);
        }));

        scopeRow.Children.Add(ScopeChip("全部 " + all.Count, !_notifyTodayOnly, () =>
        {
            _notifyTodayOnly = false;
            CloseNotificationLog();
            OpenNotificationLog(store);
        }));

        root.Children.Add(scopeRow);

        root.Children.Add(new TextBlock
        {
            Text = (_notifyTodayOnly ? "今日通知" : "全部通知") + "（" + entries.Count + " 条）",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Opacity = 0.6,
            Margin = new Thickness(10, 2, 10, 6),
            Foreground = ThemeText
        });

        if (entries.Count == 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = "暂无通知。待办提醒或置顶到期后会记录在这里。",
                FontSize = 12,
                Opacity = 0.6,
                Margin = new Thickness(10, 0, 10, 8),
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeText
            });
        }

        foreach (var item in entries)
        {
            var at = new DateTime(item.At);
            var label = (item.Kind == (int)ClipboardDueKind.Remind ? "待办提醒" : "置顶到期")
                + " · " + at.ToString("MM-dd HH:mm");

            var row = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(2, 1, 2, 1),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                Opacity = 0.6,
                Foreground = ThemeText
            });
            stack.Children.Add(new TextBlock
            {
                Text = item.Preview,
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ThemeText
            });

            row.Child = stack;
            row.MouseEnter += (_, _) => row.Background = ThemeHover;
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;

            var entryId = item.EntryId;
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                CloseNotificationLog();
                SelectEntryById(entryId, store);
            };

            root.Children.Add(row);
        }

        var surface = new Border
        {
            Background = ThemeSurface,
            BorderBrush = ThemeBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(5),
            Child = new ScrollViewer
            {
                MaxHeight = 380,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = root
            }
        };

        _notifyPopup = new Popup
        {
            PlacementTarget = (FrameworkElement?)_notifyButton ?? (FrameworkElement?)_list ?? _window!,
            Placement = PlacementMode.Top,
            VerticalOffset = -6,
            StaysOpen = true,
            AllowsTransparency = true,
            Child = surface
        };

        _notifyPopup.IsOpen = true;
    }

    /// <summary>通知面板的"今日 / 全部"切换 chip。</summary>
    private static UIElement ScopeChip(string label, bool active, Action onClick)
    {
        var chip = new Border
        {
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 0),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = active ? _accentBrush : Brushes.Transparent,
            Background = active ? ThemeSelected : Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = label,
                FontSize = 12,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = active ? _accentBrush : ThemeText
            }
        };

        chip.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };

        return chip;
    }

    private static void CloseNotificationLog()
    {
        if (_notifyPopup is not null)
        {
            _notifyPopup.IsOpen = false;
        }

        _notifyPopup = null;
    }

    /// <summary>选中指定条目；当前筛选看不到它时先清空筛选再选中。</summary>
    private static void SelectEntryById(long entryId, ClipboardStore store)
    {
        if (SelectEntryCore(entryId))
        {
            return;
        }

        _markFilter = MarkFilter.None;
        _typeFilter = ClipType.All;
        _timeFilterFrom = null;
        _timeFilterTo = null;
        _timeFilterLabel = null;
        _sourceFilter = null;
        Refresh(store);
        SelectEntryCore(entryId);
    }

    private static bool SelectEntryCore(long entryId)
    {
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

            return true;
        }

        return false;
    }

    /// <summary>
    /// 待办的小方框：**可点击**，但热区只包住字形本身（比整行小得多，不会误触）。
    /// 点击 = 归档（打勾），再点 = 取消归档 —— 与用户确认的"打勾等于自动归档"一致。
    /// </summary>
    private static UIElement MakeTodoBox(ClipboardIndexEntry entry, ClipboardStore store)
    {
        var box = new Border
        {
            Padding = new Thickness(1, 0, 4, 0), // 热区只比字形大一点点
            Margin = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = entry.IsTodoArchived ? "✓" : "☐",
                FontSize = 13,
                Foreground = _accentBrush
            }
        };

        box.MouseEnter += (_, _) => box.Background = ThemeHover;
        box.MouseLeave += (_, _) => box.Background = Brushes.Transparent;
        box.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true; // 关键：不让点击冒泡成"选中/粘贴"
            _justArchivedId = entry.Id;
            store.ToggleTodoArchive(entry.Id);
            ClipboardHistoryPlugin.Reminders?.Arm();
            Refresh(store);
        };

        return box;
    }

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

        // 直接按 Rows 索引移动，天然跳过分组标题；当前选中不是条目行（比如选了分组头）
        // 时按 -1 处理，这样 ↓ 落到第一条、↑ 停在第一条，而不是从第二条开始
        var current = -1;
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

    /// <summary>打开时间选择器（置顶时限 / 提醒时间），锚定在该条目行上。</summary>
    private static void OpenWhenPicker(ClipboardIndexEntry entry, ClipboardStore store, bool reminder)
    {
        FrameworkElement anchor = (FrameworkElement?)_list ?? _window!;
        foreach (var row in Rows)
        {
            if (row.Entry.Id == entry.Id)
            {
                anchor = row.Container;
                break;
            }
        }

        var current = reminder ? entry.RemindAt : entry.PinnedUntil;

        // 置顶场景补一个"永久"选项：否则空值只能表示"清除"，没有表达"永久置顶"的方式
        // （用 DateTime.MaxValue 作哨兵，与"清除 = null"区分开）
        var extras = reminder
            ? null
            : new (string, Func<DateTime?>)[]
            {
                ("永久置顶（无期限）", () => DateTime.MaxValue)
            };

        ClipboardWhenPicker.Open(
            anchor,
            reminder ? "什么时候提醒？" : "置顶到什么时候？",
            reminder ? "取消提醒" : "取消置顶",
            current,
            value =>
            {
                if (reminder)
                {
                    ApplyReminder(store, entry, value);
                }
                else if (value is null)
                {
                    ApplyUnpin(store, entry);
                }
                else
                {
                    ApplyPin(store, entry, value == DateTime.MaxValue ? null : value);
                }
            },
            extras);
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

        if (entry.IsTodo)
        {
            // 归档 ≠ 删除：归档只是从"进行中"收起，内容与记录保留，可随时取消归档
            menu.Items.Add(NewMenuItem(entry.IsTodoArchived ? "取消归档" : "归档（完成）", () =>
            {
                store.ToggleTodoArchive(entry.Id);
                ClipboardHistoryPlugin.Reminders?.Arm();
                Refresh(store);
            }));
        }

        // 置顶与提醒共用一个自绘时间选择器（菜单三级子菜单在鼠标移出二级项时会收起，点不到）
        menu.Items.Add(NewMenuItem(entry.IsPinned ? "修改置顶时限…" : "置顶…", () =>
            OpenWhenPicker(entry, store, reminder: false)));

        if (entry.IsPinned)
        {
            menu.Items.Add(NewMenuItem("取消置顶", () => ApplyUnpin(store, entry)));
        }

        menu.Items.Add(NewMenuItem(entry.RemindAt is null ? "设置提醒…" : "修改提醒时间…", () =>
            OpenWhenPicker(entry, store, reminder: true)));

        if (entry.RemindAt is not null)
        {
            menu.Items.Add(NewMenuItem("取消提醒", () => ApplyReminder(store, entry, null)));
        }

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

    /// <summary>提醒/到期导致数据变化时刷新已打开的面板，并同步"通知 (N)"计数。</summary>
    internal static void NotifyStoreChanged(ClipboardStore store)
    {
        if (_notifyButton is not null)
        {
            _notifyButton.Content = "通知 (今日 " + ClipboardNotificationLog.TodayCount
                + " / 共 " + ClipboardNotificationLog.Count + ")";
        }

        if (_list is not null && _window is { IsVisible: true })
        {
            Refresh(store);
        }
    }

    /// <summary>自检用：库里被标记（收藏或待办）的条目数。</summary>
    private static int CountMarked(ClipboardStore store)
    {
        var count = 0;
        foreach (var entry in store.Snapshot())
        {
            if (entry.IsFavorite || entry.IsTodo)
            {
                count++;
            }
        }

        return count;
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
