using PaymentService.Infrastructure.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Test;

/// <summary>Logs reach OpenTelemetry (and so Application Insights) only when the connection string is configured.</summary>
public class LoggingExportTests
{
    // Never contacted in these tests: the exporter just fails quietly to send.
    private const string FakeConnectionString =
        "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=http://127.0.0.1:1/";

    private static List<LogRecord> LogOnce(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var exported = new List<LogRecord>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddObservability("test-service", configuration);
        // A second, in-memory exporter next to the real one, to see what is handed to exporters.
        services.ConfigureOpenTelemetryLoggerProvider(logging => logging.AddInMemoryExporter(exported));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ILogger<LoggingExportTests>>().LogInformation("User {UserId} signed up", 42);
        provider.GetService<LoggerProvider>()?.ForceFlush(5000);
        return exported;
    }

    [Fact]
    public void WithAConnectionString_ILoggerOutputIsExported_WithItsFormattedMessage()
    {
        var exported = LogOnce(new() { [ObservabilityExtensions.ApplicationInsightsConnectionString] = FakeConnectionString });

        var record = Assert.Single(exported);
        Assert.Equal("User 42 signed up", record.FormattedMessage);
        Assert.Equal(LogLevel.Information, record.LogLevel);
    }

    [Fact]
    public void WithoutAConnectionString_NothingIsExported() =>
        Assert.Empty(LogOnce([]));

    [Fact]
    public void WithABlankConnectionString_NothingIsExported() =>
        Assert.Empty(LogOnce(new() { [ObservabilityExtensions.ApplicationInsightsConnectionString] = "  " }));
}
