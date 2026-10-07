using Earth.ArcGIS.Gateway;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class AccessPolicyContractTests
{
    [Fact]
    public void RequestCarriesTrustedAuthorizationDimensions()
    {
        var request = new AccessPolicyRequest(
            "earthid-sub-1", "tenant-1", "jtuwma", "Land/Parcels",
            "FeatureServer", 0, "query", "POST", "cid-1");

        Assert.Equal("earthid-sub-1", request.EarthIdSub);
        Assert.Equal("jtuwma", request.Application);
        Assert.Equal("Land/Parcels", request.Service);
        Assert.Equal(0, request.LayerId);
        Assert.Equal("query", request.Operation);
    }

    [Fact]
    public void DeniedDecisionCannotBeMistakenForAllow()
    {
        var decision = new AccessPolicyDecision(false, "policy_denied", "v1");
        Assert.False(decision.Allowed);
    }
}
