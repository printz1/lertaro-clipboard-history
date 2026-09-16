using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lertaro.PluginSdk.Services;

namespace Lertaro.Plugins.Crosshair;

/// <summary>
/// 准星插件设置。参数体系 1:1 对齐 okiaimx.com 的准星编辑器（即 VALORANT 的准星参数）：
/// 颜色（8 预设 + 自定义）/ 描边 / 中心点 / 内部线条 / 外部线条，
/// 并完整支持 VALORANT 准星代码的导入与导出（见 <see cref="CrosshairCodec"/>）。
///
/// 注：<see cref="General"/> / <see cref="Primary"/> 之外的字段是本插件的「扩展项」
/// （okiaimx 里没有、游戏里也不需要）：总开关、位置微调、整体不透明度、触发词、全局热键。
/// 持久化到自己目录下的 settings.json；改动保存后由插件实时重绘。
/// </summary>
internal sealed class CrosshairSettings
{
    private const string FileName = "settings.json";
    private const string FolderName = "Lertaro.Plugins.Crosshair";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private static readonly object Gate = new();
    private static CrosshairSettings? _current;

    internal static CrosshairSettings Current
    {
        get
        {
            lock (Gate)
            {
                return _current ??= Load();
            }
        }
    }

    // ---- 扩展项（okiaimx 没有的部分）----

    /// <summary>是否显示准星（总开关）。</summary>
    public bool Visible { get; set; }

    /// <summary>水平位置微调（像素，0 = 屏幕正中）。</summary>
    public int OffsetX { get; set; }

    /// <summary>垂直位置微调（像素）。</summary>
    public int OffsetY { get; set; }

    /// <summary>整体不透明度（10-100%），在 okiaimx 参数之上再叠一层。</summary>
    public int Opacity { get; set; } = 100;

    /// <summary>触发词。</summary>
    public List<string> TriggerKeywords { get; set; } = ["准星", "cross"];

    /// <summary>全局热键（游戏内一键开关准星），如 Ctrl+Alt+C。</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+C";

    // ---- okiaimx / VALORANT 参数 ----

    /// <summary>通用项（代码里的 P:f / P:s）。</summary>
    public CrosshairGeneralSettings General { get; set; } = new();

    /// <summary>准星主体（颜色、描边、中心点、内线、外线）。</summary>
    public CrosshairPrimarySettings Primary { get; set; } = new();

    /// <summary>当前参数对应的 VALORANT 代码。</summary>
    internal string Code => CrosshairCodec.Export(this);

    /// <summary>是否已偏离默认准星（对应 okiaimx 的「默认 / 自定义」标记）。</summary>
    internal bool IsCustomized => !string.Equals(Code, CrosshairCodec.DefaultCode, StringComparison.Ordinal);

    internal CrosshairSettings Clone() => new()
    {
        Visible = Visible,
        OffsetX = OffsetX,
        OffsetY = OffsetY,
        Opacity = Opacity,
        TriggerKeywords = [.. TriggerKeywords],
        Hotkey = Hotkey,
        General = General.Copy(),
        Primary = Primary.Copy()
    };

    /// <summary>把暂存设置落回当前设置：先收敛到合法范围，再落盘。</summary>
    internal void ApplyFrom(CrosshairSettings staged)
    {
        Visible = staged.Visible;
        OffsetX = Math.Clamp(staged.OffsetX, -4096, 4096);
        OffsetY = Math.Clamp(staged.OffsetY, -4096, 4096);
        Opacity = Math.Clamp(staged.Opacity, 10, 100);
        TriggerKeywords = NormalizeKeywords(staged.TriggerKeywords);
        Hotkey = staged.Hotkey?.Trim() ?? string.Empty;
        General.CopyFrom(staged.General);
        Primary.CopyFrom(staged.Primary);
        CrosshairCodec.Sanitize(this);
        Save();
    }

    /// <summary>恢复默认准星（对应 okiaimx 的「恢复默认」按钮；不动扩展项）。原地覆盖，不换对象。</summary>
    internal void ResetCrosshair()
    {
        var defaults = new CrosshairSettings();
        General.CopyFrom(defaults.General);
        Primary.CopyFrom(defaults.Primary);
    }

    /// <summary>代码是否看起来像 VALORANT 准星代码（okiais 的判据：以 0 开头）。</summary>
    internal static bool IsPlausibleCode(string? code) =>
        !string.IsNullOrWhiteSpace(code) && code.TrimStart().StartsWith('0');

    internal static List<string> NormalizeKeywords(IEnumerable<string>? keywords)
    {
        var result = new List<string>();
        if (keywords is not null)
        {
            foreach (var raw in keywords)
            {
                var value = raw?.Trim();
                if (string.IsNullOrEmpty(value) || result.Contains(value))
                {
                    continue;
                }

                result.Add(value);
                if (result.Count >= 8)
                {
                    break;
                }
            }
        }

        return result.Count > 0 ? result : ["准星", "cross"];
    }

    internal static string SettingsDirectory
    {
        get
        {
            string? root = null;
            try
            {
                root = UserDataService.GetUserDataDirectory();
            }
            catch
            {
                // 宿主服务不可用时退回 %APPDATA%\Lertaro
            }

            if (string.IsNullOrEmpty(root))
            {
                root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Lertaro");
            }

            return Path.Combine(root, FolderName);
        }
    }

    private static CrosshairSettings Load()
    {
        try
        {
            var path = Path.Combine(SettingsDirectory, FileName);
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<CrosshairSettings>(File.ReadAllText(path), JsonOptions);
                if (loaded is not null)
                {
                    loaded.TriggerKeywords = NormalizeKeywords(loaded.TriggerKeywords);
                    loaded.Opacity = Math.Clamp(loaded.Opacity, 10, 100);
                    loaded.OffsetX = Math.Clamp(loaded.OffsetX, -4096, 4096);
                    loaded.OffsetY = Math.Clamp(loaded.OffsetY, -4096, 4096);
                    loaded.General ??= new CrosshairGeneralSettings();
                    loaded.Primary ??= new CrosshairPrimarySettings();
                    loaded.Primary.HexColor ??= new CrosshairHexColor();
                    loaded.Primary.Outlines ??= new CrosshairOutlines();
                    loaded.Primary.Dot ??= new CrosshairDot();
                    loaded.Primary.Inner ??= CrosshairLines.CreateInner();
                    loaded.Primary.Outer ??= CrosshairLines.CreateOuter();
                    loaded.Primary.Inner.Vertical ??= new CrosshairVertical();
                    loaded.Primary.Inner.MoveMul ??= new CrosshairErrorMul();
                    loaded.Primary.Inner.FireMul ??= new CrosshairErrorMul();
                    loaded.Primary.Outer.Vertical ??= new CrosshairVertical();
                    loaded.Primary.Outer.MoveMul ??= new CrosshairErrorMul();
                    loaded.Primary.Outer.FireMul ??= new CrosshairErrorMul();
                    CrosshairCodec.Sanitize(loaded);
                    return loaded;
                }
            }
        }
        catch
        {
            // 损坏的设置按默认值启动，不带病运行
        }

        return new CrosshairSettings();
    }

    internal void Save()
    {
        try
        {
            var dir = SettingsDirectory;
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, FileName);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));

            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch
        {
            // 写盘失败不阻断准星显示
        }
    }
}
