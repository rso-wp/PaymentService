using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace PaymentService.Infrastructure.Auth;

/// <summary>
/// Local development only (DevAuth:Enabled in Development): authenticates a request from the X-Dev-User-Id (Guid) and
/// optional X-Dev-User-Email headers instead of an Entra token. Same user id across all services = same user.
/// </summary>
public class DevAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Dev";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var userId = Request.Headers["X-Dev-User-Id"].ToString();
        if (!Guid.TryParse(userId, out _)) return Task.FromResult(AuthenticateResult.NoResult());

        var email = Request.Headers["X-Dev-User-Email"].ToString();
        var identity = new ClaimsIdentity(
            [
                new Claim("oid", userId),
                new Claim("email", string.IsNullOrWhiteSpace(email) ? $"{userId}@dev.local" : email)
            ],
            SchemeName);

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
