using Earth.ArcGIS.Gateway;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class TelemetryQueueTests
{
    [Fact]
    public void WriteIsNonBlockingAndAcceptsEvent()
    {
        var queue = new TelemetryQueue();
        var item = Event("cid");

        Assert.True(queue.TryWrite(item));
        Assert.Contains(queue.GetRecent(10), x => x.CorrelationId == "cid");
    }

    [Fact]
    public void FullQueueRejectsCountsDropAndDoesNotExposeDroppedEventAsRecent()
    {
        var queue = new TelemetryQueue();

        for (var i = 0; i < 10_000; i++)
            Assert.True(queue.TryWrite(Event("accepted-" + i)));

        Assert.False(queue.TryWrite(Event("dropped")));
        Assert.Equal(10_000, queue.Accepted);
        Assert.Equal(1, queue.Dropped);
        Assert.DoesNotContain(
            queue.GetRecent(1000),
            x => x.CorrelationId == "dropped");
    }

    private static TelemetryEvent Event(string correlationId) =>
        new(
            DateTimeOffset.UtcNow,
            "sub",
            null,
            "jtuwma",
            "Land/Parcels",
            "FeatureServer",
            0,
            "query",
            "GET",
            200,
            10,
            "ALLOW",
            "policy_allow",
            "v1",
            correlationId);
}
