using System.Windows.Media;
using Lertaro.PluginSdk;
using Lertaro.PluginSdk.Abstractions;
using Lertaro.PluginSdk.Abstractions.Plugins;
using Lertaro.PluginSdk.Models;

namespace Lertaro.Plugins.Crosshair;

/// <summary>动作菜单项：切换准星显示。默认热键留空（按开发指南用可空类型表示"无默认热键"）。</summary>
internal sealed class CrosshairToggleAction : ISearchResultAction
{
    public string Name => CrosshairText.Get("Crosshair_Action_Toggle_Name", "屏幕准星");

    public string Description => CrosshairText.Get(
        "Crosshair_Action_Toggle_Desc",
        "显示 / 隐藏屏幕中央的自定义准星（点击穿透，不影响游戏操作）");

    public string GroupName => "准星";

    public string DisplayName => CrosshairSettings.Current.Visible
        ? CrosshairText.Get("Crosshair_Action_Toggle_Hide", "隐藏屏幕准星")
        : CrosshairText.Get("Crosshair_Action_Toggle_Show", "显示屏幕准星");

    public string Hotkey => string.Empty;

    public IReadOnlyList<string> Keywords => CrosshairSettings.Current.TriggerKeywords;

    public IReadOnlyList<string> Parameters => [];

    public ImageSource? Icon => null;

    public bool IsVisibleInSearch(IReadOnlyList<ISearchResult> results, SearchWindowType windowType) => true;

    public bool IsVisibleInMenu(IReadOnlyList<ISearchResult> results, SearchWindowType windowType) => true;

    public bool CanExecute(IReadOnlyList<ISearchResult> results) => true;

    public void Execute(IReadOnlyList<ISearchResult> results, IPluginSearchWindow view)
        => CrosshairPlugin.Toggle("action");
}

/// <summary>动作菜单项：打开可视化准星编辑器（okiaimx / VALORANT 参数 + 代码互导 + 实时预览）。</summary>
internal sealed class CrosshairEditAction : ISearchResultAction
{
    public string Name => CrosshairText.Get("Crosshair_Action_Edit_Name", "准星编辑器");

    public string Description => CrosshairText.Get(
        "Crosshair_Action_Edit_Desc",
        "可视化调整准星：颜色/描边/中心点/内外线拖滑杆、实时预览，还能粘贴 VALORANT 准星代码，保存即生效");

    public string GroupName => "准星";

    public string DisplayName => CrosshairText.Get("Crosshair_Action_Edit_Display", "准星编辑器（可视化调整）");

    public string Hotkey => string.Empty;

    public IReadOnlyList<string> Keywords => ["编辑器", "editor"];

    public IReadOnlyList<string> Parameters => [];

    public ImageSource? Icon => null;

    public bool IsVisibleInSearch(IReadOnlyList<ISearchResult> results, SearchWindowType windowType) => true;

    public bool IsVisibleInMenu(IReadOnlyList<ISearchResult> results, SearchWindowType windowType) => true;

    public bool CanExecute(IReadOnlyList<ISearchResult> results) => true;

    public void Execute(IReadOnlyList<ISearchResult> results, IPluginSearchWindow view)
    {
        // 每次打开新建实例（基于当前设置），保存即应用
        new CrosshairEditor().Show();
    }
}

/// <summary>动作菜单项 / 搜索项：<c>zx k</c> 打开准星（关键词跟随用户的触发词配置）。</summary>
internal sealed class CrosshairShowAction : ISearchResultAction
{
    public string Name => CrosshairText.Get("Crosshair_Action_Show_Name", "打开屏幕准星");

    public string Description => CrosshairText.Get(
        "Crosshair_Action_Show_Desc",
        "立即显示屏幕中央的自定义准星（点击穿透，不影响游戏操作）");

    public string GroupName => "准星";

    public string DisplayName => CrosshairText.Get("Crosshair_Action_Show_Display", "打开屏幕准星");

    public string Hotkey => string.Empty;

    public IReadOnlyList<string> Keywords => CrosshairCommands.ShowKeywords;

    public IReadOnlyList<string> Parameters => [];

    public ImageSource? Icon => null;

    public bool IsVisibleInSearch(IReadOnlyList<ISearchResult> results, SearchWindowType windowType) => true;

    public bool IsVisibleInMenu(IReadOnlyList<ISearchResult> results, SearchWindowType windowType) => true;

    public bool CanExecute(IReadOnlyList<ISearchResult> results) => true;

    public void Execute(IReadOnlyList<ISearchResult> results, IPluginSearchWindow view)
        => CrosshairPlugin.SetVisible(true, "action:show");
}

/// <summary>动作菜单项 / 搜索项：<c>zx g</c> 关闭准星。</summary>
internal sealed class CrosshairHideAction : ISearchResultAction
{
    public string Name => CrosshairText.Get("Crosshair_Action_Hide_Name", "关闭屏幕准星");

    public string Description => CrosshairText.Get(
        "Crosshair_Action_Hide_Desc",
        "隐藏屏幕中央的自定义准星");

    public string GroupName => "准星";

    public string DisplayName => CrosshairText.Get("Crosshair_Action_Hide_Display", "关闭屏幕准星");

    public string Hotkey => string.Empty;

    public IReadOnlyList<string> Keywords => CrosshairCommands.HideKeywords;

    public IReadOnlyList<string> Parameters => [];

    public ImageSource? Icon => null;

    public bool IsVisibleInSearch(IReadOnlyList<ISearchResult> results, SearchWindowType windowType) => true;

    public bool IsVisibleInMenu(IReadOnlyList<ISearchResult> results, SearchWindowType windowType) => true;

    public bool CanExecute(IReadOnlyList<ISearchResult> results) => true;

    public void Execute(IReadOnlyList<ISearchResult> results, IPluginSearchWindow view)
        => CrosshairPlugin.SetVisible(false, "action:hide");
}
