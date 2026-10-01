using System.Windows.Threading;
using Lertaro.PluginSdk;

namespace Lertaro.Plugins.ClipboardHistory;

/// <summary>
/// 插件自管的 WPF UI 线程（STA + Dispatcher 消息泵）。
///
/// 为什么需要它：宿主 5.8 起把插件加载进提权 hook 进程 —— 那里没有
/// Application.Current，WPF 的 DispatcherTimer / Window 找不到调度器，
/// 面板与提醒浮窗全部弹不出来（Win+V 静默退回宿主搜索窗）。
///
/// 线程规则：懒启动一条后台 STA 线程跑 Dispatcher.Run；所有 UI 元素
/// （面板窗口、浮窗、DispatcherTimer）都必须创建在该 Dispatcher 上。
/// 宿主 App 进程里 Application.Current 存在时，调用方应继续用宿主
/// Dispatcher，本类只作为 hook 进程的兜底。
/// </summary>
internal static class ClipboardUi
{
    private static readonly object Gate = new();
    private static Dispatcher? _dispatcher;
    private static bool _started;

    /// <summary>
    /// 确保 UI 线程已启动并返回其 Dispatcher；启动失败返回 null
    /// （调用方自行回落到旧行为，例如退回宿主搜索窗）。
    /// </summary>
    internal static Dispatcher? EnsureStarted()
    {
        lock (Gate)
        {
            if (_dispatcher is not null)
            {
                return _dispatcher;
            }

            if (_started)
            {
                return null; // 已尝试过且没成功，不再重试
            }

            _started = true;

            try
            {
                var thread = new Thread(() =>
                {
                    // CurrentDispatcher 为当前线程创建 Dispatcher；Run 进消息泵后不再返回
                    _dispatcher = Dispatcher.CurrentDispatcher;
                    _dispatcher.UnhandledException += (_, e) =>
                    {
                        e.Handled = true;
                        ClipboardListener.Log("ui dispatcher exception: " + e.Exception.Message, LogLevel.Warn);
                    };
                    Dispatcher.Run();
                })
                {
                    IsBackground = true,
                    Name = "LertaroClipboardUi"
                };

                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();

                // 等 Dispatcher 挂上来（线程启动是毫秒级，留 2 秒余量）
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                while (_dispatcher is null && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(10);
                }
            }
            catch (Exception ex)
            {
                ClipboardListener.Log("ui thread start failed: " + ex.Message, LogLevel.Warn);
            }

            return _dispatcher;
        }
    }
}
