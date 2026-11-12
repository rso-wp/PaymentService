using Grpc.Core;

namespace Test;

/// <summary>Minimal <see cref="ServerCallContext"/> for calling gRPC service methods directly in tests.</summary>
public class TestServerCallContext(CancellationToken cancellationToken = default) : ServerCallContext
{
    protected override string MethodCore => "test";
    protected override string HostCore => "localhost";
    protected override string PeerCore => "test";
    protected override DateTime DeadlineCore => DateTime.MaxValue;
    protected override Metadata RequestHeadersCore { get; } = [];
    protected override CancellationToken CancellationTokenCore => cancellationToken;
    protected override Metadata ResponseTrailersCore { get; } = [];
    protected override Status StatusCore { get; set; }
    protected override WriteOptions? WriteOptionsCore { get; set; }
    protected override AuthContext AuthContextCore =>
        new(null, new Dictionary<string, List<AuthProperty>>());

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
        throw new NotSupportedException();

    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
}
