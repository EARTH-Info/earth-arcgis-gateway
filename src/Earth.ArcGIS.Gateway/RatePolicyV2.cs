using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace Earth.ArcGIS.Gateway;

public interface IRequestActivityClassifier
{
    RequestActivity Classify(
        ArcGisResource resource,
        IQueryCollection query,
        byte[]? postBody = null,
        string? contentType = null);
}

public sealed class RequestActivityClassifier(GisCostOptions options)
    : IRequestActivityClassifier
{
    public RequestActivity Classify(
        ArcGisResource resource,
        IQueryCollection query,
        byte[]? postBody = null,
        string? contentType = null)
    {
        var form = ParseForm(postBody, contentType);
        var operation = resource.Operation;

        if (operation.Equals("metadata", StringComparison.OrdinalIgnoreCase))
            return Activity("metadata", options.Metadata, false, false, false, false, query, form);

        if (operation.Equals("tile", StringComparison.OrdinalIgnoreCase))
            return Activity("tile", options.Tile, false, false, false, false, query, form);

        if (operation.Equals("nodes", StringComparison.OrdinalIgnoreCase))
            return Activity("scene_node", options.SceneNode, false, false, false, false, query, form);

        if (operation.Equals("query", StringComparison.OrdinalIgnoreCase))
            return ClassifyQuery(query, form);

        if (operation.Equals("identify", StringComparison.OrdinalIgnoreCase))
            return Activity("identify", options.Identify, HasKey(query, form, "geometry"), false, false, false, query, form);

        if (operation.Equals("find", StringComparison.OrdinalIgnoreCase))
            return Activity("find", options.Find, false, false, false, false, query, form);

        if (operation.Equals("attachments", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals("queryAttachments", StringComparison.OrdinalIgnoreCase))
        {
            return Activity("attachments", options.Attachment, false, false, false, false, query, form);
        }

        if (operation.Equals("export", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals("exportImage", StringComparison.OrdinalIgnoreCase))
        {
            return Activity(operation.ToLowerInvariant(), options.Export, true, true, false, true, query, form);
        }

        return Activity(operation.ToLowerInvariant(), options.Fallback, false, false, false, false, query, form);
    }

    private RequestActivity ClassifyQuery(
        IQueryCollection query,
        IReadOnlyDictionary<string, StringValues>? form)
    {
        if (IsTrue(query, form, "returnCountOnly"))
            return Activity("query_count", options.CountOnly, false, false, false, false, query, form);

        if (IsTrue(query, form, "returnIdsOnly"))
            return Activity("query_ids", options.IdsOnly, false, false, false, false, query, form);

        if (IsTrue(query, form, "returnExtentOnly"))
            return Activity("query_extent", options.ExtentOnly, false, false, false, false, query, form);

        var spatial = HasKey(query, form, "geometry");
        var explicitNoGeometry = IsFalse(query, form, "returnGeometry");
        var returnsGeometry = !explicitNoGeometry;
        var allFields = Values(query, form, "outFields")
            .Any(value => string.Equals(value?.Trim(), "*", StringComparison.Ordinal));
        var paged = HasKey(query, form, "resultOffset") ||
                    HasKey(query, form, "resultRecordCount");

        var units = returnsGeometry
            ? options.GeometryQuery
            : options.AttributeQuery;

        if (spatial)
            units += options.SpatialSurcharge;
        if (allFields)
            units += options.AllFieldsSurcharge;
        if (paged)
            units += options.PaginationSurcharge;

        var extractionLike = paged &&
            (HasKey(query, form, "resultOffset") || allFields);
        var heavy = extractionLike || units >= options.HeavyThreshold;

        var activityClass = spatial
            ? "query_spatial"
            : returnsGeometry
                ? "query_geometry"
                : "query_attribute";

        if (allFields)
            activityClass += "_all_fields";
        if (paged)
            activityClass += "_paged";

        return new RequestActivity(
            activityClass,
            units,
            spatial,
            returnsGeometry,
            paged,
            heavy,
            extractionLike,
            BuildFingerprint(query, form));
    }

    private static RequestActivity Activity(
        string activityClass,
        int cost,
        bool spatial,
        bool returnsGeometry,
        bool paged,
        bool heavy,
        IQueryCollection query,
        IReadOnlyDictionary<string, StringValues>? form) =>
        new(
            activityClass,
            cost,
            spatial,
            returnsGeometry,
            paged,
            heavy,
            false,
            BuildFingerprint(query, form));

    private static IReadOnlyDictionary<string, StringValues>? ParseForm(
        byte[]? postBody,
        string? contentType)
    {
        if (postBody is null ||
            postBody.Length == 0 ||
            string.IsNullOrWhiteSpace(contentType) ||
            !contentType.StartsWith(
                "application/x-www-form-urlencoded",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var value = Encoding.UTF8.GetString(postBody);
        return QueryHelpers.ParseQuery(
            value.StartsWith("?", StringComparison.Ordinal)
                ? value
                : "?" + value);
    }

    private static bool HasKey(
        IQueryCollection query,
        IReadOnlyDictionary<string, StringValues>? form,
        string key) =>
        query.ContainsKey(key) ||
        (form?.ContainsKey(key) ?? false);

    private static bool IsTrue(
        IQueryCollection query,
        IReadOnlyDictionary<string, StringValues>? form,
        string key) =>
        Values(query, form, key).Any(value =>
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));

    private static bool IsFalse(
        IQueryCollection query,
        IReadOnlyDictionary<string, StringValues>? form,
        string key) =>
        Values(query, form, key).Any(value =>
            string.Equals(value, "false", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string?> Values(
        IQueryCollection query,
        IReadOnlyDictionary<string, StringValues>? form,
        string key)
    {
        if (query.TryGetValue(key, out var queryValues))
        {
            foreach (var value in queryValues)
                yield return value;
        }

        if (form is not null && form.TryGetValue(key, out var formValues))
        {
            foreach (var value in formValues)
                yield return value;
        }
    }

    private static string? BuildFingerprint(
        IQueryCollection query,
        IReadOnlyDictionary<string, StringValues>? form)
    {
        var keys = new[]
        {
            "where", "outFields", "geometry", "spatialRel", "returnGeometry",
            "returnCountOnly", "returnIdsOnly", "returnExtentOnly", "orderByFields",
            "groupByFieldsForStatistics", "outStatistics", "resultOffset",
            "resultRecordCount", "time"
        };

        var parts = new List<string>();
        foreach (var key in keys)
        {
            var values = Values(query, form, key).Where(v => v is not null).ToArray();
            if (values.Length == 0)
                continue;

            var normalizedValue = key is "resultOffset" or "resultRecordCount"
                ? "<present>"
                : string.Join(',', values.Select(v => v!.Trim()));
            parts.Add($"{key.ToLowerInvariant()}={normalizedValue}");
        }

        if (parts.Count == 0)
            return null;

        parts.Sort(StringComparer.Ordinal);
        var raw = string.Join('&', parts);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash.AsSpan(0, 16));
    }
}
