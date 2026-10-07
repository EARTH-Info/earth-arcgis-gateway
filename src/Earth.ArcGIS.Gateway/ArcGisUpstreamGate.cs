namespace Earth.ArcGIS.Gateway;

public interface IArcGisUpstreamGate
{
    ValueTask<IDisposable?> TryEnterAsync(CancellationToken cancellationToken);
    int Limit { get; }
    int Available { get; }
}

public sealed class ArcGisUpstreamGate : IArcGisUpstreamGate, IDisposable
{
    private readonly SemaphoreSlim semaphore;

    public ArcGisUpstreamGate(int limit)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit));

        Limit = limit;
        semaphore = new SemaphoreSlim(limit, limit);
    }

    public int Limit { get; }
    public int Available => semaphore.CurrentCount;

    public async ValueTask<IDisposable?> TryEnterAsync(
        CancellationToken cancellationToken)
    {
        var entered = await semaphore.WaitAsync(0, cancellationToken);
        return entered ? new Lease(semaphore) : null;
    }

    public void Dispose() => semaphore.Dispose();

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
                semaphore.Release();
        }
    }
}
