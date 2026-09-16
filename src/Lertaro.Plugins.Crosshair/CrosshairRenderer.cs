using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Lertaro.Plugins.Crosshair;

/// <summary>
/// 准星绘制器：叠加层（屏幕）与编辑器预览共用同一套绘制逻辑，保证"预览即所得"。
/// 几何按 okiaimx.com 的 canvas 实现（<c>xc()</c> / <c>bc()</c>）逐行移植：
/// 单位与 VALORANT 一致 —— 1 单位 = <see cref="ReferenceWidth"/> 宽屏幕上的 1 像素，
/// 所以屏幕越宽准星同比放大（4K 下 ×2，和游戏里的表现一致）。
///
/// 绘制顺序也与 okiaimx 相同：内部线条 → 中心点 → 外部线条（后画的压在上面）。
/// </summary>
internal static class CrosshairRenderer
{
    /// <summary>VALORANT / okiaimx 的参考宽度：1 单位 = 该宽度下的 1 像素。</summary>
    internal const double ReferenceWidth = 1920.0;

    /// <summary>okiaimx 预览画布用的缩放（其 <c>Sc()</c> 固定传 0.5）。</summary>
    internal const double PreviewScale = 0.5;

    /// <summary>屏幕叠加层用的缩放：屏幕宽度 / 1920。</summary>
    internal static double ScaleForWidth(double width) =>
        width <= 0 ? 1 : width / ReferenceWidth;

    internal static void Draw(Canvas canvas, double cx, double cy, CrosshairSettings settings, double unitScale)
    {
        var primary = settings.Primary;
        var outlines = primary.Outlines;

        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(CrosshairCodec.ResolveColor(primary)));
        var outlineBrush = new SolidColorBrush(Colors.Black);

        // 整体不透明度是插件扩展项，叠在 okiaimx 参数之上
        canvas.Opacity = Math.Clamp(settings.Opacity, 10, 100) / 100.0;

        DrawLines(canvas, cx, cy, unitScale, primary.Inner, primary, outlines, brush, outlineBrush);
        DrawDot(canvas, cx, cy, unitScale, primary.Dot, outlines, brush, outlineBrush);
        DrawLines(canvas, cx, cy, unitScale, primary.Outer, primary, outlines, brush, outlineBrush);
    }

    /// <summary>中心点：边长 = 大小，位置 -ceil(大小/2)；描边沿用全局粗细与不透明度。</summary>
    private static void DrawDot(
        Canvas canvas,
        double cx,
        double cy,
        double unitScale,
        CrosshairDot dot,
        CrosshairOutlines outlines,
        Brush brush,
        Brush outlineBrush)
    {
        if (!dot.Enabled)
        {
            return;
        }

        var size = dot.Width;
        var origin = -Math.Ceiling(size / 2);
        AddFill(canvas, cx + origin * unitScale, cy + origin * unitScale, size * unitScale, size * unitScale, brush, dot.Alpha);

        if (outlines.Enabled)
        {
            AddOutline(
                canvas,
                cx + origin * unitScale,
                cy + origin * unitScale,
                size * unitScale,
                size * unitScale,
                outlines.Width * unitScale,
                outlineBrush,
                outlines.Alpha);
        }
    }

    /// <summary>内线 / 外线：左右两臂 + 上下两臂（竖线可单独设长度）。</summary>
    private static void DrawLines(
        Canvas canvas,
        double cx,
        double cy,
        double unitScale,
        CrosshairLines lines,
        CrosshairPrimarySettings primary,
        CrosshairOutlines outlines,
        Brush brush,
        Brush outlineBrush)
    {
        if (!lines.Enabled)
        {
            return;
        }

        var width = lines.Width;
        var length = lines.Length;
        var offset = lines.Offset;

        // okiaimx：开火误差倍率开启且未被全局覆盖时，按"开火瞬间"的偏移多推 4 个单位
        if (lines.FireMul.Enabled && !primary.OverwriteFireMul)
        {
            offset += 4;
        }

        var parity = width % 2;                 // 奇数粗细：左/上臂补 1 单位，四臂视觉对称
        var half = Math.Floor(-width / 2);
        var vertical = lines.Vertical.Enabled ? lines.Vertical.Length : length;

        // 右臂 / 左臂
        AddBar(canvas, cx, cy, unitScale, offset, half, length, width, brush, outlineBrush, lines.Alpha, outlines);
        AddBar(canvas, cx, cy, unitScale, -offset - length - parity, half, length, width, brush, outlineBrush, lines.Alpha, outlines);

        // 下臂 / 上臂
        AddBar(canvas, cx, cy, unitScale, half, offset, width, vertical, brush, outlineBrush, lines.Alpha, outlines);
        AddBar(canvas, cx, cy, unitScale, half, -offset - vertical - parity, width, vertical, brush, outlineBrush, lines.Alpha, outlines);
    }

    private static void AddBar(
        Canvas canvas,
        double cx,
        double cy,
        double unitScale,
        double x,
        double y,
        double w,
        double h,
        Brush brush,
        Brush outlineBrush,
        double alpha,
        CrosshairOutlines outlines)
    {
        AddFill(canvas, cx + x * unitScale, cy + y * unitScale, w * unitScale, h * unitScale, brush, alpha);

        if (outlines.Enabled && w != 0 && h != 0)
        {
            AddOutline(
                canvas,
                cx + x * unitScale,
                cy + y * unitScale,
                w * unitScale,
                h * unitScale,
                outlines.Width * unitScale,
                outlineBrush,
                outlines.Alpha);
        }
    }

    private static void AddFill(Canvas canvas, double x, double y, double w, double h, Brush brush, double alpha)
    {
        if (w <= 0 || h <= 0)
        {
            return; // 0 宽/高的线条画不出来（与 canvas fillRect 一致）
        }

        var rect = new Rectangle
        {
            Width = w,
            Height = h,
            Fill = brush,
            Opacity = ClampAlpha(alpha)
        };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        canvas.Children.Add(rect);
    }

    /// <summary>
    /// 描边：紧贴填充外沿的一圈黑框（外扩 thickness 单位），等价于 canvas 的
    /// <c>strokeRect(x - L/2, y - L/2, w + L, h + L)</c>（线宽 L 跨框居中）。
    /// 这里用「外框减内框」的填充几何而不是 WPF 的 Stroke —— 实测 WPF 的
    /// Rectangle(Stretch=Fill) 会把描边画在框内（canvas 是跨框居中），直接用 Stroke 会少外扩 L/2。
    /// </summary>
    private static void AddOutline(
        Canvas canvas,
        double x,
        double y,
        double w,
        double h,
        double thickness,
        Brush brush,
        double alpha)
    {
        if (w <= 0 || h <= 0 || thickness <= 0)
        {
            return;
        }

        var outer = new RectangleGeometry(new Rect(x - thickness, y - thickness, w + thickness * 2, h + thickness * 2));
        var inner = new RectangleGeometry(new Rect(x, y, w, h));
        var frame = new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);

        var path = new Path
        {
            Data = frame,
            Fill = brush,
            Opacity = ClampAlpha(alpha)
        };
        canvas.Children.Add(path);
    }

    private static double ClampAlpha(double alpha) =>
        double.IsNaN(alpha) ? 1 : Math.Clamp(alpha, CrosshairLimits.AlphaMin, CrosshairLimits.AlphaMax);
}
