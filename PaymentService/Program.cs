using Microsoft.EntityFrameworkCore;
using PaymentService.Features.StartSubscription;
using PaymentService.Infrastructure.Persistance;
using PaymentService.Infrastructure.StripeIntegration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPaymentPersistence(builder.Configuration);
builder.Services.AddPaymentEventStore(builder.Configuration);
builder.Services.AddStripeIntegration(builder.Configuration);
builder.Services.AddGrpc();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
    await db.Database.MigrateAsync();
}

app.MapGrpcService<PaymentGrpcService>();

await app.RunAsync();
