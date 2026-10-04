namespace Earth.ArcGIS.Gateway;

public interface IArcGisOperationPolicy
{
    bool IsAllowed(ArcGisResource resource, string method);
}

public sealed class ArcGisOperationPolicy : IArcGisOperationPolicy
{
    private static readonly HashSet<string> QueryOperations =
        new(
        [
            "query",
            "queryRelatedRecords",
            "queryAttachments",
            "legend",
            "find",
            "identify",
            "export",
            "exportImage"
        ],
        StringComparer.OrdinalIgnoreCase);

    public bool IsAllowed(ArcGisResource resource, string method)
    {
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsPost(method))
            return false;

        var tail = resource.OperationTail ?? Array.Empty<string>();

        if (resource.Operation.Equals("metadata", StringComparison.OrdinalIgnoreCase))
            return tail.Count == 0;

        if (QueryOperations.Contains(resource.Operation))
            return tail.Count == 0;

        if (resource.Operation.Equals("attachments", StringComparison.OrdinalIgnoreCase))
        {
            if (!HttpMethods.IsGet(method))
                return false;

            return tail.Count == 0 ||
                   (tail.Count == 1 && int.TryParse(tail[0], out var attachmentId) && attachmentId >= 0);
        }

        if (resource.Operation.Equals("tile", StringComparison.OrdinalIgnoreCase))
        {
            if (!HttpMethods.IsGet(method) || tail.Count != 3)
                return false;

            return IsTileCoordinate(tail[0]) &&
                   IsTileCoordinate(tail[1]) &&
                   IsTileCoordinateOrPbf(tail[2]);
        }

        if (resource.Operation.Equals("resources", StringComparison.OrdinalIgnoreCase))
        {
            return HttpMethods.IsGet(method) &&
                   resource.ServiceType.Equals("VectorTileServer", StringComparison.OrdinalIgnoreCase) &&
                   tail.Count > 0 &&
                   tail.All(IsSafeStaticResourceSegment);
        }

        if (resource.Operation.Equals("nodes", StringComparison.OrdinalIgnoreCase) ||
            resource.Operation.Equals("nodepages", StringComparison.OrdinalIgnoreCase))
        {
            return HttpMethods.IsGet(method) &&
                   resource.ServiceType.Equals("SceneServer", StringComparison.OrdinalIgnoreCase) &&
                   resource.LayerId is not null &&
                   tail.Count > 0 &&
                   tail.All(IsSafeStaticResourceSegment);
        }

        return false;
    }

    private static bool IsTileCoordinate(string value) =>
        int.TryParse(value, out var coordinate) && coordinate >= 0;

    private static bool IsTileCoordinateOrPbf(string value)
    {
        if (IsTileCoordinate(value))
            return true;

        return value.EndsWith(".pbf", StringComparison.OrdinalIgnoreCase) &&
               IsTileCoordinate(value[..^4]);
    }

    private static bool IsSafeStaticResourceSegment(string value) =>
        value.Length is > 0 and <= 255 &&
        value.All(ch =>
            char.IsLetterOrDigit(ch) ||
            ch is '-' or '_' or '.' or '@');
}
