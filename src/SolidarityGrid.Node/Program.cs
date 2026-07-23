using Microsoft.EntityFrameworkCore;
using SolidarityGrid.Node;

var builder = WebApplication.CreateBuilder(args);

// Configuración de BD Postgres
var connectionString = builder.Configuration.GetConnectionString("Default");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

// Servicios de Dominio
builder.Services.AddScoped<TransactionProcessor>();
builder.Services.AddHostedService<HealthMonitorService>();

var app = builder.Build();

// Auto-crear / Migrar la base de datos al iniciar el nodo
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    try
    {
        db.Database.EnsureCreated();
    }
    catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P07")
    {
        // La tabla ya la creó otro nodo que inició milisegundos antes
    }
}
var nodeId = builder.Configuration["NODE_ID"] ?? "Node-Unknown";

// Endpoints
app.MapGet("/", () => $"SolidarityGrid Node: {nodeId} online.");

app.MapPost("/pay", async (PayRequest req, TransactionProcessor processor) =>
{
    var tx = await processor.CreateTransactionAsync(req.Amount);
    
    // Iniciar el procesamiento pesado en segundo plano sin bloquear la respuesta del API
    _ = Task.Run(async () =>
    {
        using var scope = app.Services.CreateScope();
        var scopedProcessor = scope.ServiceProvider.GetRequiredService<TransactionProcessor>();
        await scopedProcessor.StartProcessingAsync(tx.Id);
    });

    return Results.Accepted($"/transactions/{tx.Id}", new { tx.Id, Message = "Pago en proceso", Node = nodeId });
});

app.MapGet("/transactions/{id:guid}", async (Guid id, AppDbContext db) =>
{
    var tx = await db.Transactions.FindAsync(id);
    return tx is not null ? Results.Ok(tx) : Results.NotFound();
});

app.Run();