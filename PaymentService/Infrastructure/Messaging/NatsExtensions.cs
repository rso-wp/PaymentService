using System.Text.Json;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;

namespace PaymentService.Infrastructure.Messaging;

public static class NatsExtensions
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IServiceCollection AddNats(this IServiceCollection services, IConfiguration configuration)
    {
        var url = configuration["Nats:Url"]
            ?? throw new InvalidOperationException("'Nats:Url' is not configured.");

        services.AddSingleton(new NatsClient(url));
        services.AddSingleton(sp => sp.GetRequiredService<NatsClient>().CreateJetStreamContext());

        return services;
    }

    /// <summary>Creates the stream if missing. UserService declares the same stream.</summary>
    public static Task EnsureUsersStreamAsync(this INatsJSContext js, CancellationToken ct) =>
        js.EnsureStreamAsync(UserSubjects.Stream, UserSubjects.All, ct);

    /// <summary>Creates the stream if missing. ProvisioningService declares the same stream.</summary>
    public static Task EnsurePaymentsStreamAsync(this INatsJSContext js, CancellationToken ct) =>
        js.EnsureStreamAsync(PaymentSubjects.Stream, PaymentSubjects.All, ct);

    public static async Task PublishJsonAsync<T>(this INatsJSContext js, string subject, T message, CancellationToken ct)
    {
        var ack = await js.PublishAsync(subject, JsonSerializer.SerializeToUtf8Bytes(message, Json), cancellationToken: ct);
        ack.EnsureSuccess();
    }

    private static async Task EnsureStreamAsync(this INatsJSContext js, string stream, string subjects, CancellationToken ct)
    {
        try
        {
            await js.GetStreamAsync(stream, cancellationToken: ct);
        }
        catch (NatsJSApiException ex) when (ex.Error.Code == 404)
        {
            await js.CreateStreamAsync(
                new StreamConfig(stream, [subjects])
                {
                    Storage = StreamConfigStorage.File,
                    MaxAge = TimeSpan.FromDays(7)
                },
                ct);
        }
    }
}
