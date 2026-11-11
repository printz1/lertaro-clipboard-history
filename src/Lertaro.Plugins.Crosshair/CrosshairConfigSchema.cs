using System.Globalization;
using Lertaro.PluginSdk;
using Lertaro.PluginSdk.Abstractions;
using Lertaro.PluginSdk.Abstractions.Plugins;
using Lertaro.PluginSdk.Models;

namespace Lertaro.Plugins.Crosshair;

/// <summary>
/// 设置面板 schema：okiaimx / VALORANT 的全部准星参数（宿主通用面板的等价文本字段），
/// 保存即实时重绘。可视化编辑请用「准星编辑器」，这里同时提供 VALORANT 代码栏作为快捷入口。
///
/// <para>
/// 关键约定（踩过坑）：<c>staged</c> 传进来的是<b>取值函数</b>而不是暂存对象实例。
/// 宿主在面板存续期间可能多次调用 <c>GetConfigSchema()</c>，而字段控件的
/// GetValue/SetValue 闭包是它某一次拿到的那些 —— 如果闭包直接持有当时的暂存对象，
/// 而插件在下次 GetConfigSchema 时换了新对象，用户的改动就会落在旧对象上、
/// 保存时读到的却是新对象，表现为"第一次点保存没反应、再点一次才行"。
/// 统一用 <c>S()</c> 动态取当前暂存对象即可彻底避免这种别名错位。
/// </para>
/// </summary>
internal static class CrosshairConfigSchema
{
    private const string GroupKey = "Crosshair";

    /// <summary>
    /// 颜色下拉项（对照开发指南：Value 写入设置、LabelKey 解析为界面文本）。
    /// 索引 0-7 为 VALORANT 预设色，8 = 自定义十六进制。
    /// </summary>
    private static readonly List<PluginConfigChoice> ColorChoices =
    [
        new() { Value = "0", LabelKey = "Crosshair_Color_White" },
        new() { Value = "1", LabelKey = "Crosshair_Color_Green" },
        new() { Value = "2", LabelKey = "Crosshair_Color_YellowGreen" },
        new() { Value = "3", LabelKey = "Crosshair_Color_GreenYellow" },
        new() { Value = "4", LabelKey = "Crosshair_Color_Yellow" },
        new() { Value = "5", LabelKey = "Crosshair_Color_Cyan" },
        new() { Value = "6", LabelKey = "Crosshair_Color_Pink" },
        new() { Value = "7", LabelKey = "Crosshair_Color_Red" },
        new() { Value = CrosshairCodec.CustomColorIndex.ToString(CultureInfo.InvariantCulture), LabelKey = "Crosshair_Color_Custom" }
    ];

    /// <summary>逐次记进 debug.log 的关键字段；其余字段只在保存时汇总差异，免得刷屏。</summary>
    private static readonly HashSet<string> TracedKeys =
    [
        "Visible", "CrosshairCode", "Color", "HexColor", "HexColorEnabled",
        "TriggerKeywords", "Hotkey", "OffsetX", "OffsetY", "Opacity"
    ];

    /// <summary>颜色索引 → 下拉项的稳定字符串值。</summary>
    private static string ColorValues(int index) =>
        Math.Clamp(index, 0, CrosshairCodec.CustomColorIndex).ToString(CultureInfo.InvariantCulture);

