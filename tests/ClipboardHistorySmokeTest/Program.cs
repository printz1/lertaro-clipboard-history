using System.Diagnostics;
using System.IO;
using Lertaro.Plugins.ClipboardHistory;
using Lertaro.Plugins.ClipboardHistory.SmokeTest;

var lines = new List<string>();
var failures = new List<string>();
var resultPath = Path.Combine(
    Environment.GetEnvironmentVariable("TEMP") ?? AppContext.BaseDirectory,
    "clipboard-smoke-result.txt");

try
{
    var original = ClipboardReader.Read().Text;
    lines.Add("baseline: clipboard text length = " + (original?.Length ?? 0));

    var store = new ClipboardStore(50, TimeSpan.FromDays(30));
    using var listener = new ClipboardListener(store);

    var started = listener.Start(TimeSpan.FromMilliseconds(1500));
    lines.Add("listener start (event-driven path) = " + started);
    if (!started)
    {
        failures.Add("listener failed to start");
        Finish();
        return;
    }

    var wrote = SetClipboardWithRetry("smoke-one", excluded: false, out var note1);
    lines.Add("case 1: fixture write ok = " + wrote + (wrote ? "" : " (" + note1 + ")"));

    if (!wrote)
    {
        lines.Add("case 1: SKIPPED - fixture could not place text on the clipboard");
    }
    else if (!WaitFor(() => listener.NotificationCount > 0, 3000))
    {
        failures.Add("case 1: listener received no WM_CLIPBOARDUPDATE");
    }
    else if (!WaitFor(() => listener.CaptureCount >= 1, 8000))
    {
        failures.Add("case 1: listener never captured text [notifications=" + listener.NotificationCount
            + " reads=" + listener.ReadCount
            + " emptyReads=" + listener.EmptyReadCount
            + " captured=" + listener.CaptureCount + "]");
    }
    else
    {
        lines.Add("case 1: notifications=" + listener.NotificationCount
            + ", captured=" + listener.CaptureCount
            + ", top = " + store.Snapshot()[0].Preview);
    }

    // 用例 2：带排除格式的内容不允许进历史（写入夹具不可靠，只在确认真的写进去后断言）
    if (!SetClipboardWithRetry("SECRET-SHOULD-BE-IGNORED", excluded: true, out var note2))
    {
        lines.Add("case 2: SKIPPED - fixture could not place excluded content (" + note2 + ")");
    }
    else
    {
        Thread.Sleep(600);
        lines.Add("case 2: excluded content written, store count = " + store.Count
            + " (expect unchanged), excludedReads = " + listener.ExcludedCount);
        if (listener.ExcludedCount == 0)
        {
            failures.Add("case 2: excluded content was not recognised as sensitive");
        }
    }

    // 用例 3：哈希去重 —— 对同一个文本重复 add，不应产生新条目
    var beforeCount = store.Count;
    var firstText = store.Snapshot().Count > 0 ? store.Snapshot()[0].Preview : string.Empty;
    _ = store.Add(firstText, "fixture");
    lines.Add("case 3: duplicate add kept count = " + store.Count + " (was " + beforeCount + ")");

    // 用例 4：快照缓存 —— 内容未变时两次调用应返回同一个数组实例
    var a = store.Snapshot();
    var b = store.Snapshot();
    lines.Add("case 4: snapshot cached (same instance) = " + ReferenceEquals(a, b));
    if (!ReferenceEquals(a, b))
    {
        failures.Add("case 4: snapshot cache not working");
    }

    // 用例 5：热键解析（纯逻辑，确定性强）
    CheckHotkey(lines, failures, "Win+V", expectValid: true, expectWin: true, expectKey: 0x56);
    CheckHotkey(lines, failures, "Ctrl+Shift+V", expectValid: true, expectWin: false, expectKey: 0x56);
    CheckHotkey(lines, failures, "Ctrl+`", expectValid: true, expectWin: false, expectKey: 0xC0);
    CheckHotkey(lines, failures, "Win+alt+v", expectValid: true, expectWin: true, expectKey: 0x56);
    CheckHotkey(lines, failures, "Ctrl+", expectValid: false, expectWin: false, expectKey: 0);
    CheckHotkey(lines, failures, "NotAKey+Q", expectValid: false, expectWin: false, expectKey: 0);

    if (!string.IsNullOrEmpty(original))
    {
        _ = SetClipboardWithRetry(original, excluded: false, out _);
        lines.Add("attempted to restore original clipboard text");
    }

    ScaleTest(lines, failures);

    Finish();
}
catch (Exception ex)
{
    failures.Add("unexpected exception: " + ex);
    Finish();
}

void Finish()
{
    lines.Add(failures.Count == 0 ? "RESULT = PASS" : "RESULT = FAIL");
    foreach (var f in failures)
    {
        lines.Add("  - " + f);
    }

    File.WriteAllLines(resultPath, lines);
    Console.WriteLine(string.Join(Environment.NewLine, lines));
}

