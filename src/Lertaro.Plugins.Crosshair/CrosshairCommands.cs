namespace Lertaro.Plugins.Crosshair;

/// <summary>
/// 准星子命令词：<c>&lt;触发词&gt; k</c> 打开、<c>&lt;触发词&gt; g</c> 关闭
/// （兼容 on/off 与中文 开/关、打开/关闭）。
///
/// 关键词跟着用户自己配的触发词走 —— 用户这里是 <c>zx</c>，所以是 <c>zx k</c> / <c>zx g</c>；
/// 哪天把触发词改成别的前缀，子命令会自动跟着变，不用改代码。
/// </summary>
internal static class CrosshairCommands
{
    /// <summary>打开准星的参数写法。</summary>
    private static readonly string[] ShowArguments = ["k", "on", "开", "打开", "显示"];

    /// <summary>关闭准星的参数写法。</summary>
    private static readonly string[] HideArguments = ["g", "off", "关", "关闭", "隐藏"];

    internal static bool IsShow(string argument) => Matches(ShowArguments, argument);

    internal static bool IsHide(string argument) => Matches(HideArguments, argument);

    /// <summary>
    /// 匹配触发词并拆出子命令（纯函数，方便单测）：
    /// <c>zx</c> → keyword=zx, argument=""；<c>zx k</c> → keyword=zx, argument="k"；<c>z</c> → 不匹配。
    /// </summary>
    internal static bool TryMatch(string text, IEnumerable<string> triggerKeywords, out string keyword, out string argument)
    {
        keyword = string.Empty;
        argument = string.Empty;

        foreach (var candidate in triggerKeywords)
        {
            if (string.IsNullOrEmpty(candidate))
            {
                continue;
            }

            if (string.Equals(text, candidate, StringComparison.OrdinalIgnoreCase))
            {
                keyword = candidate;
                return true;
            }

            if (text.Length > candidate.Length
                && text.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
            {
                var tail = text[candidate.Length..];

                // 标准写法："zx k"
                if (char.IsWhiteSpace(tail[0]))
                {
                    keyword = candidate;
                    argument = tail.Trim();
                    return true;
                }

                // 紧贴写法："zxk"、"zx开" —— 只在尾巴正好是命令词时才算，
                // 免得 "zxcv" 这类正常搜索词被我们抢走
                var glued = tail.Trim();
                if (IsShow(glued) || IsHide(glued))
                {
                    keyword = candidate;
                    argument = glued;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>动作/搜索项的匹配关键词：<c>zx k</c>、<c>zx on</c>、<c>zx 打开</c> …（按当前触发词生成）。</summary>
    internal static IReadOnlyList<string> ShowKeywords => Build(ShowArguments);

    /// <summary>动作/搜索项的匹配关键词：<c>zx g</c>、<c>zx off</c>、<c>zx 关闭</c> …</summary>
    internal static IReadOnlyList<string> HideKeywords => Build(HideArguments);

    private static bool Matches(string[] candidates, string argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            return false;
        }

        foreach (var candidate in candidates)
        {
            if (string.Equals(candidate, argument, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] Build(string[] arguments)
    {
        var keywords = CrosshairSettings.Current.TriggerKeywords;
        var result = new List<string>();
        foreach (var keyword in keywords)
        {
            foreach (var argument in arguments)
            {
                result.Add(keyword + " " + argument); // zx k
                result.Add(keyword + argument);       // zxk（宿主按空格分词时也能匹配）
            }
        }

        return result.Count > 0 ? [.. result] : ["cross " + arguments[0]];
    }
}
