using Lertaro.PluginSdk.Abstractions;

namespace Lertaro.Plugins.ClipboardHistory;

/// <summary>
/// 配置面板的字段定义。
/// 宿主按 GetValue/SetValue 渲染表单：改的是 <paramref name="staged"/> 副本，
/// 点"应用"触发 OnSave 才写盘，关闭触发 OnRollback 丢弃 —— 改错了不会留痕。
/// </summary>
internal static class ClipboardConfigSchema
{
    internal const string GroupKey = "ClipboardHistory_Config_Group_General";

    internal const string KeyHotkey = "Hotkey";
    internal const string KeyKeywords = "TriggerKeywords";
    internal const string KeyMaxEntries = "MaxEntries";
    internal const string KeyRetentionDays = "RetentionDays";
    internal const string KeyPause = "PauseCapture";
    internal const string KeyShowSource = "ShowSourceProcess";
    internal const string KeyCaptureImages = "CaptureImages";
    internal const string KeyMaxImageMB = "MaxImageMB";
    internal const string KeyPersistHistory = "PersistHistory";
    internal const string KeyHoverPreview = "HoverPreview";
    internal const string KeyReminderSound = "ReminderSound";
    internal const string KeySortTodosByDue = "SortTodosByDue";

    internal static PluginConfigSchema Build(
        ClipboardSettings current,
        ClipboardSettings staged,
        Action onSave,
        Action onRollback)
    {
        return new PluginConfigSchema
        {
            Fields =
            [
                NewField(
                    KeyHotkey,
                    "ClipboardHistory_Config_Hotkey_Label",
                    "ClipboardHistory_Config_Hotkey_Desc",
                    // 用文本而不是 Hotkey 控件：宿主的热键控件不接受 Win 组合，
                    // 而我们自己解析（HotkeyBinding），所以让用户直接写 "Win+V" 这样的文本。
                    ConfigFieldType.Text,
                    current.Hotkey,
                    () => staged.Hotkey,
                    value => staged.Hotkey = value?.ToString() ?? string.Empty),

                NewField(
                    KeyKeywords,
                    "ClipboardHistory_Config_Keywords_Label",
                    "ClipboardHistory_Config_Keywords_Desc",
                    ConfigFieldType.StringList,
                    current.TriggerKeywords,
                    () => staged.TriggerKeywords,
                    value => staged.TriggerKeywords = value as List<string> ?? ClipboardSettings.NormalizeKeywords(value as IEnumerable<string>)),

                NewField(
                    KeyMaxEntries,
                    "ClipboardHistory_Config_MaxEntries_Label",
                    "ClipboardHistory_Config_MaxEntries_Desc",
                    ConfigFieldType.Integer,
                    current.MaxEntries,
                    () => staged.MaxEntries,
                    value => staged.MaxEntries = ToInt(value, staged.MaxEntries)),

                NewField(
                    KeyRetentionDays,
                    "ClipboardHistory_Config_RetentionDays_Label",
                    "ClipboardHistory_Config_RetentionDays_Desc",
                    ConfigFieldType.Integer,
                    current.RetentionDays,
                    () => staged.RetentionDays,
                    value => staged.RetentionDays = ToInt(value, staged.RetentionDays)),

                NewField(
                    KeyPause,
                    "ClipboardHistory_Config_Pause_Label",
                    "ClipboardHistory_Config_Pause_Desc",
                    ConfigFieldType.Boolean,
                    current.PauseCapture,
                    () => staged.PauseCapture,
                    value => staged.PauseCapture = value is bool b && b),

                NewField(
                    KeyShowSource,
                    "ClipboardHistory_Config_ShowSource_Label",
                    "ClipboardHistory_Config_ShowSource_Desc",
                    ConfigFieldType.Boolean,
                    current.ShowSourceProcess,
                    () => staged.ShowSourceProcess,
                    value => staged.ShowSourceProcess = value is bool b && b),

                NewField(
                    KeyCaptureImages,
                    "ClipboardHistory_Config_CaptureImages_Label",
                    "ClipboardHistory_Config_CaptureImages_Desc",
                    ConfigFieldType.Boolean,
                    current.CaptureImages,
                    () => staged.CaptureImages,
                    value => staged.CaptureImages = value is bool b && b),

                NewField(
                    KeyMaxImageMB,
                    "ClipboardHistory_Config_MaxImageMB_Label",
                    "ClipboardHistory_Config_MaxImageMB_Desc",
                    ConfigFieldType.Integer,
                    current.MaxImageMB,
                    () => staged.MaxImageMB,
                    value => staged.MaxImageMB = ToInt(value, staged.MaxImageMB)),

                NewField(
                    KeyPersistHistory,
                    "ClipboardHistory_Config_PersistHistory_Label",
                    "ClipboardHistory_Config_PersistHistory_Desc",
                    ConfigFieldType.Boolean,
                    current.PersistHistory,
                    () => staged.PersistHistory,
                    value => staged.PersistHistory = value is bool b && b),

                NewField(
                    KeyHoverPreview,
                    "ClipboardHistory_Config_HoverPreview_Label",
                    "ClipboardHistory_Config_HoverPreview_Desc",
                    ConfigFieldType.Boolean,
                    current.HoverPreview,
                    () => staged.HoverPreview,
                    value => staged.HoverPreview = value is bool b && b),

                NewField(
                    KeyReminderSound,
                    "ClipboardHistory_Config_ReminderSound_Label",
                    "ClipboardHistory_Config_ReminderSound_Desc",
                    ConfigFieldType.Boolean,
                    current.ReminderSound,
                    () => staged.ReminderSound,
                    value => staged.ReminderSound = value is bool b && b),

                NewField(
                    KeySortTodosByDue,
                    "ClipboardHistory_Config_SortTodosByDue_Label",
                    "ClipboardHistory_Config_SortTodosByDue_Desc",
                    ConfigFieldType.Boolean,
                    current.SortTodosByDue,
                    () => staged.SortTodosByDue,
                    value => staged.SortTodosByDue = value is bool b && b)
            ],
            OnSave = onSave,
            OnRollback = onRollback
        };
    }

    private static PluginConfigField NewField(
        string key,
        string labelKey,
        string descriptionKey,
        ConfigFieldType type,
        object defaultValue,
        Func<object?> getValue,
        Action<object?> setValue)
    {
        return new PluginConfigField
        {
            Key = key,
            GroupKey = GroupKey,
            LabelKey = labelKey,
            DescriptionKey = descriptionKey,
            FieldType = type,
            DefaultValue = defaultValue,
            GetValue = getValue,
            SetValue = setValue
        };
    }

    private static int ToInt(object? value, int fallback)
    {
        return value switch
        {
            int i => i,
            long l => (int)Math.Clamp(l, int.MinValue, int.MaxValue),
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => fallback
        };
    }
}
