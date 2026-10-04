using System.Threading.Channels;

namespace Earth.ArcGIS.Gateway;

public interface ITelemetryQueue
{
    bool TryWrite(TelemetryEvent item);
    IAsyncEnumerable<TelemetryEvent> ReadAllAsync(CancellationToken cancellationToken);
    long Accepted { get; }
    long Dropped { get; }
}

public sealed class TelemetryQueue : ITelemetryQueue
{
    private readonly Channel<TelemetryEvent> _channel;
    private long _accepted;
    private long _dropped;

    public TelemetryQueue()
    {
        _channel = Channel.CreateBounded<TelemetryEvent>(new BoundedChannelOptions(10_000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false
        });
    }

    public long Accepted => Interlocked.Read(ref _accepted);
    public long Dropped => Interlocked.Read(ref _dropped);

    public bool TryWrite(TelemetryEvent item)
    {
        if (_channel.Writer.TryWrite(item))
        {
            Interlocked.Increment(ref _accepted);
            return true;
        }
        Interlocked.Increment(ref _dropped);
        return false;
    }

    public IAsyncEnumerable<TelemetryEvent> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
