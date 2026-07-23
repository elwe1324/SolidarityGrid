namespace SolidarityGrid.Node;

public class HealthMonitorService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<HealthMonitorService> _logger;

    public HealthMonitorService(IServiceProvider serviceProvider, ILogger<HealthMonitorService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<TransactionProcessor>();
                
                // Vigilar y reclamar si algún compañero murió
                await processor.TryClaimOrphanedTransactionsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error durante el ciclo de monitoreo de salud.");
            }

            // Chequeo de red cada 1.5 segundos
            await Task.Delay(1500, stoppingToken);
        }
    }
}