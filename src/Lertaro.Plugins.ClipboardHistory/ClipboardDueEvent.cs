namespace Lertaro.Plugins.ClipboardHistory;

/// <summary>到期事件类型。</summary>
internal enum ClipboardDueKind
{
    /// <summary>待办提醒到点。</summary>
    Remind,

    /// <summary>置顶已到期（条目回到普通分组）。</summary>
    PinExpired
}

/// <summary>
/// 一条到期的通知事件。由 <see cref="ClipboardStore.CollectDue"/> 产出，
/// 交给通知层展示（浮窗 + 提示音），点击可回到对应条目。
/// </summary>
internal sealed record ClipboardDueEvent(ClipboardDueKind Kind, long EntryId, string Preview, ClipboardEntryKind EntryKind);
