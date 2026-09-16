using System.Windows.Threading;

namespace Lertaro.Plugins.ClipboardHistory;

/// <summary>
/// 提醒调度：**精准单次定时器** —— 直接对准最近的到期时刻（提醒 / 置顶到期），
/// 平时不做任何周期扫描；到点后处理所有到期项，再重新对准下一次。
/// 启动时 Arm 一次即可完成"睡过头"补偿（过期时刻的延迟为 0，会立即触发）。
/// </summary>
internal sealed class ClipboardReminderService : IDisposable
{
    private readonly ClipboardStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private bool _disposed;

    internal ClipboardReminderService(ClipboardStore store, Dispatcher dispatcher)
    {
        _store = store;
        _dispatcher = dispatcher;
        _timer = new Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>重新对准最近一次到期时刻；没有待触发项则彻底停表。</summary>
    internal void Arm()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var next = _store.NextDeadline();
            if (next is null)
            {
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                return;
            }

            var delay = (next.Value - DateTime.Now).TotalMilliseconds;
            var due = (long)Math.Clamp(delay, 0, int.MaxValue);
            _timer.Change(due, Timeout.Infinite);
        }
    }

    private void OnTick(object? state)
    {
        var due = new List<ClipboardDueEvent>();

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _store.CollectDue(DateTime.Now, due);
        }

        if (due.Count > 0)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (var item in due)
                {
                    ClipboardToast.Show(item);
                }

                ClipboardPanel.NotifyStoreChanged(_store);
            }));
        }

        Arm();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            _timer.Dispose();
        }
    }
}
