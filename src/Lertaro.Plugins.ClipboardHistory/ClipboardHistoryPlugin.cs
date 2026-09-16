using System.IO;
using System.Windows.Media;
using Lertaro.PluginSdk;
using Lertaro.PluginSdk.Abstractions;
using Lertaro.PluginSdk.Abstractions.Plugins;
using Lertaro.PluginSdk.Services;

namespace Lertaro.Plugins.ClipboardHistory;

/// <summary>
/// 剪贴板历史插件入口。
/// 触发词：cb / clip，后面可跟关键词过滤，例如 "clip token"。
/// 回车即把该条内容复制回剪贴板（交给宿主执行，ActionType = Copy）。
/// 全局热键（默认 Ctrl+Shift+V）直接打开搜索窗并预填 cb，等价于 Win+V 的用法。
///
/// 线程约定：本类的所有方法都在宿主 UI 线程上被调用，只读内存索引，
/// 绝不碰剪贴板 —— 剪贴板的读取全在 ClipboardListener 自己的线程上完成。
/// </summary>
public sealed class ClipboardHistoryPlugin : IPlugin, IInstantResultProvider, IActionProvider, IConfigurable, ITranslationProvider
{
    private const int MaxResults = 30;
    private const int FuzzyScanLimit = 1500;

    /// <summary>持久化轮询间隔：内容变化后最迟 5 秒落盘。</summary>
    private const int PersistIntervalMs = 5000;

    private static readonly object InitGate = new();
    private static ClipboardStore? _store;
    private static ClipboardListener? _listener;
    private static ClipboardPoller? _poller;
    private static ClipboardSettings? _stagedSettings;
    private static IReadOnlyList<string>? _supportedCultures;
    private static System.Threading.Timer? _persistTimer;

    /// <summary>供面板读取当前历史（面板是插件的一部分，同进程内直接取）。</summary>
    internal static ClipboardStore? Store => _store;

    private static ClipboardReminderService? _reminders;

    /// <summary>
    /// 提醒调度（精准单次定时器，不做周期扫描）。
    /// 首次访问时创建 —— 需要 WPF Dispatcher 才能弹浮窗，因此等到 Application.Current 就绪。
    /// </summary>
    internal static ClipboardReminderService? Reminders
    {
        get
        {
            if (_reminders is null && _store is not null)
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher is null)
                {
                    return null;
                }

                _reminders = new ClipboardReminderService(_store, dispatcher);
            }

