using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// 诊断工具：把当前剪贴板的全部格式、文本内容、序列号、占用进程列出来。
// 用来回答"剪贴板上到底有什么、是不是被别的程序抢占了"。

const uint CF_TEXT = 1;
const uint CF_UNICODETEXT = 13;
const uint CF_HDROP = 15;

[DllImport("user32.dll", SetLastError = true)]
static extern uint GetClipboardSequenceNumber();

[DllImport("user32.dll", SetLastError = true)]
static extern bool OpenClipboard(IntPtr hWndNewOwner);

[DllImport("user32.dll", SetLastError = true)]
static extern bool CloseClipboard();

[DllImport("user32.dll", SetLastError = true)]
static extern bool IsClipboardFormatAvailable(uint format);

[DllImport("user32.dll", SetLastError = true)]
static extern IntPtr GetClipboardData(uint uFormat);

[DllImport("user32.dll", SetLastError = true)]
static extern IntPtr GetClipboardOwner();

[DllImport("user32.dll", SetLastError = true)]
static extern uint EnumClipboardFormats(uint format);

[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
static extern int GetClipboardFormatNameW(uint format, StringBuilder lpszFormatName, int cchMaxCount);

[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
static extern uint RegisterClipboardFormatW(string lpszFormat);

[DllImport("user32.dll", SetLastError = true)]
static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

[DllImport("kernel32.dll")]
static extern IntPtr GlobalLock(IntPtr hMem);

[DllImport("kernel32.dll")]
static extern bool GlobalUnlock(IntPtr hMem);

[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
static extern int lstrlenW(IntPtr lpString);

var lines = new List<string>();
lines.Add("=== 剪贴板诊断 ===");
lines.Add("序列号 = " + GetClipboardSequenceNumber());

if (!OpenClipboard(IntPtr.Zero))
{
    lines.Add("OpenClipboard 失败，win32 error = " + Marshal.GetLastWin32Error());
    lines.Add("=> 剪贴板被别的进程独占中");
    Write(lines);
    return;
}

try
{
    var owner = GetClipboardOwner();
    var ownerText = owner == IntPtr.Zero ? "(无 owner / 由系统持有)" : "hwnd=0x" + owner.ToString("X");
    if (owner != IntPtr.Zero)
    {
        _ = GetWindowThreadProcessId(owner, out var pid);
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
            ownerText += " pid=" + pid + " proc=" + proc.ProcessName;
        }
        catch
        {
            ownerText += " pid=" + pid + " (进程已退出)";
        }
    }

    lines.Add("owner = " + ownerText);

    lines.Add("");
    lines.Add("--- 常见格式 ---");
    void Check(string label, uint format)
    {
        lines.Add(string.Format("  {0,-42} 可用={1}", label, IsClipboardFormatAvailable(format)));
    }

    Check("CF_TEXT (1)", CF_TEXT);
    Check("CF_UNICODETEXT (13)", CF_UNICODETEXT);
    Check("CF_HDROP (15, 文件列表)", CF_HDROP);
    Check("HTML Format", RegisterClipboardFormatW("HTML Format"));
    Check("PNG", RegisterClipboardFormatW("PNG"));
    Check("ExcludeClipboardContentFromMonitorProcessing", RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing"));
    Check("Clipboard Viewer Ignore", RegisterClipboardFormatW("Clipboard Viewer Ignore"));
    Check("CanIncludeInClipboardHistory", RegisterClipboardFormatW("CanIncludeInClipboardHistory"));
    Check("CanUploadToCloudClipboard", RegisterClipboardFormatW("CanUploadToCloudClipboard"));
    Check("Preferred DropEffect", RegisterClipboardFormatW("Preferred DropEffect"));

    lines.Add("");
    lines.Add("--- 全部已注册格式枚举 ---");
    var format = 0u;
    var guard = 0;
    while ((format = EnumClipboardFormats(format)) != 0 && guard++ < 60)
    {
        string name;
        if (format < 0xC000)
        {
            name = format switch
            {
                CF_TEXT => "CF_TEXT",
                CF_UNICODETEXT => "CF_UNICODETEXT",
                CF_HDROP => "CF_HDROP",
                2 => "CF_BITMAP",
                3 => "CF_METAFILEPICT",
                8 => "CF_DIB",
                14 => "CF_ENHMETAFILE",
                17 => "CF_DIBV5",
                _ => "标准格式 " + format
            };
        }
        else
        {
            var sb = new StringBuilder(256);
            _ = GetClipboardFormatNameW(format, sb, sb.Capacity);
            name = sb.ToString();
        }

        lines.Add(string.Format("  0x{0:X4}  {1}", format, name));
    }

    lines.Add("");
    lines.Add("--- 文本内容 ---");
    if (IsClipboardFormatAvailable(CF_UNICODETEXT))
    {
        var handle = GetClipboardData(CF_UNICODETEXT);
        if (handle != IntPtr.Zero)
        {
            var pointer = GlobalLock(handle);
            if (pointer != IntPtr.Zero)
            {
                try
                {
                    var length = lstrlenW(pointer);
                    lines.Add("  lstrlenW = " + length);
                    lines.Add("  内容 = " + (length > 0 ? "'" + Marshal.PtrToStringUni(pointer, Math.Min(length, 200)) + "'" : "(空串)"));
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
            else
            {
                lines.Add("  GlobalLock 失败");
            }
        }
        else
        {
            lines.Add("  GetClipboardData 返回 0");
        }
    }
    else
    {
        lines.Add("  剪贴板上没有 CF_UNICODETEXT");
    }
}
finally
{
    CloseClipboard();
}

Write(lines);

static void Write(List<string> lines)
{
    var path = Path.Combine(
        Environment.GetEnvironmentVariable("TEMP") ?? AppContext.BaseDirectory,
        "clipboard-probe.txt");
    File.WriteAllLines(path, lines);
    Console.WriteLine(string.Join(Environment.NewLine, lines));
}
