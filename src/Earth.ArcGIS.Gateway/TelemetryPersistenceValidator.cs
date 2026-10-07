using System.Text.RegularExpressions;

namespace Earth.ArcGIS.Gateway;

public static class TelemetryPersistenceValidator
{
    private static readonly Regex IdentifierPattern =
        new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    public static void Validate(
        TelemetryPersistenceOptions options,
        bool production)
    {
        if (options.MaxSpoolFiles <= 0 ||
            options.MaxSpoolBytes <= 0 ||
            options.ReplayBatchFiles <= 0)
        {
            throw new InvalidOperationException(
                "Telemetry spool limits must be positive.");
        }

        if (!IdentifierPattern.IsMatch(options.Database) ||
            !IdentifierPattern.IsMatch(options.Table) ||
            !IdentifierPattern.IsMatch(options.AdminAuditTable))
        {
            throw new InvalidOperationException(
                "Telemetry database and table names must be simple identifiers.");
        }

        if (string.IsNullOrWhiteSpace(options.ClickHouseBaseUrl))
        {
            if (production)
            {
                throw new InvalidOperationException(
                    "Telemetry:ClickHouseBaseUrl is required in Production.");
            }

            return;
        }

        var validUri = Uri.TryCreate(
            options.ClickHouseBaseUrl,
            UriKind.Absolute,
            out var uri);
        var validScheme = validUri && uri is not null &&
            (production
                ? string.Equals(
                    uri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase)
                : string.Equals(
                      uri.Scheme,
                      Uri.UriSchemeHttp,
                      StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(
                      uri.Scheme,
                      Uri.UriSchemeHttps,
                      StringComparison.OrdinalIgnoreCase));

        if (!validUri ||
            uri is null ||
            !validScheme ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                production
                    ? "Production ClickHouse endpoint must be an absolute HTTPS URL without user-info, query, or fragment."
                    : "ClickHouse endpoint must be an absolute HTTP/HTTPS URL without user-info, query, or fragment.");
        }
    }
}
