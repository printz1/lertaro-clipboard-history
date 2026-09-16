using System.IO;
using System.Text.Json;
using Lertaro.PluginSdk;

namespace Lertaro.Plugins.ClipboardHistory;

/// <summary>
/// 通知记录：浮窗几秒后就消失，用户需要事后还能看到"刚才提醒了什么"。
/// 内存保留最近 100 条，并落盘到 notifications.json（跨重启也能查）。
/// </summary>
internal static class ClipboardNotificationLog
{
    private const int Capacity = 100;
    private static readonly List<Entry> Items = [];
    private static readonly object Gate = new();

    internal sealed class Entry
    {
        public long At { get; set; }              // ticks
        public int Kind { get; set; }             // 0 = 待办提醒, 1 = 置顶到期
        public long EntryId { get; set; }
        public string Preview { get; set; } = string.Empty;
    }

    private static string Path => System.IO.Path.Combine(ClipboardSettings.SettingsDirectory, "notifications.json");

    internal static void Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return;
            }

            var loaded = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(Path));
            if (loaded is null)
            {
                return;
            }

            lock (Gate)
            {
                Items.Clear();
                Items.AddRange(loaded.Take(Capacity));
            }
        }
        catch (Exception ex)
        {
            ClipboardListener.Log("notification log load failed: " + ex.Message, LogLevel.Debug);
        }
    }

    internal static void Add(ClipboardDueEvent due)
    {
        lock (Gate)
        {
            Items.Insert(0, new Entry
            {
                At = DateTime.Now.Ticks,
                Kind = (int)due.Kind,
                EntryId = due.EntryId,
                Preview = due.Preview
            });

            while (Items.Count > Capacity)
            {
                Items.RemoveAt(Items.Count - 1);
            }
        }

        Save();
    }

    /// <summary>最近的记录快照（新→旧）。</summary>
    internal static List<Entry> Snapshot()
    {
        lock (Gate)
        {
            return [.. Items];
        }
    }

    internal static int Count
    {
        get
        {
            lock (Gate)
            {
                return Items.Count;
            }
        }
    }

    /// <summary>今天的通知条数（"一眼看到今天有多少通知"）。</summary>
    internal static int TodayCount
    {
        get
        {
            var today = DateTime.Today;
            lock (Gate)
            {
                var count = 0;
                foreach (var item in Items)
                {
                    if (new DateTime(item.At).Date == today)
                    {
                        count++;
                    }
                }

                return count;
            }
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(ClipboardSettings.SettingsDirectory);
            var path = Path;
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Snapshot()));

            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch (Exception ex)
        {
            ClipboardListener.Log("notification log save failed: " + ex.Message, LogLevel.Debug);
        }
    }
}
