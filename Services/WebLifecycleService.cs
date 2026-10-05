namespace BusGo.Services;

public sealed class WebLifecycleService(ILogger<WebLifecycleService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            do
            {
                try
                {
                    await PaymentService.ExpirePendingPaymentsAsync();
                    await TicketLifecycleService.MarkDepartedTicketsUsedAsync();
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(exception, "Ticket maintenance failed; next scheduled pass will try again.");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
