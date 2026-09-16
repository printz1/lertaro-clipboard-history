using System.Globalization;
using Lertaro.PluginSdk;
using Lertaro.PluginSdk.Abstractions;
using Lertaro.PluginSdk.Abstractions.Plugins;
using Lertaro.PluginSdk.Models;

namespace Lertaro.Plugins.Crosshair;

/// <summary>
/// 设置面板 schema：okiaimx / VALORANT 的全部准星参数（宿主通用面板的等价文本字段），
/// 保存即实时重绘。可视化编辑请用「准星编辑器」，这里同时提供 VALORANT 代码栏作为快捷入口。
/// </summary>
internal static class CrosshairConfigSchema
{
    private const string GroupKey = "Crosshair";

    internal static PluginConfigSchema Build(
        CrosshairSettings current,
        CrosshairSettings staged,
        Action onSave,
        Action onRollback)
    {
        return new PluginConfigSchema
        {
            Fields =
            [
                // ---- 总开关与代码 ----
                NewField("Visible", "Crosshair_Config_Visible_Label", "Crosshair_Config_Visible_Desc",
                    ConfigFieldType.Boolean, current.Visible,
                    () => staged.Visible,
                    value => staged.Visible = value is bool b && b),

                NewField("CrosshairCode", "Crosshair_Config_Code_Label", "Crosshair_Config_Code_Desc",
                    ConfigFieldType.Text, current.Code,
                    () => staged.Code,
                    value => ApplyCode(staged, value?.ToString())),

                // ---- 颜色 ----
                NewField("Color", "Crosshair_Config_Color_Label", "Crosshair_Config_Color_Desc",
                    ConfigFieldType.Integer, current.Primary.Color,
                    () => staged.Primary.Color,
                    value => staged.Primary.Color = ToInt(value, staged.Primary.Color)),

                NewField("HexColor", "Crosshair_Config_HexColor_Label", "Crosshair_Config_HexColor_Desc",
                    ConfigFieldType.Text, CrosshairCodec.Hex6(current.Primary.HexColor.Value),
                    () => CrosshairCodec.Hex6(staged.Primary.HexColor.Value),
                    value =>
                    {
                        var hex8 = CrosshairCodec.Hex8(value?.ToString());
                        if (hex8 is not null)
                        {
                            staged.Primary.HexColor.Value = hex8;
                        }
                    }),

                NewField("HexColorEnabled", "Crosshair_Config_HexColorEnabled_Label", "Crosshair_Config_HexColorEnabled_Desc",
                    ConfigFieldType.Boolean, current.Primary.HexColor.Enabled,
                    () => staged.Primary.HexColor.Enabled,
                    value => staged.Primary.HexColor.Enabled = value is bool b && b),

                // ---- 描边 ----
                NewField("OutlineEnabled", "Crosshair_Config_OutlineEnabled_Label", "Crosshair_Config_OutlineEnabled_Desc",
                    ConfigFieldType.Boolean, current.Primary.Outlines.Enabled,
                    () => staged.Primary.Outlines.Enabled,
                    value => staged.Primary.Outlines.Enabled = value is bool b && b),

                NewField("OutlineWidth", "Crosshair_Config_OutlineWidth_Label", "Crosshair_Config_OutlineWidth_Desc",
                    ConfigFieldType.Integer, current.Primary.Outlines.Width,
                    () => staged.Primary.Outlines.Width,
                    value => staged.Primary.Outlines.Width = ToDouble(value, staged.Primary.Outlines.Width)),

                NewField("OutlineAlpha", "Crosshair_Config_OutlineAlpha_Label", "Crosshair_Config_OutlineAlpha_Desc",
                    ConfigFieldType.Text, current.Primary.Outlines.Alpha,
                    () => staged.Primary.Outlines.Alpha,
                    value => staged.Primary.Outlines.Alpha = ToDouble(value, staged.Primary.Outlines.Alpha)),

                // ---- 中心点 ----
                NewField("DotEnabled", "Crosshair_Config_DotEnabled_Label", "Crosshair_Config_DotEnabled_Desc",
                    ConfigFieldType.Boolean, current.Primary.Dot.Enabled,
                    () => staged.Primary.Dot.Enabled,
                    value => staged.Primary.Dot.Enabled = value is bool b && b),

                NewField("DotWidth", "Crosshair_Config_DotWidth_Label", "Crosshair_Config_DotWidth_Desc",
                    ConfigFieldType.Integer, current.Primary.Dot.Width,
                    () => staged.Primary.Dot.Width,
                    value => staged.Primary.Dot.Width = ToDouble(value, staged.Primary.Dot.Width)),

                NewField("DotAlpha", "Crosshair_Config_DotAlpha_Label", "Crosshair_Config_DotAlpha_Desc",
                    ConfigFieldType.Text, current.Primary.Dot.Alpha,
                    () => staged.Primary.Dot.Alpha,
                    value => staged.Primary.Dot.Alpha = ToDouble(value, staged.Primary.Dot.Alpha)),

                // ---- 内部线条 ----
                NewField("InnerEnabled", "Crosshair_Config_InnerEnabled_Label", "Crosshair_Config_InnerEnabled_Desc",
                    ConfigFieldType.Boolean, current.Primary.Inner.Enabled,
                    () => staged.Primary.Inner.Enabled,
                    value => staged.Primary.Inner.Enabled = value is bool b && b),

                NewField("InnerWidth", "Crosshair_Config_InnerWidth_Label", "Crosshair_Config_InnerWidth_Desc",
                    ConfigFieldType.Integer, current.Primary.Inner.Width,
                    () => staged.Primary.Inner.Width,
                    value => staged.Primary.Inner.Width = ToDouble(value, staged.Primary.Inner.Width)),

                NewField("InnerLength", "Crosshair_Config_InnerLength_Label", "Crosshair_Config_InnerLength_Desc",
                    ConfigFieldType.Integer, current.Primary.Inner.Length,
                    () => staged.Primary.Inner.Length,
                    value => staged.Primary.Inner.Length = ToDouble(value, staged.Primary.Inner.Length)),

                NewField("InnerOffset", "Crosshair_Config_InnerOffset_Label", "Crosshair_Config_InnerOffset_Desc",
                    ConfigFieldType.Integer, current.Primary.Inner.Offset,
                    () => staged.Primary.Inner.Offset,
                    value => staged.Primary.Inner.Offset = ToDouble(value, staged.Primary.Inner.Offset)),

                NewField("InnerAlpha", "Crosshair_Config_InnerAlpha_Label", "Crosshair_Config_InnerAlpha_Desc",
                    ConfigFieldType.Text, current.Primary.Inner.Alpha,
                    () => staged.Primary.Inner.Alpha,
                    value => staged.Primary.Inner.Alpha = ToDouble(value, staged.Primary.Inner.Alpha)),

                NewField("InnerVerticalEnabled", "Crosshair_Config_InnerVerticalEnabled_Label", "Crosshair_Config_InnerVerticalEnabled_Desc",
                    ConfigFieldType.Boolean, current.Primary.Inner.Vertical.Enabled,
                    () => staged.Primary.Inner.Vertical.Enabled,
                    value => staged.Primary.Inner.Vertical.Enabled = value is bool b && b),

                NewField("InnerVerticalLength", "Crosshair_Config_InnerVerticalLength_Label", "Crosshair_Config_InnerVerticalLength_Desc",
                    ConfigFieldType.Integer, current.Primary.Inner.Vertical.Length,
                    () => staged.Primary.Inner.Vertical.Length,
                    value => staged.Primary.Inner.Vertical.Length = ToDouble(value, staged.Primary.Inner.Vertical.Length)),

                // ---- 外部线条 ----
                NewField("OuterEnabled", "Crosshair_Config_OuterEnabled_Label", "Crosshair_Config_OuterEnabled_Desc",
                    ConfigFieldType.Boolean, current.Primary.Outer.Enabled,
                    () => staged.Primary.Outer.Enabled,
                    value => staged.Primary.Outer.Enabled = value is bool b && b),

                NewField("OuterWidth", "Crosshair_Config_OuterWidth_Label", "Crosshair_Config_OuterWidth_Desc",
                    ConfigFieldType.Integer, current.Primary.Outer.Width,
                    () => staged.Primary.Outer.Width,
                    value => staged.Primary.Outer.Width = ToDouble(value, staged.Primary.Outer.Width)),

                NewField("OuterLength", "Crosshair_Config_OuterLength_Label", "Crosshair_Config_OuterLength_Desc",
                    ConfigFieldType.Integer, current.Primary.Outer.Length,
                    () => staged.Primary.Outer.Length,
                    value => staged.Primary.Outer.Length = ToDouble(value, staged.Primary.Outer.Length)),

                NewField("OuterOffset", "Crosshair_Config_OuterOffset_Label", "Crosshair_Config_OuterOffset_Desc",
                    ConfigFieldType.Integer, current.Primary.Outer.Offset,
                    () => staged.Primary.Outer.Offset,
                    value => staged.Primary.Outer.Offset = ToDouble(value, staged.Primary.Outer.Offset)),

                NewField("OuterAlpha", "Crosshair_Config_OuterAlpha_Label", "Crosshair_Config_OuterAlpha_Desc",
                    ConfigFieldType.Text, current.Primary.Outer.Alpha,
                    () => staged.Primary.Outer.Alpha,
                    value => staged.Primary.Outer.Alpha = ToDouble(value, staged.Primary.Outer.Alpha)),

                NewField("OuterVerticalEnabled", "Crosshair_Config_OuterVerticalEnabled_Label", "Crosshair_Config_OuterVerticalEnabled_Desc",
                    ConfigFieldType.Boolean, current.Primary.Outer.Vertical.Enabled,
                    () => staged.Primary.Outer.Vertical.Enabled,
                    value => staged.Primary.Outer.Vertical.Enabled = value is bool b && b),

                NewField("OuterVerticalLength", "Crosshair_Config_OuterVerticalLength_Label", "Crosshair_Config_OuterVerticalLength_Desc",
                    ConfigFieldType.Integer, current.Primary.Outer.Vertical.Length,
                    () => staged.Primary.Outer.Vertical.Length,
                    value => staged.Primary.Outer.Vertical.Length = ToDouble(value, staged.Primary.Outer.Vertical.Length)),

                // ---- 扩展项 ----
                NewField("OffsetX", "Crosshair_Config_OffsetX_Label", "Crosshair_Config_OffsetX_Desc",
                    ConfigFieldType.Integer, current.OffsetX,
                    () => staged.OffsetX,
                    value => staged.OffsetX = ToInt(value, staged.OffsetX)),

                NewField("OffsetY", "Crosshair_Config_OffsetY_Label", "Crosshair_Config_OffsetY_Desc",
                    ConfigFieldType.Integer, current.OffsetY,
                    () => staged.OffsetY,
                    value => staged.OffsetY = ToInt(value, staged.OffsetY)),

                NewField("Opacity", "Crosshair_Config_Opacity_Label", "Crosshair_Config_Opacity_Desc",
                    ConfigFieldType.Integer, current.Opacity,
                    () => staged.Opacity,
                    value => staged.Opacity = ToInt(value, staged.Opacity)),

                NewField("TriggerKeywords", "Crosshair_Config_Keywords_Label", "Crosshair_Config_Keywords_Desc",
                    ConfigFieldType.StringList, current.TriggerKeywords,
                    () => staged.TriggerKeywords,
                    value => staged.TriggerKeywords = CrosshairSettings.NormalizeKeywords(value as IEnumerable<string>)),

                NewField("Hotkey", "Crosshair_Config_Hotkey_Label", "Crosshair_Config_Hotkey_Desc",
                    ConfigFieldType.Text, current.Hotkey,
                    () => staged.Hotkey,
                    value => staged.Hotkey = value?.ToString() ?? string.Empty)
            ],
            OnSave = onSave,
            OnRollback = onRollback
        };
    }

    /// <summary>把一栏代码应用到暂存设置；代码没变（或为空）时不动，免得覆盖用户刚改的细项。</summary>
    private static void ApplyCode(CrosshairSettings staged, string? code)
    {
        var text = (code ?? string.Empty).Trim();
        if (!CrosshairSettings.IsPlausibleCode(text)
            || string.Equals(text, staged.Code, StringComparison.Ordinal))
        {
            return;
        }

        CrosshairCodec.Import(text, staged);
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
