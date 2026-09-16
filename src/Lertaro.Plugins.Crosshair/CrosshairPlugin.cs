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
/// </summary>
public sealed class CrosshairPlugin : IPlugin, IInstantResultProvider, IActionProvider, IConfigurable, ITranslationProvider
{
    private static CrosshairSettings? _staged;

    private static readonly CrosshairToggleAction ToggleAction = new();
    private static readonly CrosshairEditAction EditAction = new();
    private static readonly CrosshairShowAction ShowAction = new();
    private static readonly CrosshairHideAction HideAction = new();

    public string Name => "屏幕准星";

    public string Description => "屏幕中央显示自定义准星（颜色/描边/中心点/内外线，参数与 okiaimx、VALORANT 一致，支持 VALORANT 代码导入导出），点击穿透不影响游戏；输入 准星 或 cross 查看。";

    public string WebsiteUrl => string.Empty;

    public string WebsiteLabel => string.Empty;

    public CrosshairPlugin()
    {
        // 构造必须极快：只调度 UI 初始化，不做任何等待
        ApplyVisibilityAsync();
        ApplyHotkeyAsync();
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

    /// <summary>编辑器保存后调用：显示状态、外观、热键全部实时生效。</summary>
    internal static void ApplySettingsLive()
    {
        ApplyVisibilityAsync();
        CrosshairOverlay.Refresh();
        ApplyHotkeyAsync();
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

    private static void ApplyVisibility()
    {
        if (CrosshairSettings.Current.Visible)
        {
            CrosshairOverlay.ShowOverlay();
        }
        else
        {
            CrosshairOverlay.HideOverlay();
        }
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
        }

        ApplyVisibilityAsync();
        System.Diagnostics.Debug.WriteLine($"crosshair {visible} via {source}");
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
