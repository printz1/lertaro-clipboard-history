using System.IO;
using System.Runtime.InteropServices;
using Lertaro.PluginSdk;

namespace Lertaro.Plugins.ClipboardHistory;

/// <summary>
/// 提示音。MessageBeep 依赖系统的"默认提示音/星号"声音方案分配，
/// 部分机器该事件被设为"无"就完全不响（用户实测"没有声音"）。
/// 因此改为：优先播放系统自带的通知音 wav（异步、不阻塞），
/// 找不到文件再回退 MessageBeep。
/// </summary>
internal static class ClipboardSounds
{
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndFilename = 0x00020000;

    private static readonly string[] CandidateFiles =
    [
        "Windows Notify System Generic.wav",
        "Windows Notify.wav",
        "Windows Notify Messaging.wav",
        "notify.wav"
    ];

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);

    internal static void PlayReminder()
    {
        try
        {
            var media = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Media");

            foreach (var name in CandidateFiles)
            {
                var file = Path.Combine(media, name);
                if (File.Exists(file)
                    && PlaySound(file, IntPtr.Zero, SndAsync | SndNoDefault | SndFilename))
                {
                    return;
                }
            }

            // 系统音文件都不可用：回退到声音方案里的"星号"事件
            NativeMethods.MessageBeep(NativeMethods.MbIconAsterisk);
        }
        catch (Exception ex)
        {
            ClipboardListener.Log("reminder sound failed: " + ex.Message, LogLevel.Debug);
        }
    }
}
