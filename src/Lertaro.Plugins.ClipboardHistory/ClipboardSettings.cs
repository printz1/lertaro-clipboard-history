using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lertaro.PluginSdk;
using Lertaro.PluginSdk.Services;

namespace Lertaro.Plugins.ClipboardHistory;

/// <summary>
/// 插件设置。持久化到自己目录下的 settings.json，不依赖宿主的写入语义：
/// 读的路径 HostValue 优先（若宿主确实持久化了），否则用自己的文件。
/// 配置面板打开期间改的是 <see cref="Clone"/> 出来的副本，点"应用/确定"才写盘，
/// 关闭即放弃 —— 与宿主配置面板的交互约定保持一致。
/// </summary>
internal sealed class ClipboardSettings
{
    private const string FileName = "settings.json";
    private const string FolderName = "Lertaro.Plugins.ClipboardHistory";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private static readonly object Gate = new();
    private static ClipboardSettings? _current;

    /// <summary>进程级当前设置。</summary>
    internal static ClipboardSettings Current
    {
        get
        {
            lock (Gate)
            {
                return _current ??= Load();
            }
        }
    }

    /// <summary>默认热键。刻意不占 Win+V —— 那是 Windows 自带剪贴板历史的键位。</summary>
    internal const string DefaultHotkey = "Ctrl+Shift+V";

    /// <summary>默认 Ctrl+Shift+V；刻意不占 Win+V（系统剪贴板历史）。</summary>
    public string Hotkey { get; set; } = DefaultHotkey;

    public List<string> TriggerKeywords { get; set; } = ["cb", "clip"];

    public int MaxEntries { get; set; } = 5000;

    /// <summary>保留天数。0 = 不限时（受 MaxEntries 兜底）。</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>暂停记录：临时处理敏感内容时用，不需要卸载插件。</summary>
    public bool PauseCapture { get; set; }

    /// <summary>在副标题里显示来源进程名。</summary>
    public bool ShowSourceProcess { get; set; } = true;

    /// <summary>是否记录图片。关闭后截图/复制图片不进历史。</summary>
    public bool CaptureImages { get; set; } = true;

    /// <summary>单张图片大小上限（MB），超过直接丢弃，防止巨型截图拖慢采集。</summary>
    public int MaxImageMB { get; set; } = 20;

    /// <summary>
    /// 历史持久化：文本全文（明文 JSON）+ 图片引用落盘，重启后恢复。
    /// 关闭则回到"仅内存，重启即清空"的行为，图片缓存也随之清空。
    /// </summary>
    public bool PersistHistory { get; set; } = true;

    /// <summary>悬停预览浮窗：鼠标停在条目上弹出全文/大图。关闭后悬停无浮窗。</summary>
    public bool HoverPreview { get; set; } = true;

    /// <summary>提醒提示音（系统"提醒"音，跟随系统音量与静音）。</summary>
    public bool ReminderSound { get; set; } = true;

    /// <summary>待办按紧急度排序：临近时间越近越靠上（已错过的排最顶）。</summary>
    public bool SortTodosByDue { get; set; } = true;

    internal ClipboardSettings Clone() => new()
    {
        Hotkey = Hotkey,
        TriggerKeywords = [.. TriggerKeywords],
        MaxEntries = MaxEntries,
        RetentionDays = RetentionDays,
        PauseCapture = PauseCapture,
        ShowSourceProcess = ShowSourceProcess,
        CaptureImages = CaptureImages,
        MaxImageMB = MaxImageMB,
        PersistHistory = PersistHistory,
        HoverPreview = HoverPreview,
        ReminderSound = ReminderSound,
        SortTodosByDue = SortTodosByDue
    };

    /// <summary>把副本的规范化结果写回当前设置并落盘。</summary>
    internal void ApplyFrom(ClipboardSettings staged)
    {
        lock (Gate)
        {
            Hotkey = string.IsNullOrWhiteSpace(staged.Hotkey) ? DefaultHotkey : staged.Hotkey.Trim();
            TriggerKeywords = NormalizeKeywords(staged.TriggerKeywords);
            MaxEntries = Math.Clamp(staged.MaxEntries, 50, 200_000);
            RetentionDays = Math.Clamp(staged.RetentionDays, 0, 3650);
            PauseCapture = staged.PauseCapture;
            ShowSourceProcess = staged.ShowSourceProcess;
            CaptureImages = staged.CaptureImages;
            MaxImageMB = Math.Clamp(staged.MaxImageMB, 1, 200);
            PersistHistory = staged.PersistHistory;
            HoverPreview = staged.HoverPreview;
            ReminderSound = staged.ReminderSound;
            SortTodosByDue = staged.SortTodosByDue;
            _current = this;
        }

        Save();
    }

    internal static List<string> NormalizeKeywords(IEnumerable<string>? keywords)
    {
        var result = new List<string>();
        if (keywords is not null)
        {
            foreach (var raw in keywords)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var keyword = raw.Trim();
                if (!result.Contains(keyword, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(keyword);
                }
            }
        }

        return result.Count == 0 ? ["cb", "clip"] : result;
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
                // 宿主未接线时退回默认用户目录
            }

            root ??= Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Lertaro");

            return Path.Combine(root, FolderName);
        }
    }

    private static ClipboardSettings Load()
    {
        var path = Path.Combine(SettingsDirectory, FileName);
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<ClipboardSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    loaded.TriggerKeywords = NormalizeKeywords(loaded.TriggerKeywords);
                    loaded.MaxEntries = Math.Clamp(loaded.MaxEntries, 50, 200_000);
                    loaded.RetentionDays = Math.Clamp(loaded.RetentionDays, 0, 3650);
                    loaded.MaxImageMB = Math.Clamp(loaded.MaxImageMB, 1, 200);
                    if (string.IsNullOrWhiteSpace(loaded.Hotkey))
                    {
                        loaded.Hotkey = DefaultHotkey;
                    }

                    ClipboardListener.Log(
                        "settings loaded from " + path,
                        LogLevel.Debug);
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            ClipboardListener.Log("settings load failed, using defaults: " + ex.Message, LogLevel.Warn);
        }

        return new ClipboardSettings();
    }

    internal void Save()
    {
        try
        {
            var dir = SettingsDirectory;
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, FileName);
            var temp = path + ".tmp";
            var json = JsonSerializer.Serialize(this, JsonOptions);

            File.WriteAllText(temp, json);

            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }

            ClipboardListener.Log("settings saved to " + path, LogLevel.Info);
        }
        catch (Exception ex)
        {
            ClipboardListener.Log("settings save failed: " + ex.Message, LogLevel.Warn);
        }
    }
}