    internal static PluginConfigSchema Build(
        CrosshairSettings current,
        Func<CrosshairSettings> staged,
        Action onSave,
        Action onRollback)
    {
        // 每次访问都重新解析"当前暂存对象"，绝不把实例存进闭包。
        CrosshairSettings S() => staged();

        Action<object?> Set(string key, Action<object?> apply) => value =>
        {
            try
            {
                apply(value);
            }
            catch (Exception ex)
            {
                CrosshairDiagnostics.LogException($"SetValue({key})", ex);
                throw;
            }

            if (TracedKeys.Contains(key))
            {
                CrosshairDiagnostics.Log($"SetValue({key}) = {Describe(value)}（暂存 Visible={S().Visible}）");
            }
        };

        return new PluginConfigSchema
        {
            Fields =
            [
                // ---- 总开关与代码 ----
                NewField("Visible", "Crosshair_Config_Visible_Label", "Crosshair_Config_Visible_Desc",
                    ConfigFieldType.Boolean, current.Visible,
                    () => S().Visible,
                    Set("Visible", value => S().Visible = value is bool b && b)),

                NewField("CrosshairCode", "Crosshair_Config_Code_Label", "Crosshair_Config_Code_Desc",
                    ConfigFieldType.Text, current.Code,
                    () => S().Code,
                    Set("CrosshairCode", value => ApplyCode(S(), value?.ToString()))),

                // ---- 颜色 ----
                // 开发指南的 Choice + ChoiceOptions：保存的是稳定值 "0".."8"，界面显示的是本地化标签
                NewField("Color", "Crosshair_Config_Color_Label", "Crosshair_Config_Color_Desc",
                    ConfigFieldType.Choice, ColorValues(current.Primary.Color),
                    () => ColorValues(S().Primary.Color),
                    Set("Color", value => S().Primary.Color = ToInt(value, S().Primary.Color)),
                    choices: ColorChoices),

                NewField("HexColor", "Crosshair_Config_HexColor_Label", "Crosshair_Config_HexColor_Desc",
                    ConfigFieldType.Text, CrosshairCodec.Hex6(current.Primary.HexColor.Value),
                    () => CrosshairCodec.Hex6(S().Primary.HexColor.Value),
                    Set("HexColor", value =>
                    {
                        var hex8 = CrosshairCodec.Hex8(value?.ToString());
                        if (hex8 is not null)
                        {
                            S().Primary.HexColor.Value = hex8;
                        }
                    })),

                NewField("HexColorEnabled", "Crosshair_Config_HexColorEnabled_Label", "Crosshair_Config_HexColorEnabled_Desc",
                    ConfigFieldType.Boolean, current.Primary.HexColor.Enabled,
                    () => S().Primary.HexColor.Enabled,
                    Set("HexColorEnabled", value => S().Primary.HexColor.Enabled = value is bool b && b)),

                // ---- 描边 ----
                NewField("OutlineEnabled", "Crosshair_Config_OutlineEnabled_Label", "Crosshair_Config_OutlineEnabled_Desc",
                    ConfigFieldType.Boolean, current.Primary.Outlines.Enabled,
                    () => S().Primary.Outlines.Enabled,
                    Set("OutlineEnabled", value => S().Primary.Outlines.Enabled = value is bool b && b)),

                NewField("OutlineWidth", "Crosshair_Config_OutlineWidth_Label", "Crosshair_Config_OutlineWidth_Desc",
                    ConfigFieldType.Integer, current.Primary.Outlines.Width,
                    () => S().Primary.Outlines.Width,
                    Set("OutlineWidth", value => S().Primary.Outlines.Width = ToDouble(value, S().Primary.Outlines.Width))),

                NewField("OutlineAlpha", "Crosshair_Config_OutlineAlpha_Label", "Crosshair_Config_OutlineAlpha_Desc",
                    ConfigFieldType.Text, current.Primary.Outlines.Alpha,
                    () => S().Primary.Outlines.Alpha,
                    Set("OutlineAlpha", value => S().Primary.Outlines.Alpha = ToDouble(value, S().Primary.Outlines.Alpha))),

                // ---- 中心点 ----
                NewField("DotEnabled", "Crosshair_Config_DotEnabled_Label", "Crosshair_Config_DotEnabled_Desc",
                    ConfigFieldType.Boolean, current.Primary.Dot.Enabled,
                    () => S().Primary.Dot.Enabled,
                    Set("DotEnabled", value => S().Primary.Dot.Enabled = value is bool b && b)),

                NewField("DotWidth", "Crosshair_Config_DotWidth_Label", "Crosshair_Config_DotWidth_Desc",
                    ConfigFieldType.Integer, current.Primary.Dot.Width,
                    () => S().Primary.Dot.Width,
                    Set("DotWidth", value => S().Primary.Dot.Width = ToDouble(value, S().Primary.Dot.Width))),

                NewField("DotAlpha", "Crosshair_Config_DotAlpha_Label", "Crosshair_Config_DotAlpha_Desc",
                    ConfigFieldType.Text, current.Primary.Dot.Alpha,
                    () => S().Primary.Dot.Alpha,
                    Set("DotAlpha", value => S().Primary.Dot.Alpha = ToDouble(value, S().Primary.Dot.Alpha))),

                // ---- 内部线条 ----
                NewField("InnerEnabled", "Crosshair_Config_InnerEnabled_Label", "Crosshair_Config_InnerEnabled_Desc",
                    ConfigFieldType.Boolean, current.Primary.Inner.Enabled,
                    () => S().Primary.Inner.Enabled,
                    Set("InnerEnabled", value => S().Primary.Inner.Enabled = value is bool b && b)),

                NewField("InnerWidth", "Crosshair_Config_InnerWidth_Label", "Crosshair_Config_InnerWidth_Desc",
                    ConfigFieldType.Integer, current.Primary.Inner.Width,
                    () => S().Primary.Inner.Width,
                    Set("InnerWidth", value => S().Primary.Inner.Width = ToDouble(value, S().Primary.Inner.Width))),

                NewField("InnerLength", "Crosshair_Config_InnerLength_Label", "Crosshair_Config_InnerLength_Desc",
                    ConfigFieldType.Integer, current.Primary.Inner.Length,
                    () => S().Primary.Inner.Length,
                    Set("InnerLength", value => S().Primary.Inner.Length = ToDouble(value, S().Primary.Inner.Length))),

                NewField("InnerOffset", "Crosshair_Config_InnerOffset_Label", "Crosshair_Config_InnerOffset_Desc",
                    ConfigFieldType.Integer, current.Primary.Inner.Offset,
                    () => S().Primary.Inner.Offset,
                    Set("InnerOffset", value => S().Primary.Inner.Offset = ToDouble(value, S().Primary.Inner.Offset))),

                NewField("InnerAlpha", "Crosshair_Config_InnerAlpha_Label", "Crosshair_Config_InnerAlpha_Desc",
                    ConfigFieldType.Text, current.Primary.Inner.Alpha,
                    () => S().Primary.Inner.Alpha,
                    Set("InnerAlpha", value => S().Primary.Inner.Alpha = ToDouble(value, S().Primary.Inner.Alpha))),

                NewField("InnerVerticalEnabled", "Crosshair_Config_InnerVerticalEnabled_Label", "Crosshair_Config_InnerVerticalEnabled_Desc",
                    ConfigFieldType.Boolean, current.Primary.Inner.Vertical.Enabled,
                    () => S().Primary.Inner.Vertical.Enabled,
                    Set("InnerVerticalEnabled", value => S().Primary.Inner.Vertical.Enabled = value is bool b && b)),

                NewField("InnerVerticalLength", "Crosshair_Config_InnerVerticalLength_Label", "Crosshair_Config_InnerVerticalLength_Desc",
                    ConfigFieldType.Integer, current.Primary.Inner.Vertical.Length,
                    () => S().Primary.Inner.Vertical.Length,
                    Set("InnerVerticalLength", value => S().Primary.Inner.Vertical.Length = ToDouble(value, S().Primary.Inner.Vertical.Length))),

                // ---- 外部线条 ----
                NewField("OuterEnabled", "Crosshair_Config_OuterEnabled_Label", "Crosshair_Config_OuterEnabled_Desc",
                    ConfigFieldType.Boolean, current.Primary.Outer.Enabled,
                    () => S().Primary.Outer.Enabled,
                    Set("OuterEnabled", value => S().Primary.Outer.Enabled = value is bool b && b)),

                NewField("OuterWidth", "Crosshair_Config_OuterWidth_Label", "Crosshair_Config_OuterWidth_Desc",
                    ConfigFieldType.Integer, current.Primary.Outer.Width,
                    () => S().Primary.Outer.Width,
                    Set("OuterWidth", value => S().Primary.Outer.Width = ToDouble(value, S().Primary.Outer.Width))),

                NewField("OuterLength", "Crosshair_Config_OuterLength_Label", "Crosshair_Config_OuterLength_Desc",
                    ConfigFieldType.Integer, current.Primary.Outer.Length,
                    () => S().Primary.Outer.Length,
                    Set("OuterLength", value => S().Primary.Outer.Length = ToDouble(value, S().Primary.Outer.Length))),

                NewField("OuterOffset", "Crosshair_Config_OuterOffset_Label", "Crosshair_Config_OuterOffset_Desc",
                    ConfigFieldType.Integer, current.Primary.Outer.Offset,
                    () => S().Primary.Outer.Offset,
                    Set("OuterOffset", value => S().Primary.Outer.Offset = ToDouble(value, S().Primary.Outer.Offset))),

                NewField("OuterAlpha", "Crosshair_Config_OuterAlpha_Label", "Crosshair_Config_OuterAlpha_Desc",
                    ConfigFieldType.Text, current.Primary.Outer.Alpha,
                    () => S().Primary.Outer.Alpha,
                    Set("OuterAlpha", value => S().Primary.Outer.Alpha = ToDouble(value, S().Primary.Outer.Alpha))),

                NewField("OuterVerticalEnabled", "Crosshair_Config_OuterVerticalEnabled_Label", "Crosshair_Config_OuterVerticalEnabled_Desc",
                    ConfigFieldType.Boolean, current.Primary.Outer.Vertical.Enabled,
                    () => S().Primary.Outer.Vertical.Enabled,
                    Set("OuterVerticalEnabled", value => S().Primary.Outer.Vertical.Enabled = value is bool b && b)),

                NewField("OuterVerticalLength", "Crosshair_Config_OuterVerticalLength_Label", "Crosshair_Config_OuterVerticalLength_Desc",
                    ConfigFieldType.Integer, current.Primary.Outer.Vertical.Length,
                    () => S().Primary.Outer.Vertical.Length,
                    Set("OuterVerticalLength", value => S().Primary.Outer.Vertical.Length = ToDouble(value, S().Primary.Outer.Vertical.Length))),

                // ---- 扩展项 ----
                NewField("OffsetX", "Crosshair_Config_OffsetX_Label", "Crosshair_Config_OffsetX_Desc",
                    ConfigFieldType.Integer, current.OffsetX,
                    () => S().OffsetX,
                    Set("OffsetX", value => S().OffsetX = ToInt(value, S().OffsetX))),

                NewField("OffsetY", "Crosshair_Config_OffsetY_Label", "Crosshair_Config_OffsetY_Desc",
                    ConfigFieldType.Integer, current.OffsetY,
                    () => S().OffsetY,
                    Set("OffsetY", value => S().OffsetY = ToInt(value, S().OffsetY))),

                NewField("Opacity", "Crosshair_Config_Opacity_Label", "Crosshair_Config_Opacity_Desc",
                    ConfigFieldType.Integer, current.Opacity,
                    () => S().Opacity,
                    Set("Opacity", value => S().Opacity = ToInt(value, S().Opacity))),

                NewField("TriggerKeywords", "Crosshair_Config_Keywords_Label", "Crosshair_Config_Keywords_Desc",
                    ConfigFieldType.StringList, current.TriggerKeywords,
                    () => S().TriggerKeywords,
                    Set("TriggerKeywords", value => S().TriggerKeywords = CrosshairSettings.NormalizeKeywords(value as IEnumerable<string>))),

                // 开发指南的 Hotkey 字段类型 = 按键录制框；RequireModifier 强制带修饰键。
                // 注意：宿主录制框不认 Win 组合（剪贴板插件同样因此改用文本字段），如需 Win 组合键请反馈。
                NewField("Hotkey", "Crosshair_Config_Hotkey_Label", "Crosshair_Config_Hotkey_Desc",
                    ConfigFieldType.Hotkey, current.Hotkey,
                    () => S().Hotkey,
                    Set("Hotkey", value => S().Hotkey = value?.ToString() ?? string.Empty),
                    requireModifier: true)
            ],
            OnSave = onSave,
            OnRollback = onRollback
        };
    }

