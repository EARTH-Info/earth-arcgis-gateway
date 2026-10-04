using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace Earth.ArcGIS.Gateway;

public sealed record RateCost(int Units, string Reason);

public interface IRateCostPolicy
{
    RateCost GetCost(
        ArcGisResource resource,
        IQueryCollection query,
        byte[]? postBody = null,
        string? contentType = null);
}

public sealed class RateCostPolicy : IRateCostPolicy
{
    public RateCost GetCost(
        ArcGisResource resource,
        IQueryCollection query,
        byte[]? postBody = null,
        string? contentType = null)
    {
        var form = ParseForm(postBody, contentType);

        if (resource.Operation.Equals("metadata", StringComparison.OrdinalIgnoreCase))
            return new(1, "metadata");

        if (resource.Operation.Equals("tile", StringComparison.OrdinalIgnoreCase))
            return new(1, "tile");

        if (resource.Operation.Equals("query", StringComparison.OrdinalIgnoreCase))
        {
            if (IsTrue(query, form, "returnCountOnly"))
                return new(1, "count");

            var geometry =
                HasKey(query, form, "geometry") ||
                IsTrue(query, form, "returnGeometry");

            var allFields =
                Values(query, form, "outFields")
                    .Any(value => value == "*");

            var paged =
                HasKey(query, form, "resultOffset") ||
                HasKey(query, form, "resultRecordCount");

            var units = geometry && allFields
                ? 5
                : geometry
                    ? 4
                    : allFields
                        ? 3
                        : 2;

            if (paged)
                units = Math.Min(6, units + 1);

            return new(
                units,
                paged
                    ? "query_paged"
                    : geometry && allFields
                        ? "query_geometry_all_fields"
                        : geometry
                            ? "query_geometry"
                            : allFields
                                ? "query_all_fields"
                                : "query");
        }

        if (resource.Operation.Equals("export", StringComparison.OrdinalIgnoreCase) ||
            resource.Operation.Equals("exportImage", StringComparison.OrdinalIgnoreCase))
            return new(5, resource.Operation.ToLowerInvariant());

        if (resource.Operation.Equals("identify", StringComparison.OrdinalIgnoreCase) ||
            resource.Operation.Equals("find", StringComparison.OrdinalIgnoreCase))
            return new(3, resource.Operation.ToLowerInvariant());

        if (resource.Operation.Equals("attachments", StringComparison.OrdinalIgnoreCase))
            return new(3, "attachments");

        return new(2, resource.Operation.ToLowerInvariant());
    }

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
            return null;

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
        Values(query, form, key)
            .Any(value =>
                string.Equals(
                    value,
                    "true",
                    StringComparison.OrdinalIgnoreCase));

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

        if (form is not null &&
            form.TryGetValue(key, out var formValues))
        {
            foreach (var value in formValues)
                yield return value;
        }
    }
}
