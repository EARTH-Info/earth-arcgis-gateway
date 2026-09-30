using Earth.ArcGIS.Gateway;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class GatewayOptionsTests
{
    [Fact]
    public void Defaults_AreFailSafeForResourceAllowList()
    {
        var options = new GatewayOptions();
        Assert.Empty(options.AllowedPathPrefixes);
    }
}
