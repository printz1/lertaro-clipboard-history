namespace Lertaro.Plugins.ClipboardHistory;

/// <summary>
/// 热键字符串解析："Ctrl+Shift+V" / "Win+V" / "Ctrl+`" 之类 → RegisterHotKey 需要的
/// 修饰键掩码 + 虚拟键码。自己解析的原因：宿主的热键控件是否认识 "Win" 未知，
/// 而用户明确想用 Win+V（实测在本机可注册成功，前提是系统剪贴板历史处于关闭状态）。
/// </summary>
internal readonly record struct HotkeyBinding(uint Modifiers, uint VirtualKey, string Normalized)
{
    internal bool IsValid => VirtualKey != 0;

    internal static bool TryParse(string? text, out HotkeyBinding binding)
    {
        binding = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        uint modifiers = 0;
        uint virtualKey = 0;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= NativeMethods.MOD_CONTROL;
                    break;

                case "alt":
                    modifiers |= NativeMethods.MOD_ALT;
                    break;

                case "shift":
                    modifiers |= NativeMethods.MOD_SHIFT;
                    break;

                case "win":
                case "windows":
                case "meta":
                case "cmd":
                    modifiers |= NativeMethods.MOD_WIN;
                    break;

                default:
                    if (TryParseKey(part, out var vk))
                    {
                        virtualKey = vk;
                    }
                    else
                    {
                        return false;
                    }

                    break;
            }
        }

        if (virtualKey == 0)
        {
            return false;
        }

        binding = new HotkeyBinding(modifiers, virtualKey, Format(modifiers, virtualKey));
        return true;
    }

    private static bool TryParseKey(string token, out uint virtualKey)
    {
        virtualKey = 0;
        var key = token.Trim();
        if (key.Length == 0)
        {
            return false;
        }

        if (key.Length == 1)
        {
            var ch = char.ToUpperInvariant(key[0]);
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                virtualKey = ch;
                return true;
            }

            virtualKey = key[0] switch
            {
                '`' => 0xC0,
                '-' => 0xBD,
                '=' => 0xBB,
                '[' => 0xDB,
                ']' => 0xDD,
                '\\' => 0xDC,
                ';' => 0xBA,
                '\'' => 0xDE,
                ',' => 0xBC,
                '.' => 0xBE,
                '/' => 0xBF,
                _ => 0
            };

            return virtualKey != 0;
        }

        if (key.Length is >= 2 and <= 3 && (key[0] is 'F' or 'f') && int.TryParse(key[1..], out var fn)
            && fn is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + fn - 1);
            return true;
        }

        virtualKey = key.ToLowerInvariant() switch
        {
            "space" => 0x20,
            "tab" => 0x09,
            "enter" or "return" => 0x0D,
            "esc" or "escape" => 0x1B,
            "backspace" => 0x08,
            "insert" => 0x2D,
            "delete" or "del" => 0x2E,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" or "pgup" => 0x21,
            "pagedown" or "pgdn" => 0x22,
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            _ => 0
        };

        return virtualKey != 0;
    }

    private static string Format(uint modifiers, uint virtualKey)
    {
        var parts = new List<string>(4);
        if ((modifiers & NativeMethods.MOD_CONTROL) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & NativeMethods.MOD_SHIFT) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & NativeMethods.MOD_ALT) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & NativeMethods.MOD_WIN) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(FormatKey(virtualKey));
        return string.Join("+", parts);
    }

    private static string FormatKey(uint virtualKey)
    {
        if (virtualKey is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return "F" + (virtualKey - 0x70 + 1);
        }

        return virtualKey switch
        {
            0x20 => "Space",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Esc",
            0x2D => "Insert",
            0x2E => "Delete",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0xC0 => "`",
            0xBD => "-",
            0xBB => "=",
            0xDB => "[",
            0xDD => "]",
            0xDC => "\\",
            0xBA => ";",
            0xDE => "'",
            0xBC => ",",
            0xBE => ".",
            0xBF => "/",
            _ => "0x" + virtualKey.ToString("X2")
        };
    }
}
