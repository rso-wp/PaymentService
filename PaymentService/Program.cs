using Microsoft.EntityFrameworkCore;
using PaymentService.Features.AddPaymentMethod;
using PaymentService.Features.Customers;
using PaymentService.Features.PaymentMethods;
using PaymentService.Features.StartSubscription;
using PaymentService.Features.StripeWebhooks;
using PaymentService.Infrastructure.Auth;
using PaymentService.Infrastructure.Messaging;
using PaymentService.Infrastructure.Persistance;
using PaymentService.Infrastructure.StripeIntegration;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPaymentPersistence(builder.Configuration);
builder.Services.AddPaymentEventStore(builder.Configuration);
builder.Services.AddStripeIntegration(builder.Configuration, builder.Environment);
builder.Services.AddJwtAuth(builder.Configuration);
builder.Services.AddFrontendCors(builder.Configuration);
builder.Services.AddGrpc();
builder.Services.AddScoped<StripeCustomerResolver>();
builder.Services.AddNats(builder.Configuration);
builder.Services.AddHostedService<UserCreatedConsumer>();

builder.Host.UseWolverine(opts => opts.AddPaymentMessaging());

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
app.MapPaymentMethods();
app.MapStripeWebhooks();

await app.RunAsync();
