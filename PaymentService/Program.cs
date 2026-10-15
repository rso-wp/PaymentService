using Microsoft.EntityFrameworkCore;
using PaymentService.Infrastructure.Persistance;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPaymentPersistence(builder.Configuration);
builder.Services.AddPaymentEventStore(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
    await db.Database.MigrateAsync();
}

await app.RunAsync();
