using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace PaymentService.Infrastructure.Auth;

public static class AuthExtensions
{
    public const string FrontendCorsPolicy = "Frontend";

    /// <summary>
    /// Bearer validation of UserService's tokens. The signing key is either configured (`Auth:SigningPublicKey`, PEM), so
    /// validating a token never depends on UserService being up, or read from UserService's discovery document
    /// (`Auth:MetadataAddress`, cluster-internal http) and refreshed when it changes.
    /// </summary>
    public static IServiceCollection AddJwtAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Auth");
        string Required(string key) => section[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"'Auth:{key}' is not configured.");

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false; // keep raw claim names (sub, email, ...)
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = Required("Issuer"),
                    ValidAudience = Required("Audience"),
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256]
                };

                if (section["SigningPublicKey"] is { Length: > 0 } publicKey)
                {
                    options.TokenValidationParameters.IssuerSigningKey = PublicKey(publicKey);
                }
                else
                {
                    options.MetadataAddress = Required("MetadataAddress");
                    options.RequireHttpsMetadata = false; // UserService is reached inside the cluster / on localhost
                }
            });
        services.AddAuthorization();

        return services;
    }

    /// <summary>The RSA public key (PEM), with the key id UserService puts in each token's `kid` (see its SigningKey).</summary>
    private static RsaSecurityKey PublicKey(string pem)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        var keyId = Base64UrlEncoder.Encode(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()))[..16];
        return new RsaSecurityKey(rsa) { KeyId = keyId };
    }

    public static IServiceCollection AddFrontendCors(this IServiceCollection services, IConfiguration configuration)
    {
        var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

        services.AddCors(options => options.AddPolicy(FrontendCorsPolicy, policy =>
            policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));

        return services;
    }

    public static Guid GetUserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("sub"), out var id)
            ? id
            : throw new InvalidOperationException("Token has no valid 'sub' claim.");

    public static string? GetEmail(this ClaimsPrincipal principal) => principal.FindFirstValue("email");
}
