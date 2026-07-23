using Microsoft.EntityFrameworkCore;

namespace SolidarityGrid.Node;

public class TransactionProcessor
{
    private readonly AppDbContext _db;
    private readonly string _nodeId;
    private readonly ILogger<TransactionProcessor> _logger;

    public TransactionProcessor(AppDbContext db, IConfiguration config, ILogger<TransactionProcessor> logger)
    {
        _db = db;
        _nodeId = config["NODE_ID"] ?? "Node-Unknown";
        _logger = logger;
    }

    public async Task<Transaction> CreateTransactionAsync(decimal amount)
    {
        var tx = new Transaction
        {
            Amount = amount,
            Status = TransactionStatus.Pending
        };

        _db.Transactions.Add(tx);
        await _db.SaveChangesAsync();
        return tx;
    }

    public async Task StartProcessingAsync(Guid txId)
    {
        var tx = await _db.Transactions.FindAsync(txId);
        if (tx == null) return;

        tx.Status = TransactionStatus.Processing;
        tx.ProcessedBy = _nodeId;
        tx.LeaseUntil = DateTime.UtcNow.AddSeconds(4); // Lease activo por 4s
        await _db.SaveChangesAsync();

        await ExecuteProcessingLoopAsync(tx);
    }

    public async Task ExecuteProcessingLoopAsync(Transaction tx)
    {
        _logger.LogInformation("➡️ [{NodeId}]: Iniciando procesamiento de pago TX-{TxId} (Total: 10s)...", _nodeId, tx.Id);

        try
        {
            for (int step = 1; step <= 10; step++)
            {
                await Task.Delay(1000); // Simular 1s de trabajo pesado

                // Renovar Lease en la BD para informar que seguimos vivos
                tx.LeaseUntil = DateTime.UtcNow.AddSeconds(4);
                await _db.SaveChangesAsync();

                _logger.LogInformation("⏳ [{NodeId}]: Procesando TX-{TxId} ({Step}/10s) - Lease renovado.", _nodeId, tx.Id, step);
            }

            tx.Status = TransactionStatus.Completed;
            tx.LeaseUntil = null;
            await _db.SaveChangesAsync();

            _logger.LogInformation("✅ [{NodeId}]: Transacción TX-{TxId} completada con éxito.", _nodeId, tx.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ [{NodeId}]: Ocurrió un error inesperado en TX-{TxId}", _nodeId, tx.Id);
        }
    }

    public async Task TryClaimOrphanedTransactionsAsync()
    {
        var now = DateTime.UtcNow;

        // Buscar transacciones que se quedaron en 'Processing' pero su Lease ya expiró
        var expiredTxIds = await _db.Transactions
            .Where(t => t.Status == TransactionStatus.Processing && t.LeaseUntil < now)
            .Select(t => t.Id)
            .ToListAsync();

        foreach (var txId in expiredTxIds)
        {
            // Operación atómica de actualización: Solo un nodo ganará la fila
            var newLease = DateTime.UtcNow.AddSeconds(4);
            
            var rowsAffected = await _db.Database.ExecuteSqlRawAsync(
                @"UPDATE ""Transactions"" 
                  SET ""ProcessedBy"" = {0}, ""LeaseUntil"" = {1} 
                  WHERE ""Id"" = {2} AND ""LeaseUntil"" < {3} AND ""Status"" = {4}",
                _nodeId, newLease, txId, now, (int)TransactionStatus.Processing);

            if (rowsAffected > 0)
            {
                _logger.LogWarning("⚠️ [{NodeId}]: Detecté que el nodo anterior dejó de responder. Asumiendo transacción TX-{TxId}...", _nodeId, txId);

                var tx = await _db.Transactions.FindAsync(txId);
                if (tx != null)
                {
                    // Tomar la posta y terminar la transacción
                    await ExecuteProcessingLoopAsync(tx);
                }
            }
        }
    }
}