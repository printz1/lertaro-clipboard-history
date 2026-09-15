using System.Windows.Media;
using Lertaro.PluginSdk.Abstractions;
using Lertaro.PluginSdk.Abstractions.Plugins;

namespace Lertaro.Plugins.ClipboardHistory;

/// <summary>
/// 插件对外的静态动作：打开剪贴板历史。
/// 这个类型本身不是入口 —— 必须由实现了 <see cref="IActionProvider"/> 的组件
/// 通过 GetActions() 交出去，宿主才会读它的 Hotkey 并注册全局热键。
/// （早期版本把它直接实现在插件类上，宿主发现不了，所以按了没反应。）
/// </summary>
internal sealed class ClipboardOpenAction : ISearchResultAction
{
    public string Name => "剪贴板历史";

    public string Description => "打开剪贴板历史窗口（预填触发词）";

    public string GroupName => "剪贴板";

    public string DisplayName => "打开剪贴板历史（" + ClipboardSettings.Current.Hotkey + "）";

    /// <summary>
    /// 故意留空：全局热键由插件自己用 RegisterHotKey 注册（见 ClipboardListener），
    /// 这样 "Win+V" 这种宿主热键控件未必认识、宿主未必愿意注册组合也能用。
    /// 若这里也报一个键，宿主和插件会互相抢注册（其中一个会拿到 1409 而失效）。
    /// </summary>
    public string Hotkey => string.Empty;

    public IReadOnlyList<string> Keywords => [];

    public IReadOnlyList<string> Parameters => [];

    public ImageSource Icon => ClipboardHotkeyAction.Icon;

    /// <summary>不参与搜索结果列表，只作为热键与动作菜单项存在。</summary>
    public bool IsVisibleInSearch(IReadOnlyList<ISearchResult> results, SearchWindowType windowType) => false;

    public bool IsVisibleInMenu(IReadOnlyList<ISearchResult> results, SearchWindowType windowType) => true;

    public bool CanExecute(IReadOnlyList<ISearchResult> results) => true;

    public void Execute(IReadOnlyList<ISearchResult> results, IPluginSearchWindow view)
        => ClipboardHotkeyAction.Invoke("action menu");
}
