namespace Lertaro.Plugins.Crosshair;

/// <summary>
/// okiaimx / VALORANT 准星参数的取值范围与默认值。
/// 数值全部取自 okiaimx.com 的准星编辑器（其内部对象即 VALORANT 的 crosshair 结构），
/// 导入导出用的字段表（<see cref="CrosshairCodec"/>）也引用这里的常量，避免两处漂移。
/// </summary>
internal static class CrosshairLimits
{
    /// <summary>描边粗细（VALORANT 字段 o/t）：1-6。</summary>
    public const double OutlineWidthMin = 1;
    public const double OutlineWidthMax = 6;

    /// <summary>中心点大小（字段 z）：1-6。</summary>
    public const double DotWidthMin = 1;
    public const double DotWidthMax = 6;

    /// <summary>内/外线粗细（字段 0t / 1t）：0-10。</summary>
    public const double LineWidthMin = 0;
    public const double LineWidthMax = 10;

    /// <summary>内线长度（字段 0l）：0-20。</summary>
    public const double InnerLengthMax = 20;

    /// <summary>内线偏移（字段 0o）：0-20。</summary>
    public const double InnerOffsetMax = 20;

    /// <summary>外线长度（字段 1l）：0-10。</summary>
    public const double OuterLengthMax = 10;

    /// <summary>外线偏移（字段 1o）：0-40。</summary>
    public const double OuterOffsetMax = 40;

    /// <summary>竖线长度（字段 0v / 1v）：0-20。</summary>
    public const double VerticalLengthMax = 20;

    /// <summary>移动/开火误差倍率（字段 0s / 0e / 1s / 1e）：0-3。</summary>
    public const double ErrorMultiplierMax = 3;

    /// <summary>不透明度下限/上限（所有 alpha 字段）。</summary>
    public const double AlphaMin = 0;
    public const double AlphaMax = 1;
}

/// <summary>
/// 准星参数树。分成 <c>general</c> 与 <c>primary</c> 两段，与 okiaimx（也就是 VALORANT）一致。
/// 其中 <c>f/s/m</c> 三个字段在 okiaimx 里属于 general / primary 混排，这里按语义各归其位，
/// 编解码时再由字段表映射回 VALORANT 的写法。
/// </summary>
internal sealed class CrosshairGeneralSettings
{
    /// <summary>VALORANT 字段 P:f —— 开火时隐藏准星。静态叠加层不消费，但代码往返原样保留。</summary>
    public bool HideOnFire { get; set; } = true;

    /// <summary>VALORANT 字段 P:s —— 观战跟随。同上，仅保证代码往返不失真。</summary>
    public bool FollowSpectating { get; set; } = true;

    internal CrosshairGeneralSettings Copy() => new()
    {
        HideOnFire = HideOnFire,
        FollowSpectating = FollowSpectating
    };

    /// <summary>原地覆盖（不换对象）—— 设置面板/编辑器都持有这些子对象的引用，换对象会让它们的编辑写进孤儿对象。</summary>
    internal void CopyFrom(CrosshairGeneralSettings other)
    {
        HideOnFire = other.HideOnFire;
        FollowSpectating = other.FollowSpectating;
    }
}

/// <summary>VALORANT primary 段：颜色 + 描边 + 中心点 + 内线 + 外线。</summary>
internal sealed class CrosshairPrimarySettings
{
    /// <summary>颜色索引：0-7 为预设色（见 <see cref="CrosshairCodec.Palette"/>），8 = 自定义颜色。</summary>
    public int Color { get; set; }

    /// <summary>自定义颜色。Color == 8 或 <see cref="CrosshairHexColor.Enabled"/> 为真时生效。</summary>
    public CrosshairHexColor HexColor { get; set; } = new();

    /// <summary>描边（黑边）。</summary>
    public CrosshairOutlines Outlines { get; set; } = new();

    /// <summary>中心点。</summary>
    public CrosshairDot Dot { get; set; } = new();

    /// <summary>VALORANT 字段 P:m —— 内/外线是否由全局覆盖开火误差偏移。</summary>
    public bool OverwriteFireMul { get; set; }

    /// <summary>内部线条。</summary>
    public CrosshairLines Inner { get; set; } = CrosshairLines.CreateInner();

    /// <summary>外部线条。</summary>
    public CrosshairLines Outer { get; set; } = CrosshairLines.CreateOuter();

    internal CrosshairPrimarySettings Copy() => new()
    {
        Color = Color,
        HexColor = HexColor.Copy(),
        Outlines = Outlines.Copy(),
        Dot = Dot.Copy(),
        OverwriteFireMul = OverwriteFireMul,
        Inner = Inner.Copy(),
        Outer = Outer.Copy()
    };

    /// <summary>原地覆盖（不换对象）。</summary>
    internal void CopyFrom(CrosshairPrimarySettings other)
    {
        Color = other.Color;
        OverwriteFireMul = other.OverwriteFireMul;
        HexColor.CopyFrom(other.HexColor);
        Outlines.CopyFrom(other.Outlines);
        Dot.CopyFrom(other.Dot);
        Inner.CopyFrom(other.Inner);
        Outer.CopyFrom(other.Outer);
    }
}

/// <summary>自定义颜色：8 位十六进制（okiais / VALORANT 写法，前两位固定 FF）。</summary>
internal sealed class CrosshairHexColor
{
    /// <summary>VALORANT 字段 P:b。</summary>
    public bool Enabled { get; set; }

    /// <summary>形如 FFFFFFFF 的 8 位十六进制；对外显示时取后 6 位。</summary>
    public string Value { get; set; } = "FFFFFFFF";

