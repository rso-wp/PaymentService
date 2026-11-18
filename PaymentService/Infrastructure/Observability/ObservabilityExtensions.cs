using System.Text.Json;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NATS.Net;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace PaymentService.Infrastructure.Observability;

/// <summary>
/// Health checks (<c>/health/live</c>, <c>/health/ready</c>), OpenTelemetry metrics (<c>/metrics</c>, Prometheus format) and
/// central logging (Application Insights).
/// Kubernetes probes the health endpoints; Prometheus scrapes <c>/metrics</c> (pod annotations set by the helm chart).
/// </summary>
public static class ObservabilityExtensions
{
    private const string LiveTag = "live";
    public const string ReadyTag = "ready";

    /// <summary>
    /// live = the process answers (restart it if not). ready = it can do its job: Postgres must work; NATS down only
    /// reports Degraded (still HTTP 200), because the outbox keeps messages until it is back.
    /// </summary>
    public static IServiceCollection AddServiceHealthChecks<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext
    {
        services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [LiveTag])
            .AddDbContextCheck<TDbContext>("postgres", tags: [ReadyTag])
            .AddCheck<NatsHealthCheck>("nats", failureStatus: HealthStatus.Degraded, tags: [ReadyTag]);

        return services;
    }

    /// <summary>Setting (environment variable) with the Application Insights connection string. Without it nothing is exported.</summary>
    public const string ApplicationInsightsConnectionString = "APPLICATIONINSIGHTS_CONNECTION_STRING";

    /// <summary>
    /// Metrics for Prometheus (<c>/metrics</c>) and, when <see cref="ApplicationInsightsConnectionString"/> is set, every log
    /// written through <c>ILogger</c> to Application Insights (a Log Analytics workspace), where Grafana reads them. The
    /// console output stays as it is, so <c>kubectl logs</c> keeps working. Locally the setting is empty and nothing leaves
    /// the process.
    /// </summary>
    public static IServiceCollection AddObservability(
        this IServiceCollection services, string serviceName, IConfiguration? configuration = null)
    {
        var otel = services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("Npgsql", "Wolverine")
                .AddPrometheusExporter());

        var connectionString = configuration?[ApplicationInsightsConnectionString];
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            // The service name becomes the "role name" of every log line: filter on it in Grafana.
            otel.WithLogging(logging => logging.AddAzureMonitorLogExporter(o => o.ConnectionString = connectionString));
            // Without these the message arrives as its template ("User {UserId} signed up") and without the request scope.
            services.Configure<OpenTelemetryLoggerOptions>(o =>
            {
                o.IncludeFormattedMessage = true;
                o.IncludeScopes = true;
            });
        }

        return services;
    }

    public static WebApplication MapObservability(this WebApplication app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(LiveTag),
            ResponseWriter = WriteJson
        });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadyTag),
            ResponseWriter = WriteJson
        });
        app.MapPrometheusScrapingEndpoint();

        return app;
    }

    private static Task WriteJson(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(
                e => e.Key,
                e => new { status = e.Value.Status.ToString(), error = e.Value.Exception?.Message })
        }));
    }

    private sealed class NatsHealthCheck(NatsClient nats) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await nats.PingAsync(timeout.Token);
                return HealthCheckResult.Healthy();
            }
            catch (Exception ex)
            {
                return new HealthCheckResult(context.Registration.FailureStatus, "NATS is unreachable.", ex);
            }
        }
    }
}
