using Lertaro.PluginSdk;
using Lertaro.PluginSdk.Abstractions;
using Lertaro.PluginSdk.Abstractions.Plugins;
using Lertaro.PluginSdk.Models;
using Lertaro.PluginSdk.Services;

namespace Lertaro.Plugins.Crosshair;

/// <summary>
/// 屏幕准星插件：在屏幕中央叠加自定义准星（颜色/描边/中心点/内部线条/外部线条），
/// 全屏、置顶、点击穿透。参数体系 1:1 对齐 okiaimx.com 的准星编辑器（即 VALORANT 的准星参数），
/// 支持 VALORANT 准星代码的导入与导出，全部可在设置面板实时修改。
///
/// 对照开发指南的三条约定：
/// ① 构造函数只做「非阻塞」的事：读一次设置（预热）+ 调度 UI，绝不等待；
/// ② 全局热键与全屏叠加窗属于"高成本运行时"，先查组件级开关
///    （<see cref="PluginSettingsService.IsComponentEnabled"/>）并在用户切换后启停；
/// ③ 热路径（打字触发的即时结果、渲染动作菜单读 Keywords）不读盘、不反复分配。
/// </summary>
public sealed class CrosshairPlugin : IPlugin, IInstantResultProvider, IActionProvider, IConfigurable, ITranslationProvider
{
    private static CrosshairSettings? _staged;

    private static readonly CrosshairToggleAction ToggleAction = new();
    private static readonly CrosshairEditAction EditAction = new();
    private static readonly CrosshairShowAction ShowAction = new();
    private static readonly CrosshairHideAction HideAction = new();

    public string Name => CrosshairText.Get("CrosshairPlugin_Name", "屏幕准星");

    public string Description => CrosshairText.Get(
        "CrosshairPlugin_Description",
        "屏幕中央显示自定义准星（颜色/描边/中心点/内外线，参数与 okiaimx、VALORANT 一致，支持 VALORANT 代码导入导出），点击穿透不影响游戏；输入 准星 或 cross 查看。");

    public string WebsiteUrl => string.Empty;

    public string WebsiteLabel => string.Empty;

    public CrosshairPlugin()
    {
        // 预热：把设置读进内存（含一次 JSON 反序列化）。放在这里可以避免"用户敲下第一个字时才读盘"，
        // 因为 GetInstantResults / Keywords 都在同步热路径上被调用。
        _ = CrosshairSettings.Current;

        // 用户在「设置 → 插件」里切换本组件开关时，宿主会广播，我们据此启停热键与叠加窗。
        try
        {
            PluginSettingsService.ComponentEnablementChanged += ApplyRuntime;
        }
        catch
        {
            // 宿主未挂载该服务：按启用处理
        }

        // 构造必须极快：只调度 UI 初始化，不做任何等待
        ApplyRuntime();
    }

    /// <summary>
    /// 组件级开关（宿主在「设置 → 插件」里单独禁用某个组件时返回 false）。
    /// 与剪贴板插件同一套判据；宿主未注册回调时按启用处理。
    /// </summary>
    private static bool IsComponentEnabled()
    {
        try
        {
            return PluginSettingsService.IsComponentEnabled(
                "Lertaro.Plugins.Crosshair.dll",
                nameof(IInstantResultProvider),
                nameof(CrosshairPlugin));
        }
        catch
        {
            return true;
        }
    }

    /// <summary>按组件开关启动或停止「热键 + 叠加窗」这套高成本运行时。</summary>
    private static void ApplyRuntime()
    {
        if (!IsComponentEnabled())
        {
            CrosshairText.Log("component disabled in host settings, overlay and hotkey stopped", LogLevel.Info);
            StopRuntime();
            return;
        }

        ApplyVisibilityAsync();
        ApplyHotkeyAsync();
    }

