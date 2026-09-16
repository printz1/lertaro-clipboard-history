using System.Globalization;
using System.Text;

namespace Lertaro.Plugins.Crosshair;

/// <summary>
/// VALORANT 准星代码编解码器。1:1 移植 okiaimx.com 的实现（同一张字段表、同一套解析规则、
/// 同一个导出顺序），因此：
/// <list type="bullet">
///   <item>从 okiaimx / 游戏里复制的代码可以原样粘进来；</item>
///   <item>这里导出的代码也能粘回 okiaimx 或游戏（等于默认值的字段会被省略，与官方写法一致）。</item>
/// </list>
/// 代码形如 <c>0;P;c;5;o;1;0t;1;0l;2;0o;2;0a;1;1b;0</c>：
/// 以 <c>0</c> 开头，<c>P</c> 段为常规准星（<c>A</c> = 狙击镜、<c>S</c> = 短枪，本插件只消费 P 段）。
/// </summary>
internal static class CrosshairCodec
{
    /// <summary>默认代码（okiais 用 "0;P" 表示"一切默认"）。</summary>
    internal const string DefaultCode = "0;P";

    /// <summary>VALORANT 预设色（索引 0-7），索引 8 = 自定义颜色。</summary>
    internal static readonly string[] Palette =
    [
        "#FFFFFF", // 0 白色
        "#00FF00", // 1 绿色
        "#7FFF00", // 2 黄绿色
        "#DFFF00", // 3 绿黄色
        "#FFFF00", // 4 黄色
        "#00FFFF", // 5 青色
        "#FF00FF", // 6 粉色
        "#FF0000"  // 7 红色
    ];

    /// <summary>自定义颜色的索引（okiais 的 <c>xhCustom</c>）。</summary>
    internal const int CustomColorIndex = 8;

    /// <summary>
    /// 导出顺序。与 okiais 的 <c>hc()</c> 逐字一致：顺序不同会导致同一套参数导出不同字符串。
    /// </summary>
    private static readonly string[] ExportOrder =
        "c.u.b.h.t.o.d.z.a.f.s.m.0b.0t.0l.0v.0g.0o.0a.0m.0f.0s.0e.1b.1t.1l.1v.1g.1o.1a.1m.1f.1s.1e".Split('.');

    private sealed record FieldDef(
        string Name,
        double Min,
        double Max,
        bool Integer,
        Func<CrosshairSettings, double> Get,
        Action<CrosshairSettings, double> Set);

    private static readonly FieldDef[] Fields = BuildFields();

    // ---- 解析：代码 → 参数 ----

    /// <summary>把代码解析成一套全新参数（未出现在代码里的字段取默认值）。非法片段按 okiais 的做法直接跳过。</summary>
    internal static CrosshairSettings Parse(string? code)
    {
        var result = new CrosshairSettings();
        var tokens = (code ?? string.Empty).Trim().Split(';');
        if (tokens.Length <= 1)
        {
            return result;
        }

        var section = "0";
        var seenSections = new List<string>();
        for (var i = 1; i < tokens.Length; i += 2)
        {
            var token = tokens[i];

            // 段标记（P/A/S）：本身占一个 token，下面的 i-- 让 i+=2 正好落在字段上
            if (token is "P" or "A" or "S")
            {
                section = token;
                i--;
                if (seenSections.Contains(section))
                {
                    return result; // 同一个段出现两次：okiais 直接放弃后续解析
                }

                seenSections.Add(section);
                continue;
            }

            if (i + 1 >= tokens.Length)
            {
                break;
            }

            if (!TryReadValue(tokens[i + 1], out var value))
            {
                continue;
            }

            var field = FindField(section + ":" + token);
            if (field is null
                || (field.Integer && value != Math.Floor(value))
                || value < field.Min
                || value > field.Max)
            {
                continue;
            }

            field.Set(result, value);
        }

        Sanitize(result);
        return result;
    }

    /// <summary>
    /// 把代码应用到已有设置上（只覆盖代码里出现的字段所在的 general/primary 两段）。
    /// 注意是<b>原地拷贝</b>：设置面板与编辑器都持有这些子对象的引用，换对象会让它们之后的编辑写进孤儿对象。
    /// </summary>
    internal static void Import(string? code, CrosshairSettings target)
    {
        var parsed = Parse(code);
        target.General.CopyFrom(parsed.General);
        target.Primary.CopyFrom(parsed.Primary);
    }

