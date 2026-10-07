using Earth.ArcGIS.Gateway;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class ArcGisUpstreamGateTests
{
    [Fact]
    public async Task RejectsWhenConcurrencyLimitIsExhaustedAndRecoversAfterRelease()
    {
        using var gate = new ArcGisUpstreamGate(1);

        using var first = await gate.TryEnterAsync(CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal(0, gate.Available);

        var rejected = await gate.TryEnterAsync(CancellationToken.None);
        Assert.Null(rejected);

        first!.Dispose();
        Assert.Equal(1, gate.Available);

        using var recovered = await gate.TryEnterAsync(CancellationToken.None);
        Assert.NotNull(recovered);
        Assert.Equal(0, gate.Available);
    }
}