    internal CrosshairHexColor Copy() => new() { Enabled = Enabled, Value = Value };

    internal void CopyFrom(CrosshairHexColor other)
    {
        Enabled = other.Enabled;
        Value = other.Value;
    }
}

/// <summary>描边：粗细 1-6，不透明度 0-1。</summary>
internal sealed class CrosshairOutlines
{
    /// <summary>VALORANT 字段 P:h。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>VALORANT 字段 P:t。</summary>
    public double Width { get; set; } = 1;

    /// <summary>VALORANT 字段 P:o。</summary>
    public double Alpha { get; set; } = 0.5;

    internal CrosshairOutlines Copy() => new() { Enabled = Enabled, Width = Width, Alpha = Alpha };

    internal void CopyFrom(CrosshairOutlines other)
    {
        Enabled = other.Enabled;
        Width = other.Width;
        Alpha = other.Alpha;
    }
}

/// <summary>中心点：大小 1-6，不透明度 0-1，默认关闭。</summary>
internal sealed class CrosshairDot
{
    /// <summary>VALORANT 字段 P:d。</summary>
    public bool Enabled { get; set; }

    /// <summary>VALORANT 字段 P:z。</summary>
    public double Width { get; set; } = 2;

    /// <summary>VALORANT 字段 P:a。</summary>
    public double Alpha { get; set; } = 1;

    internal CrosshairDot Copy() => new() { Enabled = Enabled, Width = Width, Alpha = Alpha };

    internal void CopyFrom(CrosshairDot other)
    {
        Enabled = other.Enabled;
        Width = other.Width;
        Alpha = other.Alpha;
    }
}

/// <summary>竖线（内线/外线各自的上下臂）：可单独设置长度。</summary>
internal sealed class CrosshairVertical
{
    /// <summary>VALORANT 字段 0g / 1g —— 单独设置竖线。</summary>
    public bool Enabled { get; set; }

    /// <summary>VALORANT 字段 0v / 1v。</summary>
    public double Length { get; set; }

    internal CrosshairVertical Copy() => new() { Enabled = Enabled, Length = Length };

    internal void CopyFrom(CrosshairVertical other)
    {
        Enabled = other.Enabled;
        Length = other.Length;
    }
}

/// <summary>移动误差 / 开火误差倍率（仅用于与 VALORANT 代码无损往返）。</summary>
internal sealed class CrosshairErrorMul
{
    /// <summary>VALORANT 字段 0m / 0f / 1m / 1f。</summary>
    public bool Enabled { get; set; }

    /// <summary>VALORANT 字段 0s / 0e / 1s / 1e。</summary>
    public double Multiplier { get; set; } = 1;

    internal CrosshairErrorMul Copy() => new() { Enabled = Enabled, Multiplier = Multiplier };

    internal void CopyFrom(CrosshairErrorMul other)
    {
        Enabled = other.Enabled;
        Multiplier = other.Multiplier;
    }
}

/// <summary>内线 / 外线共用同一套字段，只是长度、偏移范围与默认值不同。</summary>
internal sealed class CrosshairLines
{
    /// <summary>VALORANT 字段 0b / 1b。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>VALORANT 字段 0t / 1t。</summary>
    public double Width { get; set; } = 2;

    /// <summary>VALORANT 字段 0l / 1l。</summary>
    public double Length { get; set; }

    /// <summary>VALORANT 字段 0o / 1o —— 线条离中心的距离。</summary>
    public double Offset { get; set; }

    /// <summary>VALORANT 字段 0a / 1a。</summary>
    public double Alpha { get; set; } = 1;

    /// <summary>竖线单独长度。</summary>
    public CrosshairVertical Vertical { get; set; } = new();

    /// <summary>移动误差倍率。</summary>
    public CrosshairErrorMul MoveMul { get; set; } = new();

    /// <summary>开火误差倍率。okiais 的绘制在它开启且未被覆盖时给偏移 +4，本插件同样如此。</summary>
    public CrosshairErrorMul FireMul { get; set; } = new();

    internal static CrosshairLines CreateInner() => new()
    {
        Enabled = true,
        Width = 2,
        Length = 6,
        Offset = 3,
        Alpha = 0.8,
        Vertical = new CrosshairVertical { Enabled = false, Length = 6 },
        MoveMul = new CrosshairErrorMul { Enabled = false, Multiplier = 1 },
        FireMul = new CrosshairErrorMul { Enabled = true, Multiplier = 1 }
    };

    internal static CrosshairLines CreateOuter() => new()
    {
        Enabled = true,
        Width = 2,
        Length = 2,
        Offset = 10,
        Alpha = 0.35,
        Vertical = new CrosshairVertical { Enabled = false, Length = 2 },
        MoveMul = new CrosshairErrorMul { Enabled = true, Multiplier = 1 },
        FireMul = new CrosshairErrorMul { Enabled = true, Multiplier = 1 }
    };

    internal CrosshairLines Copy() => new()
    {
        Enabled = Enabled,
        Width = Width,
        Length = Length,
        Offset = Offset,
        Alpha = Alpha,
        Vertical = Vertical.Copy(),
        MoveMul = MoveMul.Copy(),
        FireMul = FireMul.Copy()
    };

    internal void CopyFrom(CrosshairLines other)
    {
        Enabled = other.Enabled;
        Width = other.Width;
        Length = other.Length;
        Offset = other.Offset;
        Alpha = other.Alpha;
        Vertical.CopyFrom(other.Vertical);
        MoveMul.CopyFrom(other.MoveMul);
        FireMul.CopyFrom(other.FireMul);
    }
}