    private static bool TryReadValue(string raw, out double value)
    {
        value = 0;

        // 6/8 位十六进制按颜色整数解析（okiais 的做法：6 位自动补 FF 前缀）
        if (raw.Length is 6 or 8 && IsHex(raw))
        {
            var hex = raw.Length == 6 ? "FF" + raw : raw;
            value = ulong.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return true;
        }

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && !double.IsNaN(value);
    }

    private static bool IsHex(string text)
    {
        foreach (var ch in text)
        {
            if (!char.IsAsciiHexDigit(ch))
            {
                return false;
            }
        }

        return text.Length > 0;
    }

    // ---- 导出：参数 → 代码 ----

    /// <summary>导出为 VALORANT 兼容代码；等于默认值的字段省略。</summary>
    internal static string Export(CrosshairSettings settings)
    {
        var defaults = new CrosshairSettings();
        var code = new StringBuilder(DefaultCode);

        foreach (var name in ExportOrder)
        {
            var field = FindField("P:" + name);
            if (field is null)
            {
                continue;
            }

            var current = field.Get(settings);
            if (current.Equals(field.Get(defaults)))
            {
                continue;
            }

            code.Append(';').Append(name).Append(';');
            code.Append(field.Integer
                ? Math.Round(current).ToString("0", CultureInfo.InvariantCulture)
                : Math.Round(current, 3).ToString("0.###", CultureInfo.InvariantCulture));
        }

        return code.ToString();
    }

    // ---- 颜色工具 ----

    /// <summary>取颜色索引对应的画刷色值；索引 8（或自定义颜色启用）时取自定义值。</summary>
    internal static string ResolveColor(CrosshairPrimarySettings primary)
    {
        if (primary.Color == CustomColorIndex || primary.HexColor.Enabled)
        {
            return Hex6(primary.HexColor.Value);
        }

        var index = Math.Clamp(primary.Color, 0, Palette.Length - 1);
        return Palette[index];
    }

    /// <summary>8 位十六进制（FFRRGGBB）→ #RRGGBB。</summary>
    internal static string Hex6(string? value)
    {
        var text = (value ?? string.Empty).Trim().TrimStart('#').ToUpperInvariant();
        if (text.Length == 8)
        {
            text = text[2..];
        }

        return text.Length == 6 && IsHex(text) ? "#" + text : "#FFFFFF";
    }

    /// <summary>#RRGGBB → 8 位十六进制（FFRRGGBB）。非法输入返回 null。</summary>
    internal static string? Hex8(string? value)
    {
        var text = (value ?? string.Empty).Trim().TrimStart('#').ToUpperInvariant();
        if (text.Length == 8)
        {
            text = text[2..];
        }

        return text.Length == 6 && IsHex(text) ? "FF" + text : null;
    }

    // ---- 字段表 ----

    private static FieldDef? FindField(string name)
    {
        foreach (var field in Fields)
        {
            if (string.Equals(field.Name, name, StringComparison.Ordinal))
            {
                return field;
            }
        }

        return null;
    }