            return _reminders;
        }
    }

    /// <summary>
    /// 启动阶段把定时器对准最近到期时刻。宿主 UI 尚未就绪时（Application.Current 为空）
    /// 隔 2 秒重试，最多 <paramref name="attempts"/> 次；成功或放弃后不再参与运行期。
    /// </summary>
    private static void ArmRemindersWithRetry(int attempts)
    {
        var left = attempts;
        System.Threading.Timer? retry = null;

        retry = new System.Threading.Timer(
            _ =>
            {
                var reminders = Reminders;
                if (reminders is not null)
                {
                    reminders.Arm();
                    ClipboardListener.Log(
                        "reminders armed (next=" + (_store?.NextDeadline()?.ToString("MM-dd HH:mm:ss") ?? "none") + ")",
                        LogLevel.Debug);
                    retry?.Dispose(); // 就绪即停：运行期不再有任何周期性任务
                    return;
                }

                if (--left <= 0)
                {
                    ClipboardListener.Log("reminders not armed: dispatcher unavailable", LogLevel.Warn);
                    retry?.Dispose();
                }
            },
            null,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2));
    }

    public string Name => "剪贴板历史";

    public string Description => "记录复制过的文本、图片与文件，输入 cb 或 clip 调用；文本持久化为 JSON，图片以 PNG 存于数据目录。";

    public string WebsiteUrl => string.Empty;

    public string WebsiteLabel => string.Empty;

    public ClipboardHistoryPlugin()
    {
        EnsureStarted();
    }

    /// <summary>
    /// 宿主在加载阶段可能多次实例化插件，因此后台监听做成进程级单例。
    /// 构造过程必须极快：只建对象和线程，不等待、不读剪贴板、不加载历史。
    /// </summary>
    private static void EnsureStarted()
    {
        lock (InitGate)
        {
            if (_store is not null)
            {
                return;
            }

            var settings = ClipboardSettings.Current;

            // 保留天数 0 表示关闭时效淘汰
            var maxAge = settings.RetentionDays <= 0
                ? TimeSpan.MaxValue
                : TimeSpan.FromDays(settings.RetentionDays);

            var historyPath = Path.Combine(ClipboardSettings.SettingsDirectory, "history.json");

            // 历史持久化（P2）：启动时从快照恢复文本全文与图片引用；
            // 关闭持久化时保持旧行为（重启清空，缓存目录随之清掉）。
            // LoadOrCreate 自己抛异常绝不能带崩插件构造器 —— 退回空库，旧快照留在磁盘上不动。
            try
            {
                _store = settings.PersistHistory
                    ? ClipboardStore.LoadOrCreate(settings.MaxEntries, maxAge, historyPath)
                    : new ClipboardStore(settings.MaxEntries, maxAge);
            }
            catch (Exception ex)
            {
                ClipboardListener.Log("history restore crashed, starting empty: " + ex, LogLevel.Error);
                _store = new ClipboardStore(settings.MaxEntries, maxAge);
            }

            // ctor 期的日志宿主接不住（logger 未接线），恢复结果挂到首次捕获时再报
            var initSummary = (settings.PersistHistory ? "restored from snapshot; " : "persistence off; ")
                + "store#" + _store.InstanceId + " entries=" + _store.Count;

            // 清扫孤儿图片：名单来自恢复后的索引；持久化关闭时名单为空（全清，与旧行为一致）
            System.Threading.Tasks.Task.Run(() => ClipboardImageCache.SweepCache(_store.ReferencedImagePaths()));

            // 内容变化后 5 秒合并落盘，避免每条复制都写几 MB 的 JSON
            if (settings.PersistHistory)
            {
                _persistTimer = new System.Threading.Timer(
                    _ =>
                    {
                        try
                        {
                            var store = _store;
                            if (store is not null
                                && store.Dirty
                                && ClipboardSettings.Current.PersistHistory)
                            {
                                // 空库不覆盖非空快照：万一恢复环节出了岔子（或出现第二份
                                // 插件实例），不能让一份空数据把用户的历史抹掉。
                                if (store.Count == 0 && File.Exists(historyPath))
                                {
                                    ClipboardListener.Log("persist skipped: store is empty but a snapshot exists on disk", LogLevel.Warn);
                                    return;
                                }

                                store.Persist(historyPath);
                                ClipboardListener.Log("history persisted", LogLevel.Debug);
                            }
                        }
                        catch (Exception ex)
                        {
                            ClipboardListener.Log("persist tick failed: " + ex.Message, LogLevel.Warn);
                        }
                    },
                    null,
                    PersistIntervalMs,
                    PersistIntervalMs);
            }

            // 提醒调度启动：等宿主 UI 就绪后对准最近一次到期时刻（有就立即触发，完成"睡过头"补偿）。
            // 只在启动阶段重试几次，正常运行期间没有任何周期扫描。
            ArmRemindersWithRetry(5);

            try
            {
                if (!IsComponentEnabled())
                {
                    ClipboardListener.Log("component disabled by host settings, monitor not started", LogLevel.Info);
                    return;
                }

                var listener = new ClipboardListener(
                    _store,
                    () => ClipboardSettings.Current.PauseCapture,
                    ClipboardSettings.Current.Hotkey,
                    () => ClipboardHotkeyAction.Invoke("global hotkey"),
                    initSummary);

                if (listener.Start(TimeSpan.FromMilliseconds(750)))
                {
                    _listener = listener;
                    ClipboardListener.Log(
                        "event-driven listener started (AddClipboardFormatListener, no polling)",
                        LogLevel.Info);

                    if (!string.IsNullOrWhiteSpace(ClipboardSettings.Current.Hotkey) && !listener.HotkeyRegistered)
                    {
                        ClipboardListener.Log(
                            "hotkey '" + ClipboardSettings.Current.Hotkey + "' is not active; pick another combination in the plugin settings",
                            LogLevel.Warn);
                    }
                }
                else
                {
                    listener.Dispose();
                    _poller = new ClipboardPoller(_store, () => ClipboardSettings.Current.PauseCapture);
                    ClipboardListener.Log("listener unavailable, fell back to sequence-number polling", LogLevel.Warn);
                }
            }
            catch (Exception ex)
            {
                ClipboardListener.Log("failed to start clipboard monitor: " + ex, LogLevel.Error);
            }
        }
    }

    private static bool IsComponentEnabled()
    {
        try
        {
            return PluginSettingsService.IsComponentEnabled(
                "Lertaro.Plugins.ClipboardHistory.dll",
                nameof(IInstantResultProvider),
                nameof(ClipboardHistoryPlugin));
        }
        catch
        {
            // 宿主未注册回调时按启用处理
            return true;
        }
    }

    public IEnumerable<InstantResultItem> GetInstantResults(string query)
    {
        var store = _store;
        if (store is null)
        {
            return [];
        }

        var text = query?.Trim() ?? string.Empty;
        if (!TrySplitTrigger(text, out var filter))
        {
            return [];
        }

        var total = store.Count;
        if (total == 0)
        {
            return
            [
                new InstantResultItem
                {
                    Title = "剪贴板历史为空",
                    Description = "还没抓到内容；复制一段文本后再试",
                    ActionType = "None"
                }
            ];
        }

        var matched = store.Search(
            string.IsNullOrEmpty(filter) ? null : filter,
            MaxResults,
            IsMatch,
            FuzzyScanLimit);

        if (matched.Count == 0)
        {
            return
            [
                new InstantResultItem
                {
                    Title = "没有匹配的记录",
                    Description = "共 " + total + " 条历史，换个关键词试试",
                    ActionType = "None"
                }
            ];
        }

        var results = new List<InstantResultItem>(matched.Count);
        foreach (var entry in matched)
        {
            var fullText = store.TryGetText(entry.Id);
            if (string.IsNullOrEmpty(fullText))
            {
                // 全文已被淘汰：不给出无法复制的空结果
                continue;
            }

            results.Add(new InstantResultItem
            {
                Title = entry.Preview,
                Description = entry.Subtitle,
                ActionType = "Copy",
                ActionArgument = fullText
            });
        }

        return results;
    }

    public bool[]? GetHighlightMask(string text, string query)
    {
        try
        {
            if (!TrySplitTrigger(query?.Trim() ?? string.Empty, out var filter) || string.IsNullOrEmpty(filter))
            {
                return null;
            }

            return FuzzyMatchService.GetHighlightMask(text, filter);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>识别触发词并取出过滤词（触发词可在配置面板里改）。"cb foo" -> filter = "foo"。</summary>
    private static bool TrySplitTrigger(string query, out string filter)
    {
        filter = string.Empty;

        if (query.Length == 0)
        {
            return false;
        }

        foreach (var keyword in ClipboardSettings.Current.TriggerKeywords)
        {
            if (query.Equals(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (query.Length > keyword.Length
                && query.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)
                && char.IsWhiteSpace(query[keyword.Length]))
            {
                filter = query[(keyword.Length + 1)..].Trim();
                return true;
            }
        }

        return false;
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

    // ---- IActionProvider：全局热键 / Ctrl+O 动作菜单 ----
    // 注意：动作必须由 IActionProvider.GetActions() 交出去，宿主才会读它的 Hotkey；
    // 只把 ISearchResultAction 实现在插件类上，宿主发现不了（这就是早期"按了没反应"的原因）。

    private static readonly ClipboardOpenAction OpenAction = new();

    public IEnumerable<ISearchResultAction> GetActions() => [OpenAction];

    public IEnumerable<IDynamicActionProvider> GetDynamicActionProviders() => [];

    // ---- IConfigurable：设置面板 ----

    /// <summary>
    /// 面板打开时给出一份设置副本，GetValue/SetValue 全在这份副本上操作；
    /// 只有 OnSave 才写回并落盘，OnRollback 直接丢弃 —— 改错不会留痕。
    /// </summary>
    public PluginConfigSchema GetConfigSchema()
    {
        var current = ClipboardSettings.Current;
        _stagedSettings = current.Clone();

        return ClipboardConfigSchema.Build(
            current,
            _stagedSettings,
            onSave: ApplyStagedSettings,
            onRollback: () =>
            {
                _stagedSettings = null;
                ClipboardListener.Log("settings changes discarded by user", LogLevel.Debug);
            });
    }

    private static void ApplyStagedSettings()
    {
        var staged = _stagedSettings;
        _stagedSettings = null;

        if (staged is null)
        {
            return;
        }

        var current = ClipboardSettings.Current;
        current.ApplyFrom(staged);

        // 热键立刻重注册，用户不用为了改键而重启宿主
        var applied = _listener?.UpdateHotkey(current.Hotkey) ?? false;

        // 条数上限与保留天数在启动时构造存储实例，需要重启宿主
        ClipboardListener.Log(
            "settings applied -> hotkey=" + current.Hotkey
            + (applied ? " (re-registered live)" : " (will take effect on next start)")
            + ", keywords=" + string.Join("/", current.TriggerKeywords)
            + ", maxEntries=" + current.MaxEntries
            + ", retentionDays=" + current.RetentionDays
            + ", pause=" + current.PauseCapture
            + ", captureImages=" + current.CaptureImages
            + ", maxImageMB=" + current.MaxImageMB
            + ", persistHistory=" + current.PersistHistory
            + ", hoverPreview=" + current.HoverPreview
            + " (capacity and retention take effect after restart)",
            LogLevel.Info);
    }

    // ---- ITranslationProvider：配置面板的多语言 ----

    public IReadOnlyList<string> SupportedCultures
    {
        get
        {
            if (_supportedCultures is not null)
            {
                return _supportedCultures;
            }

            try
            {
                _supportedCultures = TranslationService.GetSupportedCultures(typeof(ClipboardHistoryPlugin).Assembly);
            }
            catch (Exception ex)
            {
                ClipboardListener.Log("culture discovery failed: " + ex.Message, LogLevel.Warn);
                _supportedCultures = ["zh-CN", "en-US"];
            }

            return _supportedCultures;
        }
    }

    public IReadOnlyDictionary<string, string> GetTranslations(string cultureName)
    {
        try
        {
            return TranslationService.LoadEmbeddedTranslations(
                typeof(ClipboardHistoryPlugin).Assembly,
                cultureName,
                nameof(ClipboardHistoryPlugin));
        }
        catch (Exception ex)
        {
            ClipboardListener.Log("translation load failed for " + cultureName + ": " + ex.Message, LogLevel.Warn);
            return new Dictionary<string, string>();
        }
    }
}
