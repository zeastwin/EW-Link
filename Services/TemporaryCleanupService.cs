using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EW_Link.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EW_Link.Services;

public class TemporaryCleanupService : BackgroundService
{
    private readonly ILogger<TemporaryCleanupService> _logger;
    private readonly ResourceOptions _options;
    private readonly ResourcePathHelper _pathHelper;
    private readonly TimeSpan _interval = TimeSpan.FromHours(1);
    private readonly TimeSpan _expiration = TimeSpan.FromHours(72);

    public TemporaryCleanupService(IOptions<ResourceOptions> options, ILogger<TemporaryCleanupService> logger)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _pathHelper = new ResourcePathHelper(_options);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Temporary cleanup service started. Interval: {Interval}, Expiration: {Expiration}", _interval, _expiration);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CleanExpired(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during temporary files cleanup.");
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

    private void CleanExpired(CancellationToken cancellationToken)
    {
        var tempRoot = _pathHelper.ResolveSubRoot(ResourceTab.Temporary);
        if (!Directory.Exists(tempRoot))
        {
            return;
        }

        var cutoff = DateTimeOffset.UtcNow - _expiration;
        var files = Directory.EnumerateFiles(tempRoot, "*", SearchOption.AllDirectories).ToList();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc < cutoff)
                {
                    File.Delete(file);
                    _logger.LogInformation("Deleted expired temp file: {File}", file);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete temp file: {File}", file);
            }
        }

        // cleanup empty directories
        var directories = Directory.EnumerateDirectories(tempRoot, "*", SearchOption.AllDirectories)
            .OrderByDescending(d => d.Length)
            .ToList();

        foreach (var dir in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                    _logger.LogInformation("Removed empty directory: {Directory}", dir);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove directory: {Directory}", dir);
            }
        }
    }
}