    private static double ParseHexValue(string? value) =>
        ulong.TryParse((value ?? string.Empty).Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0xFFFFFFFFd;

    private static string FormatHexValue(double value)
    {
        var rounded = Math.Clamp(Math.Round(value), 0, uint.MaxValue);
        return ((uint)rounded).ToString("X8", CultureInfo.InvariantCulture);
    }

    private static FieldDef[] BuildFields() =>
    [
        // 颜色
        new("P:c", 0, CustomColorIndex, true,
            s => s.Primary.Color,
            (s, v) => s.Primary.Color = (int)v),
        new("P:u", 0, uint.MaxValue, true,
            s => ParseHexValue(s.Primary.HexColor.Value),
            (s, v) => s.Primary.HexColor.Value = FormatHexValue(v)),
        new("P:b", 0, 1, true,
            s => Bool(s.Primary.HexColor.Enabled),
            (s, v) => s.Primary.HexColor.Enabled = v != 0),

        // 描边
        new("P:h", 0, 1, true,
            s => Bool(s.Primary.Outlines.Enabled),
            (s, v) => s.Primary.Outlines.Enabled = v != 0),
        new("P:t", CrosshairLimits.OutlineWidthMin, CrosshairLimits.OutlineWidthMax, true,
            s => s.Primary.Outlines.Width,
            (s, v) => s.Primary.Outlines.Width = v),
        new("P:o", CrosshairLimits.AlphaMin, CrosshairLimits.AlphaMax, false,
            s => s.Primary.Outlines.Alpha,
            (s, v) => s.Primary.Outlines.Alpha = v),

        // 中心点
        new("P:d", 0, 1, true,
            s => Bool(s.Primary.Dot.Enabled),
            (s, v) => s.Primary.Dot.Enabled = v != 0),
        new("P:z", CrosshairLimits.DotWidthMin, CrosshairLimits.DotWidthMax, true,
            s => s.Primary.Dot.Width,
            (s, v) => s.Primary.Dot.Width = v),
        new("P:a", CrosshairLimits.AlphaMin, CrosshairLimits.AlphaMax, false,
            s => s.Primary.Dot.Alpha,
            (s, v) => s.Primary.Dot.Alpha = v),

        // general / 覆盖位（VALORANT 把这三个字段塞在 P 段里）
        new("P:f", 0, 1, true,
            s => Bool(s.General.HideOnFire),
            (s, v) => s.General.HideOnFire = v != 0),
        new("P:s", 0, 1, true,
            s => Bool(s.General.FollowSpectating),
            (s, v) => s.General.FollowSpectating = v != 0),
        new("P:m", 0, 1, true,
            s => Bool(s.Primary.OverwriteFireMul),
            (s, v) => s.Primary.OverwriteFireMul = v != 0),

        // 内部线条
        new("P:0b", 0, 1, true,
            s => Bool(s.Primary.Inner.Enabled),
            (s, v) => s.Primary.Inner.Enabled = v != 0),
        new("P:0t", CrosshairLimits.LineWidthMin, CrosshairLimits.LineWidthMax, true,
            s => s.Primary.Inner.Width,
            (s, v) => s.Primary.Inner.Width = v),
        new("P:0l", 0, CrosshairLimits.InnerLengthMax, true,
            s => s.Primary.Inner.Length,
            (s, v) => s.Primary.Inner.Length = v),
        new("P:0v", 0, CrosshairLimits.VerticalLengthMax, true,
            s => s.Primary.Inner.Vertical.Length,
            (s, v) => s.Primary.Inner.Vertical.Length = v),
        new("P:0g", 0, 1, true,
            s => Bool(s.Primary.Inner.Vertical.Enabled),
            (s, v) => s.Primary.Inner.Vertical.Enabled = v != 0),
        new("P:0o", 0, CrosshairLimits.InnerOffsetMax, true,
            s => s.Primary.Inner.Offset,
            (s, v) => s.Primary.Inner.Offset = v),
        new("P:0a", CrosshairLimits.AlphaMin, CrosshairLimits.AlphaMax, false,
            s => s.Primary.Inner.Alpha,
            (s, v) => s.Primary.Inner.Alpha = v),
        new("P:0m", 0, 1, true,
            s => Bool(s.Primary.Inner.MoveMul.Enabled),
            (s, v) => s.Primary.Inner.MoveMul.Enabled = v != 0),
        new("P:0f", 0, 1, true,
            s => Bool(s.Primary.Inner.FireMul.Enabled),
            (s, v) => s.Primary.Inner.FireMul.Enabled = v != 0),
        new("P:0s", 0, CrosshairLimits.ErrorMultiplierMax, false,
            s => s.Primary.Inner.MoveMul.Multiplier,
            (s, v) => s.Primary.Inner.MoveMul.Multiplier = v),
        new("P:0e", 0, CrosshairLimits.ErrorMultiplierMax, false,
            s => s.Primary.Inner.FireMul.Multiplier,
            (s, v) => s.Primary.Inner.FireMul.Multiplier = v),

        // 外部线条
        new("P:1b", 0, 1, true,
            s => Bool(s.Primary.Outer.Enabled),
            (s, v) => s.Primary.Outer.Enabled = v != 0),
        new("P:1t", CrosshairLimits.LineWidthMin, CrosshairLimits.LineWidthMax, true,
            s => s.Primary.Outer.Width,
            (s, v) => s.Primary.Outer.Width = v),
        new("P:1l", 0, CrosshairLimits.OuterLengthMax, true,
            s => s.Primary.Outer.Length,
            (s, v) => s.Primary.Outer.Length = v),
        new("P:1v", 0, CrosshairLimits.VerticalLengthMax, true,
            s => s.Primary.Outer.Vertical.Length,
            (s, v) => s.Primary.Outer.Vertical.Length = v),
        new("P:1g", 0, 1, true,
            s => Bool(s.Primary.Outer.Vertical.Enabled),
            (s, v) => s.Primary.Outer.Vertical.Enabled = v != 0),
        new("P:1o", 0, CrosshairLimits.OuterOffsetMax, true,
            s => s.Primary.Outer.Offset,
            (s, v) => s.Primary.Outer.Offset = v),
        new("P:1a", CrosshairLimits.AlphaMin, CrosshairLimits.AlphaMax, false,
            s => s.Primary.Outer.Alpha,
            (s, v) => s.Primary.Outer.Alpha = v),
        new("P:1m", 0, 1, true,
            s => Bool(s.Primary.Outer.MoveMul.Enabled),
            (s, v) => s.Primary.Outer.MoveMul.Enabled = v != 0),
        new("P:1f", 0, 1, true,
            s => Bool(s.Primary.Outer.FireMul.Enabled),
            (s, v) => s.Primary.Outer.FireMul.Enabled = v != 0),
        new("P:1s", 0, CrosshairLimits.ErrorMultiplierMax, false,
            s => s.Primary.Outer.MoveMul.Multiplier,
            (s, v) => s.Primary.Outer.MoveMul.Multiplier = v),
        new("P:1e", 0, CrosshairLimits.ErrorMultiplierMax, false,
            s => s.Primary.Outer.FireMul.Multiplier,
            (s, v) => s.Primary.Outer.FireMul.Multiplier = v)
    ];

    private static double Bool(bool value) => value ? 1 : 0;

    // ---- 兜底清洗：任何来源（手改 settings.json / 宿主面板）都收敛回合法范围 ----

    internal static void Sanitize(CrosshairSettings settings)
    {
        var primary = settings.Primary;

        primary.Color = (int)Math.Clamp(primary.Color, 0, CustomColorIndex);
        primary.HexColor.Value = Hex8(primary.HexColor.Value) ?? "FFFFFFFF";
        primary.Outlines.Width = Clamp(primary.Outlines.Width, CrosshairLimits.OutlineWidthMin, CrosshairLimits.OutlineWidthMax);
        primary.Outlines.Alpha = Clamp(primary.Outlines.Alpha, CrosshairLimits.AlphaMin, CrosshairLimits.AlphaMax);
        primary.Dot.Width = Clamp(primary.Dot.Width, CrosshairLimits.DotWidthMin, CrosshairLimits.DotWidthMax);
        primary.Dot.Alpha = Clamp(primary.Dot.Alpha, CrosshairLimits.AlphaMin, CrosshairLimits.AlphaMax);
        SanitizeLines(primary.Inner, CrosshairLimits.InnerLengthMax, CrosshairLimits.InnerOffsetMax);
        SanitizeLines(primary.Outer, CrosshairLimits.OuterLengthMax, CrosshairLimits.OuterOffsetMax);
    }

    private static void SanitizeLines(CrosshairLines lines, double lengthMax, double offsetMax)
    {
        lines.Width = Clamp(lines.Width, CrosshairLimits.LineWidthMin, CrosshairLimits.LineWidthMax);
        lines.Length = Clamp(lines.Length, 0, lengthMax);
        lines.Offset = Clamp(lines.Offset, 0, offsetMax);
        lines.Alpha = Clamp(lines.Alpha, CrosshairLimits.AlphaMin, CrosshairLimits.AlphaMax);
        lines.Vertical.Length = Clamp(lines.Vertical.Length, 0, CrosshairLimits.VerticalLengthMax);
        lines.MoveMul.Multiplier = Clamp(lines.MoveMul.Multiplier, 0, CrosshairLimits.ErrorMultiplierMax);
        lines.FireMul.Multiplier = Clamp(lines.FireMul.Multiplier, 0, CrosshairLimits.ErrorMultiplierMax);
    }

    private static double Clamp(double value, double min, double max) =>
        double.IsNaN(value) ? min : Math.Clamp(value, min, max);
}
