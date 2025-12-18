using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EW_Link.Services;

public class TrashCleanupService : BackgroundService
{
    private readonly ILogger<TrashCleanupService> _logger;
    private readonly IResourceStore _resourceStore;
    private readonly TimeSpan _interval = TimeSpan.FromHours(1);
    private readonly TimeSpan _retention = TimeSpan.FromDays(30);

    public TrashCleanupService(IResourceStore resourceStore, ILogger<TrashCleanupService> logger)
    {
        _resourceStore = resourceStore ?? throw new ArgumentNullException(nameof(resourceStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Trash cleanup service started. Interval: {Interval}, Retention: {Retention}", _interval, _retention);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cutoff = DateTimeOffset.UtcNow - _retention;
                _resourceStore.CleanupTrash(ResourceTab.Permanent, cutoff);
                _resourceStore.CleanupTrash(ResourceTab.Temporary, cutoff);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during trash cleanup.");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
        }
    }
}
