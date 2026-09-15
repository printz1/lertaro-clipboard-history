using System.IO;
using Lertaro.Plugins.ClipboardHistory;

// 持久化回路探针：Add 文本×2 → AddImage → Persist → LoadOrCreate → 校验
// 只输出 ASCII，结果写 stdout，由外层重定向到文件。

var dir = Path.Combine(Path.GetTempPath(), "store-probe-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dir);
var historyPath = Path.Combine(dir, "history.json");

Console.WriteLine("== phase 1: populate ==");
var store = new ClipboardStore(5000, TimeSpan.FromDays(30));
Console.WriteLine("add alpha  -> " + store.Add("alpha test content", "procA"));
Console.WriteLine("add beta   -> " + store.Add("beta test content", "procB"));

var imgPath = Path.Combine(dir, "img.png");
File.WriteAllText(imgPath, "fake-png");
Console.WriteLine("add image  -> " + store.AddImage(imgPath, 0xDEADBEEFUL, 100, 50, 123, "procC"));

var fileList = "C:\\probe\\a.txt\nC:\\probe\\b.txt";
Console.WriteLine("add files  -> " + store.AddFiles(fileList, ClipboardIndexEntry.HashOf(fileList), 2, "procD"));

Console.WriteLine("count=" + store.Count + " dirty=" + store.Dirty);

Console.WriteLine("== phase 2: persist ==");
store.Persist(historyPath);
var json = File.ReadAllText(historyPath);
Console.WriteLine("json len=" + json.Length);
Console.WriteLine("K0(text)=" + CountOccurrences(json, "\"K\":0") + " K1(image)=" + CountOccurrences(json, "\"K\":1"));

Console.WriteLine("== phase 3: restore ==");
var store2 = ClipboardStore.LoadOrCreate(5000, TimeSpan.FromDays(30), historyPath);
Console.WriteLine("restored count=" + store2.Count);

var textOk = 0;
var imageOk = 0;
foreach (var entry in store2.Snapshot())
{
    if (entry.Kind == ClipboardEntryKind.Text)
    {
        var text = store2.TryGetText(entry.Id);
        Console.WriteLine("  text id=" + entry.Id + " preview=" + entry.Preview + " payload=" + (string.IsNullOrEmpty(text) ? "MISSING" : "ok"));
        if (!string.IsNullOrEmpty(text))
        {
            textOk++;
        }
    }
    else if (entry.Kind == ClipboardEntryKind.File)
    {
        var payload = store2.TryGetText(entry.Id);
        Console.WriteLine("  file id=" + entry.Id + " preview=" + entry.Preview + " payload=" + (string.IsNullOrEmpty(payload) ? "MISSING" : "ok"));
        if (!string.IsNullOrEmpty(payload))
        {
            imageOk++; // 复用计数：文件+图片都需各就各位
        }
    }
    else
    {
        var img = store2.TryGetImagePath(entry.Id);
        Console.WriteLine("  image id=" + entry.Id + " path=" + (string.IsNullOrEmpty(img) ? "MISSING" : "ok"));
        if (!string.IsNullOrEmpty(img))
        {
            imageOk++;
        }
    }
}

Console.WriteLine(textOk == 2 && imageOk == 2 ? "RESULT = PASS" : "RESULT = FAIL (textOk=" + textOk + " imageOk=" + imageOk + ")");

// ---- phase 3.5: from: 来源筛选 ----
Console.WriteLine("== phase 3.5: from: source filter ==");
var srcStore = new ClipboardStore(5000, TimeSpan.FromDays(30));
srcStore.Add("alpha wechat content", "Weixin");
srcStore.Add("chrome content", "Chrome");

var c1 = srcStore.Search("from:weixin", 10).Count;
var c2 = srcStore.Search("from：weixin", 10).Count;          // 全角冒号
var c3 = srcStore.Search("from:weixin alpha", 10).Count;     // 来源 + 内容
var c4 = srcStore.Search("from:chrome", 10).Count;
var c5 = srcStore.Search("from:qq", 10).Count;               // 无命中
Console.WriteLine("from:weixin=" + c1 + " from(全角)=" + c2 + " from:weixin+alpha=" + c3 + " from:chrome=" + c4 + " from:qq=" + c5);
Console.WriteLine(c1 == 1 && c2 == 1 && c3 == 1 && c4 == 1 && c5 == 0 ? "SOURCE FILTER = PASS" : "SOURCE FILTER = FAIL");

// ---- phase 4: 规模压测（5000 条典型文本的持久化/恢复开销）----
Console.WriteLine("== phase 4: scale (5000 entries x ~200 chars) ==");
var scalePath = Path.Combine(dir, "scale.json");
var scaleStore = new ClipboardStore(5000, TimeSpan.FromDays(30));

var rng = new Random(20260916);
for (var i = 0; i < 5000; i++)
{
    var chars = new char[200];
    for (var c = 0; c < chars.Length; c++)
    {
        chars[c] = (char)('a' + rng.Next(26));
    }

    scaleStore.Add(new string(chars), "bench");
}

Console.WriteLine("populated count=" + scaleStore.Count);

var sw = System.Diagnostics.Stopwatch.StartNew();
scaleStore.Persist(scalePath);
sw.Stop();
Console.WriteLine("persist 5000 entries: " + sw.ElapsedMilliseconds + " ms");

Console.WriteLine("json size=" + (new FileInfo(scalePath).Length / 1024.0).ToString("0") + " KB");

sw.Restart();
var restored2 = ClipboardStore.LoadOrCreate(5000, TimeSpan.FromDays(30), scalePath);
sw.Stop();
Console.WriteLine("restore 5000 entries: " + sw.ElapsedMilliseconds + " ms (count=" + restored2.Count + ")");

// 二次 Persist（去重后已置顶的稳态改写，内容相同）
sw.Restart();
restored2.Persist(scalePath);
sw.Stop();
Console.WriteLine("re-persist (unchanged data): " + sw.ElapsedMilliseconds + " ms");

try
{
    Directory.Delete(dir, true);
}
catch
{
    // 临时目录删不掉无妨
}

static int CountOccurrences(string text, string needle)
{
    var count = 0;
    var index = 0;
    while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
    {
        count++;
        index += needle.Length;
    }

    return count;
}
