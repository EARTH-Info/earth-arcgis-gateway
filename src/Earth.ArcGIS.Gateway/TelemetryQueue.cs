using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Earth.ArcGIS.Gateway;

public interface ITelemetryQueue
{
    bool TryWrite(TelemetryEvent item);
    IAsyncEnumerable<TelemetryEvent> ReadAllAsync(CancellationToken cancellationToken);
    ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken);
    bool TryRead(out TelemetryEvent? item);
    IReadOnlyList<TelemetryEvent> GetRecent(int max);
    long Accepted { get; }
    long Dropped { get; }
}

public sealed class TelemetryQueue : ITelemetryQueue
{
    private const int RecentCapacity = 1_000;
    private readonly Channel<TelemetryEvent> _channel;
    private readonly ConcurrentQueue<TelemetryEvent> _recent = new();
    private long _accepted;
    private long _dropped;

    public TelemetryQueue()
    {
        _channel = Channel.CreateBounded<TelemetryEvent>(new BoundedChannelOptions(10_000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    public long Accepted => Interlocked.Read(ref _accepted);
    public long Dropped => Interlocked.Read(ref _dropped);

    public bool TryWrite(TelemetryEvent item)
    {
        if (!_channel.Writer.TryWrite(item))
        {
            Interlocked.Increment(ref _dropped);
            return false;
        }

        Interlocked.Increment(ref _accepted);
        _recent.Enqueue(item);
        while (_recent.Count > RecentCapacity)
            _recent.TryDequeue(out _);
        return true;
    }

    public IAsyncEnumerable<TelemetryEvent> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken) =>
        _channel.Reader.WaitToReadAsync(cancellationToken);

    public bool TryRead(out TelemetryEvent? item)
    {
        if (_channel.Reader.TryRead(out var value))
        {
            item = value;
            return true;
        }

        item = null;
        return false;
    }

    public IReadOnlyList<TelemetryEvent> GetRecent(int max) =>
        _recent
            .Reverse()
            .Take(Math.Clamp(max, 1, RecentCapacity))
            .ToArray();
}
