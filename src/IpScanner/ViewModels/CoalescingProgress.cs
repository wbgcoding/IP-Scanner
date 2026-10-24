using System;
using System.Threading;
using System.Threading.Tasks;

namespace IpScanner.ViewModels;

/// <summary>An <see cref="IProgress{T}"/> for very chatty producers. Reports from any thread only store the
/// newest value; the handler runs on the captured context at most once per interval with that value. Unlike
/// <c>Progress&lt;T&gt;</c> it never queues one UI message per report, so tens of thousands of reports
/// cannot starve input and rendering.</summary>
public sealed class CoalescingProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;
    private readonly SynchronizationContext? _context;
    private readonly TimeSpan _interval;
    private readonly object _gate = new();
    private T _latest = default!;
    private bool _hasValue;
    private bool _scheduled;
    private bool _closed;

    public CoalescingProgress(Action<T> handler, TimeSpan interval)
    {
        _handler = handler;
        _interval = interval;
        _context = SynchronizationContext.Current;
    }

    public void Report(T value)
    {
        lock (_gate)
        {
            if (_closed) return;
            _latest = value;
            _hasValue = true;
            if (_scheduled) return;
            _scheduled = true;
        }
        _ = DeliverLaterAsync();
    }

    private async Task DeliverLaterAsync()
    {
        await Task.Delay(_interval).ConfigureAwait(false);
        if (_context is null) Deliver();
        else _context.Post(_ => Deliver(), null);
    }

    private void Deliver()
    {
        T value;
        lock (_gate)
        {
            _scheduled = false;
            if (_closed || !_hasValue) return;
            _hasValue = false;
            value = _latest;
        }
        _handler(value);
    }

    /// <summary>Stops further deliveries and hands the newest unseen value to the handler on the calling thread.</summary>
    public void Complete()
    {
        T value;
        lock (_gate)
        {
            _closed = true;
            if (!_hasValue) return;
            _hasValue = false;
            value = _latest;
        }
        _handler(value);
    }
}
