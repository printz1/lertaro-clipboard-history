using Lertaro.PluginSdk;
using Lertaro.PluginSdk.Services;

namespace Lertaro.Plugins.Crosshair;

/// <summary>
/// 插件内的两个统一入口，对照开发指南：
/// ① 文案 —— 走 <see cref="TranslationService"/>，键缺失或尚未加载时回退到中文原文，
///    这样「设置 → 插件」里的名称/描述/动作名能跟着界面语言变，又不会因为加载顺序问题显示成裸键名；
/// ② 日志 —— 走宿主 <see cref="Logger"/>（会进 app.log 与「设置 → 运行状态」），
///    而不是 <c>System.Diagnostics.Debug.WriteLine</c>（那条路在宿主里什么都看不到）。
/// </summary>
internal static class CrosshairText
{
    internal static string Get(string key, string fallback)
    {
        try
        {
            var value = TranslationService.Get(key);
            return string.IsNullOrWhiteSpace(value)
                || string.Equals(value, key, StringComparison.Ordinal)
                ? fallback
                : value;
        }
        catch
        {
            return fallback;
        }
    }

    internal static void Log(string message, LogLevel level = LogLevel.Info)
    {
        try
        {
            Logger.Log("[Crosshair] " + message, level);
        }
        catch
        {
            // 宿主日志通道不可用时静默降级：绝不让日志把功能带崩
        }
    }
}