    /// <summary>把一栏代码应用到暂存设置；代码没变（或为空）时不动，免得覆盖用户刚改的细项。</summary>
    private static void ApplyCode(CrosshairSettings staged, string? code)
    {
        var text = (code ?? string.Empty).Trim();
        if (!CrosshairSettings.IsPlausibleCode(text))
        {
            CrosshairDiagnostics.Log($"代码栏内容不像准星代码（必须以 0 开头），忽略：\"{text}\"");
            return;
        }

        if (string.Equals(text, staged.Code, StringComparison.Ordinal))
        {
            return;
        }

        CrosshairCodec.Import(text, staged);
        CrosshairDiagnostics.Log($"已从代码栏导入：\"{text}\"");
    }

    private static PluginConfigField NewField(
        string key,
        string labelKey,
        string descriptionKey,
        ConfigFieldType type,
        object defaultValue,
        Func<object?> getValue,
        Action<object?> setValue,
        List<PluginConfigChoice>? choices = null,
        bool requireModifier = false)
    {
        return new PluginConfigField
        {
            Key = key,
            GroupKey = GroupKey,
            LabelKey = labelKey,
            DescriptionKey = descriptionKey,
            FieldType = type,
            DefaultValue = defaultValue,
            ChoiceOptions = choices,
            RequireModifier = requireModifier,
            GetValue = getValue,
            SetValue = setValue
        };
    }

    private static string Describe(object? value) => value switch
    {
        null => "<null>",
        bool b => b ? "true" : "false",
        string s => "\"" + s + "\"",
        IEnumerable<string> list => "[" + string.Join(", ", list) + "]",
        _ => value.ToString() ?? "<?>"
    };

    private static int ToInt(object? value, int fallback)
    {
        return value switch
        {
            int i => i,
            long l => (int)Math.Clamp(l, int.MinValue, int.MaxValue),
            double d => (int)Math.Round(d),
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => fallback
        };
    }

    /// <summary>不透明度一类的小数参数：接受 "0.8" 与 "0,8"，非法输入保持原值。</summary>
    private static double ToDouble(object? value, double fallback)
    {
        switch (value)
        {
            case double d:
                return d;
            case int i:
                return i;
            case long l:
                return l;
            case float f:
                return f;
        }

        var text = value?.ToString()?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return fallback;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var invariant)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out invariant)
            ? invariant
            : fallback;
    }
}
