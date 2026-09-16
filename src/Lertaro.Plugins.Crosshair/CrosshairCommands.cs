namespace Lertaro.Plugins.Crosshair;

/// <summary>
/// 准星子命令词：<c>&lt;触发词&gt; k</c> 打开、<c>&lt;触发词&gt; g</c> 关闭
/// （兼容 on/off 与中文 开/关、打开/关闭）。
///
/// 关键词跟着用户自己配的触发词走 —— 用户这里是 <c>zx</c>，所以是 <c>zx k</c> / <c>zx g</c>；
/// 哪天把触发词改成别的前缀，子命令会自动跟着变，不用改代码。
///
/// 性能约定（对照开发指南）：即时结果与动作列表的 Keywords 都在「打字 / 渲染」热路径上被
/// <b>同步</b>调用，所以这里把关键词数组缓存起来，绝不在热路径上读盘或反复分配。
/// </summary>
internal static class CrosshairCommands
{
    /// <summary>打开准星的参数写法。</summary>
    private static readonly string[] ShowArguments = ["k", "on", "开", "打开", "显示"];

    /// <summary>关闭准星的参数写法。</summary>
    private static readonly string[] HideArguments = ["g", "off", "关", "关闭", "隐藏"];

    private static readonly object Gate = new();
    private static string[] _cachedTriggers = [];
    private static string[]? _showKeywords;
    private static string[]? _hideKeywords;

    internal static bool IsShow(string argument) => Matches(ShowArguments, argument);

    internal static bool IsHide(string argument) => Matches(HideArguments, argument);

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

    /// <summary>
    /// 匹配触发词并拆出子命令（纯函数，方便单测）：
    /// <c>zx</c> → keyword=zx, argument=""；<c>zx k</c> / <c>zxk</c> → argument="k"；<c>z</c> → 不匹配。
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

    /// <summary>动作菜单项的匹配关键词（单 token 形式，如 <c>zxk</c>、<c>zx开</c>）。</summary>
    internal static IReadOnlyList<string> ShowKeywords => Cached(ShowArguments);

    /// <summary>动作菜单项的匹配关键词（单 token 形式，如 <c>zxg</c>、<c>zx关</c>）。</summary>
    internal static IReadOnlyList<string> HideKeywords => Cached(HideArguments);

    private static string[] Cached(string[] arguments)
    {
        lock (Gate)
        {
            var triggers = CrosshairSettings.Current.TriggerKeywords;
            if (_showKeywords is null || _hideKeywords is null || !Same(triggers, _cachedTriggers))
            {
                _cachedTriggers = [.. triggers];
                _showKeywords = Build(_cachedTriggers, ShowArguments);
                _hideKeywords = Build(_cachedTriggers, HideArguments);
            }

            return ReferenceEquals(arguments, ShowArguments) ? _showKeywords : _hideKeywords;
        }
    }

    private static bool Same(IReadOnlyList<string> current, string[] cached)
    {
        if (current.Count != cached.Length)
        {
            return false;
        }

        for (var i = 0; i < cached.Length; i++)
        {
            if (!string.Equals(current[i], cached[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 只生成"紧贴"单 token 形式（<c>zxk</c>）：开发指南只承诺 <c>Keywords</c> 是字符串列表，
    /// 没有承诺带空格的词组会被匹配；带空格的 <c>zx k</c> 由即时结果那条路（拿到的是原始查询串）负责。
    /// </summary>
    private static string[] Build(string[] triggerKeywords, string[] arguments)
    {
        var result = new List<string>(triggerKeywords.Length * arguments.Length);
        foreach (var keyword in triggerKeywords)
        {
            foreach (var argument in arguments)
            {
                result.Add(keyword + argument);
            }
        }

        return result.Count > 0 ? [.. result] : ["cross" + arguments[0]];
    }
}
