using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

// 探针目标：验证在 .NET 10 / Windows 上发送系统 Toast 通知的可行性。
// 分别尝试两个 AUMID：
//   1) PowerShell 的已注册 AUMID（无需安装步骤，但通知会显示为 Windows PowerShell）
//   2) 自定义 AUMID（未注册时预期抛异常 —— 用于确认"必须有快捷方式注册"这一约束）
Console.OutputEncoding = System.Text.Encoding.UTF8;

var xml = new XmlDocument();
xml.LoadXml(
    "<toast><visual><binding template='ToastGeneric'>"
    + "<text>ToastProbe 测试通知</text>"
    + "<text>这是来自 Lertaro 剪贴板插件探针的系统通知</text>"
    + "</binding></visual></toast>");

var targets = new[]
{
    ("PowerShell 已注册 AUMID", "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe"),
    ("自定义未注册 AUMID", "Printz1.Lertaro.ClipboardHistory.ToastProbe")
};

foreach (var (label, aumid) in targets)
{
    try
    {
        var notifier = ToastNotificationManager.CreateToastNotifier(aumid);
        notifier.Show(new ToastNotification(xml));
        Console.WriteLine("[OK]   " + label + " -> 已提交通知请求");
    }
    catch (Exception ex)
    {
        Console.WriteLine("[FAIL] " + label + " -> " + ex.GetType().Name + ": " + ex.Message);
    }
}

Console.WriteLine("done");
