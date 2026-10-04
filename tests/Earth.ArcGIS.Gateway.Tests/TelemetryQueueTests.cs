using Earth.ArcGIS.Gateway;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class TelemetryQueueTests
{
    [Fact]
    public void WriteIsNonBlockingAndAcceptsEvent()
    {
        var queue = new TelemetryQueue();
        var item = new TelemetryEvent(DateTimeOffset.UtcNow, "sub", null, "jtuwma",
            "Land/Parcels", "FeatureServer", 0, "query", "GET", 200, 10,
            "ALLOW", "policy_allow", "v1", "cid");

        Assert.True(queue.TryWrite(item));
    }
}
