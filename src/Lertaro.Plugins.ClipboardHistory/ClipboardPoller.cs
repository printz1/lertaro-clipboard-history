using Lertaro.PluginSdk;

namespace Lertaro.Plugins.ClipboardHistory;

/// <summary>
/// 兜底方案：当消息窗口或 AddClipboardFormatListener 不可用时才启用。
/// 按官方文档的定位使用 GetClipboardSequenceNumber —— 它只用来判断"缓存是否还有效"，
/// 不做主力通知机制。间隔刻意放长，因为这条路径正常情况下根本不该跑起来。
/// </summary>
internal sealed class ClipboardPoller : IDisposable
{
    private const int PollIntervalMs = 1000;

    private readonly ClipboardStore _store;
    private readonly Func<bool>? _isPaused;
    private readonly Timer _timer;
    private uint _lastSequence;
    private bool _disposed;
    private int _ticking;

    internal ClipboardPoller(ClipboardStore store, Func<bool>? isPaused = null)
    {
        _store = store;
        _isPaused = isPaused;
        _lastSequence = NativeMethods.GetClipboardSequenceNumber();
        _timer = new Timer(OnTick, null, PollIntervalMs, PollIntervalMs);
    }

    private void OnTick(object? state)
    {
        if (_disposed || Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            if (_isPaused?.Invoke() == true)
            {
                return;
            }

            var sequence = NativeMethods.GetClipboardSequenceNumber();
            if (sequence == _lastSequence)
            {
                return;
            }

            _lastSequence = sequence;

            var result = ClipboardReader.Read();
            if (result.IsExcluded || string.IsNullOrEmpty(result.Text))
            {
                return;
            }

            if (_store.Add(result.Text, null))
            {
                ClipboardListener.Log("captured (poll fallback) " + result.Text.Length + " chars", LogLevel.Debug);
            }
        }
        catch (Exception ex)
        {
            ClipboardListener.Log("poll tick failed: " + ex.Message, LogLevel.Warn);
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
    }
}
