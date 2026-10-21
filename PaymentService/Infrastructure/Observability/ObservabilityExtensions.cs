using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NATS.Net;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace PaymentService.Infrastructure.Observability;

/// <summary>
/// Health checks (<c>/health/live</c>, <c>/health/ready</c>) and OpenTelemetry metrics (<c>/metrics</c>, Prometheus format).
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

    public static IServiceCollection AddObservability(this IServiceCollection services, string serviceName)
    {
        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("Npgsql", "Wolverine")
                .AddPrometheusExporter());

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