// 模拟长期日常使用：验证淘汰策略、内存占用与检索延迟
static void ScaleTest(List<string> lines, List<string> failures)
{
    const int Capacity = 5000;
    const int TotalCaptures = 21600; // 约 6 个月 × 每天 120 次复制

    var store = new ClipboardStore(Capacity, TimeSpan.FromDays(30));
    var filler = new string('x', 300);

    var beforeMemory = GC.GetTotalMemory(true);

    var sw = Stopwatch.StartNew();
    for (var i = 0; i < TotalCaptures; i++)
    {
        _ = store.Add("clip-" + i.ToString("D6") + " " + filler, "chrome");
    }

    sw.Stop();
    lines.Add("scale: captured " + TotalCaptures + " items in " + sw.ElapsedMilliseconds + " ms"
        + " (avg " + (sw.Elapsed.TotalMilliseconds * 1000 / TotalCaptures).ToString("F1") + " us/capture)");
    lines.Add("scale: retained count = " + store.Count + " (capacity " + Capacity + ")");
    if (store.Count != Capacity)
    {
        failures.Add("scale: capacity trimming failed, count = " + store.Count);
    }

    var afterMemory = GC.GetTotalMemory(true);
    lines.Add("scale: managed memory delta = "
        + ((afterMemory - beforeMemory) / 1024.0 / 1024.0).ToString("F1") + " MB"
        + " (index + payload of " + store.Count + " items)");
    lines.Add("scale: payload chars = " + store.PayloadChars.ToString("N0"));

    // 热路径：无过滤，取最新 30 条
    const int Iterations = 500;
    var head = store.Search(null, 30, null);
    var swHead = Stopwatch.StartNew();
    for (var i = 0; i < Iterations; i++)
    {
        _ = store.Search(null, 30, null);
    }

    swHead.Stop();
    lines.Add("scale: empty-filter query avg = "
        + (swHead.Elapsed.TotalMilliseconds / Iterations).ToString("F3") + " ms"
        + " (results " + head.Count + ")");
    if (head.Count != 30)
    {
        failures.Add("scale: empty-filter query returned " + head.Count + " results");
    }

    // 最坏路径：过滤词命中最旧的一条，需要扫完整个索引
    var swWorst = Stopwatch.StartNew();
    for (var i = 0; i < Iterations; i++)
    {
        _ = store.Search("clip-016601", 30, null);
    }

    swWorst.Stop();
    lines.Add("scale: worst-case (single old match) query avg = "
        + (swWorst.Elapsed.TotalMilliseconds / Iterations).ToString("F3") + " ms");

    // 无命中：同样要扫完
    var swMiss = Stopwatch.StartNew();
    for (var i = 0; i < Iterations; i++)
    {
        _ = store.Search("zzz-not-here", 30, null);
    }

    swMiss.Stop();
    lines.Add("scale: no-match query avg = "
        + (swMiss.Elapsed.TotalMilliseconds / Iterations).ToString("F3") + " ms");

    var oldMatch = store.Search("clip-016601", 5, null);
    lines.Add("scale: oldest-window search hit = " + oldMatch.Count + " item(s)");
    if (oldMatch.Count == 0)
    {
        failures.Add("scale: search failed to reach retained old entries");
    }

    // 去重仍然成立
    var countBefore = store.Count;
    _ = store.Add("clip-021599 " + filler, "chrome");
    lines.Add("scale: duplicate add kept count = " + store.Count + " (was " + countBefore + ")");
    if (store.Count != countBefore)
    {
        failures.Add("scale: duplicate detection failed at scale");
    }

    // 全文可按 id 取回
    var newest = store.Snapshot()[0];
    var text = store.TryGetText(newest.Id);
    if (string.IsNullOrEmpty(text))
    {
        failures.Add("scale: payload lookup returned nothing for the newest entry");
    }
    else
    {
        lines.Add("scale: payload lookup ok, length = " + text.Length);
    }
}

static bool WaitFor(Func<bool> condition, int timeoutMs)
{
    var deadline = Environment.TickCount64 + timeoutMs;
    while (Environment.TickCount64 < deadline)
    {
        if (condition())
        {
            return true;
        }

        Thread.Sleep(50);
    }

    return condition();
}

// 写剪贴板会和其他进程抢锁，而且 GMEM 分配偶发会写出"格式在但内容为空"的条目，
// 所以这里带写后校验 + 重试：写入成功不等于内容真的可读。
static bool SetClipboardWithRetry(string text, bool excluded, out string note)
{
    note = string.Empty;

    for (var attempt = 1; attempt <= 8; attempt++)
    {
        if (ClipboardTestHelper.SetText(text, excluded))
        {
            var read = ClipboardReader.Read();
            var ok = excluded ? read.IsExcluded : string.Equals(read.Text, text, StringComparison.Ordinal);

            if (ok)
            {
                if (attempt > 1)
                {
                    note = "succeeded on attempt " + attempt;
                }

                return true;
            }

            note = "write returned true but read-back was "
                + (excluded ? "not excluded" : (read.Text is null ? "empty" : "'" + read.Text + "'"));
        }
        else
        {
            note = "OpenClipboard/SetClipboardData failed";
        }

        Thread.Sleep(150);
    }

    return false;
}

static void CheckHotkey(List<string> lines, List<string> failures, string text,
    bool expectValid, bool expectWin, uint expectKey)
{
    var valid = HotkeyBinding.TryParse(text, out var binding);

    if (valid != expectValid)
    {
        failures.Add("hotkey '" + text + "': expected valid=" + expectValid + " but got " + valid);
        return;
    }

    if (!valid)
    {
        lines.Add("hotkey '" + text + "' -> rejected as expected");
        return;
    }

    var hasWin = (binding.Modifiers & NativeMethods.MOD_WIN) != 0;
    if (hasWin != expectWin || binding.VirtualKey != expectKey)
    {
        failures.Add("hotkey '" + text + "': parsed as win=" + hasWin + " vk=0x"
            + binding.VirtualKey.ToString("X2") + " (expected win=" + expectWin
            + " vk=0x" + expectKey.ToString("X2") + ")");
        return;
    }

    lines.Add("hotkey '" + text + "' -> " + binding.Normalized
        + " (mods=0x" + binding.Modifiers.ToString("X2")
        + ", vk=0x" + binding.VirtualKey.ToString("X2") + ")");
}
