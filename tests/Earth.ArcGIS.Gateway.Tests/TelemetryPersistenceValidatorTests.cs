using Earth.ArcGIS.Gateway;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class TelemetryPersistenceValidatorTests
{
    [Fact]
    public void ProductionRejectsPlainHttpClickHouse()
    {
        var options = Valid();
        options.ClickHouseBaseUrl = "http://clickhouse.internal:8123";

        Assert.Throws<InvalidOperationException>(() =>
            TelemetryPersistenceValidator.Validate(options, production: true));
    }

    [Fact]
    public void DevelopmentAllowsLocalHttpClickHouse()
    {
        var options = Valid();
        options.ClickHouseBaseUrl = "http://localhost:8123";

        TelemetryPersistenceValidator.Validate(options, production: false);
    }

    [Fact]
    public void ProductionAcceptsHttpsClickHouse()
    {
        TelemetryPersistenceValidator.Validate(Valid(), production: true);
    }

    [Theory]
    [InlineData(0, 1024L, 1)]
    [InlineData(1, 0L, 1)]
    [InlineData(1, 1024L, 0)]
    public void InvalidSpoolBoundsAreRejected(
        int files,
        long bytes,
        int replay)
    {
        var options = Valid();
        options.MaxSpoolFiles = files;
        options.MaxSpoolBytes = bytes;
        options.ReplayBatchFiles = replay;

        Assert.Throws<InvalidOperationException>(() =>
            TelemetryPersistenceValidator.Validate(options, production: true));
    }

    private static TelemetryPersistenceOptions Valid() =>
        new()
        {
            ClickHouseBaseUrl = "https://clickhouse.internal:8443",
            Database = "default",
            Table = "gateway_telemetry",
            AdminAuditTable = "gateway_admin_audit",
            SpoolDirectory = "data/telemetry-spool",
            MaxSpoolFiles = 1000,
            MaxSpoolBytes = 1024 * 1024,
            ReplayBatchFiles = 10
        };
}
