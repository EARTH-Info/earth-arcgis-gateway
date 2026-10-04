namespace Earth.ArcGIS.Gateway;

public interface IArcGisOperationPolicy
{
    bool IsAllowed(ArcGisResource resource, string method);
}

public sealed class ArcGisOperationPolicy : IArcGisOperationPolicy
{
    private static readonly HashSet<string> AllowedOperations =
        new(["metadata", "query", "queryRelatedRecords", "attachments", "legend", "find", "identify"],
            StringComparer.OrdinalIgnoreCase);

    public bool IsAllowed(ArcGisResource resource, string method)
    {
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsPost(method)) return false;
        return AllowedOperations.Contains(resource.Operation);
    }
}
