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

/// <summary>
/// 清理上传临时目录（.uploading）中的过期残留文件，避免半成品占用空间。
/// </summary>
public class UploadCleanupService : BackgroundService
{
    private readonly ILogger<UploadCleanupService> _logger;
    private readonly ResourcePathHelper _pathHelper;
    private readonly TimeSpan _interval = TimeSpan.FromHours(1);
    private readonly TimeSpan _expiration = TimeSpan.FromHours(6);
    private const string UploadTempFolderName = ".uploading";

    public UploadCleanupService(IOptions<ResourceOptions> options, ILogger<UploadCleanupService> logger)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _pathHelper = new ResourcePathHelper(options.Value);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Upload cleanup service started. Interval: {Interval}, Expiration: {Expiration}", _interval, _expiration);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CleanExpired(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during upload temp cleanup.");
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
        var cutoff = DateTimeOffset.UtcNow - _expiration;
        foreach (var tab in new[] { ResourceTab.Permanent, ResourceTab.Temporary })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var subRoot = _pathHelper.ResolveSubRoot(tab);
            var uploadRoot = Path.Combine(subRoot, UploadTempFolderName);
            if (!Directory.Exists(uploadRoot))
            {
                continue;
            }

            var files = Directory.EnumerateFiles(uploadRoot, "*", SearchOption.AllDirectories).ToList();
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc < cutoff)
                    {
                        File.Delete(file);
                        _logger.LogInformation("Deleted expired upload temp file: {File}", file);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete upload temp file: {File}", file);
                }
            }

            // 清理空目录
            var directories = Directory.EnumerateDirectories(uploadRoot, "*", SearchOption.AllDirectories)
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
                        _logger.LogInformation("Removed empty upload temp dir: {Dir}", dir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove upload temp dir: {Dir}", dir);
                }
            }
        }
    }
}