    private static void StopRuntime()
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            CrosshairHotkey.Apply(string.Empty);
            return;
        }

        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(new Action(StopRuntime));
            return;
        }

        CrosshairOverlay.HideOverlay();
        CrosshairHotkey.Apply(string.Empty); // 空字符串 = 注销热键
    }

    private static void ApplyHotkeyAsync()
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return;
        }

        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(new Action(() => CrosshairHotkey.Apply(CrosshairSettings.Current.Hotkey)));
            return;
        }

        CrosshairHotkey.Apply(CrosshairSettings.Current.Hotkey);
    }

    /// <summary>编辑器/设置面板保存后调用：显示状态、外观、热键全部实时生效。</summary>
    internal static void ApplySettingsLive()
    {
        ApplyRuntime();
        CrosshairOverlay.Refresh();
    }

    private static void ApplyVisibilityAsync()
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return; // 宿主 UI 未就绪：等首次切换/设置应用时再建窗口
        }

        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(new Action(ApplyVisibility));
            return;
        }

        ApplyVisibility();
    }

    private static System.Windows.Threading.DispatcherTimer? _visibilityRetry;
    private static int _visibilityRetries;

    private static void ApplyVisibility()
    {
        if (!CrosshairSettings.Current.Visible)
        {
            CrosshairOverlay.HideOverlay();
            return;
        }

        // 关键：叠加窗也是 WPF Window，而 WPF 会把 Application.MainWindow 指向"第一个被实例化的
        // Window"。插件在宿主自己的窗口之前加载，所以必须等宿主已经有窗口之后再建我们的窗口 ——
        // 否则宿主"显示/激活快速窗"的逻辑会拿到错的 MainWindow（实测：宿主精简搜索窗唤不醒，
        // 而完整面板正常；卸载本插件即恢复）。
        if (!IsHostWindowReady())
        {
            ScheduleVisibilityRetry();
            return;
        }

        _visibilityRetries = 0;
        CrosshairOverlay.ShowOverlay();
    }

    private static bool IsHostWindowReady()
    {
        var app = System.Windows.Application.Current;
        return app is not null && app.Windows.Count > 0;
    }

    /// <summary>宿主窗口尚未就绪时稍后重试（500ms 一次，最多 30 次 ≈ 15 秒）。</summary>
    private static void ScheduleVisibilityRetry()
    {
        var app = System.Windows.Application.Current;
        if (app is null || _visibilityRetry is not null || _visibilityRetries >= 30)
        {
            return;
        }

        _visibilityRetries++;
        _visibilityRetry = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(500),
            System.Windows.Threading.DispatcherPriority.Background,
            (_, _) =>
            {
                _visibilityRetry?.Stop();
                _visibilityRetry = null;
                ApplyVisibility();
            },
            app.Dispatcher);
        _visibilityRetry.Start();
    }

    /// <summary>切换显示状态（动作菜单 / 触发结果入口）。写回设置以便重启后保持。</summary>
    internal static void Toggle(string source) => SetVisible(!CrosshairSettings.Current.Visible, source);

    /// <summary>
    /// 显式打开 / 关闭准星（<c>zx k</c> 与 <c>zx g</c> 子命令、动作菜单都走这里）。
    /// 幂等：已经是目标状态时也会重新应用一次，避免"设置说开着、窗口其实没建"的不一致。
    /// </summary>
    internal static void SetVisible(bool visible, string source)
    {
        var settings = CrosshairSettings.Current;
        if (settings.Visible != visible)
        {
            settings.Visible = visible;
            settings.Save();
        }

        // 用户实测发现的问题：内线/外线/中心点三个"显示"全不勾时，总开关切到开
        // 也得不到可见产物（画布是空的），热键看起来像失灵。
        // 打开时若准星是空白的，自动恢复内线 —— 想整体隐藏请用"显示准星"总开关。
        if (visible && IsBlank(settings))
        {
            settings.Primary.Inner.Enabled = true;
            settings.Save();
            CrosshairText.Log("crosshair was blank (inner/outer/dot all off), inner lines restored", LogLevel.Info);
        }

        ApplyRuntime();
        CrosshairText.Log($"crosshair {(visible ? "shown" : "hidden")} via {source}", LogLevel.Info);
    }

    /// <summary>内线/外线/中心点全部禁用 = 打开了也什么都看不见。</summary>
    private static bool IsBlank(CrosshairSettings s) =>
        !s.Primary.Inner.Enabled && !s.Primary.Outer.Enabled && !s.Primary.Dot.Enabled;

    // ---- IInstantResultProvider：触发词状态提示 + 子命令 ----

    public IEnumerable<InstantResultItem> GetInstantResults(string query)
    {
        var text = query?.Trim() ?? string.Empty;
        if (!CrosshairCommands.TryMatch(text, CrosshairSettings.Current.TriggerKeywords, out var keyword, out var argument))
        {
            return [];
        }

        var visible = CrosshairSettings.Current.Visible;

        if (CrosshairCommands.IsShow(argument))
        {
            return
            [
                new InstantResultItem
                {
                    Title = visible ? "屏幕准星：已经打开" : "屏幕准星：打开",
                    Description = $"回车执行「{keyword} k」：显示屏幕中央的准星（全屏置顶、点击穿透）",
                    ActionType = "None",
                    OnExecute = () => SetVisible(true, "instant:show")
                }
            ];
        }

        if (CrosshairCommands.IsHide(argument))
        {
            return
            [
                new InstantResultItem
                {
                    Title = visible ? "屏幕准星：关闭" : "屏幕准星：已经是关闭状态",
                    Description = $"回车执行「{keyword} g」：隐藏屏幕中央的准星",
                    ActionType = "None",
                    OnExecute = () => SetVisible(false, "instant:hide")
                }
            ];
        }

        // 只打了触发词（或跟了不认识的尾巴）：报状态 + 提示子命令
        return
        [
            new InstantResultItem
            {
                Title = visible ? "屏幕准星：显示中" : "屏幕准星：已隐藏",
                Description = $"回车切换显示；{keyword} k 打开、{keyword} g 关闭（参数在准星编辑器里调）",
                ActionType = "None",
                TabCompletion = keyword + " k"
            }
        ];
    }

    public bool[]? GetHighlightMask(string text, string query) => null;

    // ---- IActionProvider ----

    public IEnumerable<ISearchResultAction> GetActions() => [ToggleAction, EditAction, ShowAction, HideAction];

    public IEnumerable<IDynamicActionProvider> GetDynamicActionProviders() => [];

    // ---- IConfigurable ----

    public PluginConfigSchema GetConfigSchema()
    {
        var current = CrosshairSettings.Current;
        _staged = current.Clone();
        return CrosshairConfigSchema.Build(current, _staged, ApplyStaged, DiscardStaged);
    }

    private static void ApplyStaged()
    {
        var staged = _staged;
        _staged = null;
        if (staged is null)
        {
            return;
        }

        var current = CrosshairSettings.Current;
        current.ApplyFrom(staged); // 含 Clamp、落盘

        // 实时生效：显示状态、外观与热键立即应用，不用重启宿主
        ApplySettingsLive();
    }

    private static void DiscardStaged()
    {
        _staged = null;
    }

    // ---- ITranslationProvider ----

    private IReadOnlyList<string>? _supportedCultures;

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
                _supportedCultures = TranslationService.GetSupportedCultures(typeof(CrosshairPlugin).Assembly);
            }
            catch
            {
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
                typeof(CrosshairPlugin).Assembly,
                cultureName,
                nameof(CrosshairPlugin));
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }
}
