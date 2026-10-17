using Microsoft.EntityFrameworkCore;
using PaymentService.Features.AddPaymentMethod;
using PaymentService.Features.StartSubscription;
using PaymentService.Infrastructure.Auth;
using PaymentService.Infrastructure.Persistance;
using PaymentService.Infrastructure.StripeIntegration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPaymentPersistence(builder.Configuration);
builder.Services.AddPaymentEventStore(builder.Configuration);
builder.Services.AddStripeIntegration(builder.Configuration);
builder.Services.AddEntraAuth(builder.Configuration);
builder.Services.AddFrontendCors(builder.Configuration);
builder.Services.AddGrpc();
builder.Services.AddScoped<StripeCustomerResolver>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
    await db.Database.MigrateAsync();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapGrpcService<PaymentGrpcService>();
app.MapAddPaymentMethod();

await app.RunAsync();
