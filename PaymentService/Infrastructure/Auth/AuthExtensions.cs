using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace PaymentService.Infrastructure.Auth;

public static class AuthExtensions
{
    public const string FrontendCorsPolicy = "Frontend";

    /// <summary>
    /// Bearer validation of the access tokens Keycloak issues (it owns sign-up, log-in and passwords). Settings, section "Auth":
    /// <c>Issuer</c> is the realm URL as the browser sees it (the token's <c>iss</c>), <c>Audience</c> the API's audience
    /// (<c>rso-api</c>, put in the token by a Keycloak mapper), and <c>MetadataAddress</c> Keycloak's discovery document, from
    /// which the signing keys are read and refreshed (cluster-internal http, so it can differ from <c>Issuer</c>).
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

                options.MetadataAddress = Required("MetadataAddress");
                options.RequireHttpsMetadata = false; // Keycloak is reached inside the cluster / on localhost
            });
        services.AddAuthorization();

        return services;
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
