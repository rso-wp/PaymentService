using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace PaymentService.Infrastructure.ApiDocs;

/// <summary>OpenAPI document at <c>/openapi/v1.json</c> and Swagger UI at <c>/swagger</c>, with a Bearer token "Authorize" button.</summary>
public static class ApiDocsExtensions
{
    public static IServiceCollection AddApiDocs(this IServiceCollection services, string title)
    {
        services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info = new OpenApiInfo { Title = title, Version = "v1" };
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
            {
                ["Bearer"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "Access token from UserService (signUp / logIn)."
                }
            };
            document.Security =
            [
                new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] }
            ];
            return Task.CompletedTask;
        }));

        return services;
    }

    public static WebApplication MapApiDocs(this WebApplication app, string title)
    {
        app.MapOpenApi();
        app.UseSwaggerUI(options =>
        {
            options.DocumentTitle = title;
            options.SwaggerEndpoint("/openapi/v1.json", title);
        });

        return app;
    }
}
