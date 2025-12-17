using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace EW_Link.Services;

public class ZipStreamService : IZipStreamService
{
    private readonly ILogger<ZipStreamService> _logger;
    private const int BufferSize = 81920;

    public ZipStreamService(ILogger<ZipStreamService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task WriteZipAsync(Stream responseBody, IEnumerable<(string entryName, Stream fileStream)> items, CancellationToken cancellationToken = default)
    {
        if (responseBody == null)
        {
            throw new ArgumentNullException(nameof(responseBody));
        }

        if (items == null)
        {
            throw new ArgumentNullException(nameof(items));
        }

        using var archive = new ZipArchive(responseBody, ZipArchiveMode.Create, leaveOpen: true);

        foreach (var (entryName, fileStream) in items)
        {
            if (string.IsNullOrWhiteSpace(entryName))
            {
                throw new ArgumentException("Entry name is required.", nameof(items));
            }

            if (fileStream == null)
            {
                throw new ArgumentException("File stream is required.", nameof(items));
            }

            var normalizedEntryName = NormalizeEntryName(entryName);
            _logger.LogInformation("Adding {EntryName} to zip stream.", normalizedEntryName);

            var entry = archive.CreateEntry(normalizedEntryName);
            await using var entryStream = entry.Open();
            await fileStream.CopyToAsync(entryStream, BufferSize, cancellationToken);
            await entryStream.FlushAsync(cancellationToken);
        }
    }

    private static string NormalizeEntryName(string name)
    {
        var normalized = name.Replace('\\', '/');
        while (normalized.StartsWith("/", StringComparison.Ordinal))
        {
            normalized = normalized[1..];
        }

        if (string.IsNullOrEmpty(normalized))
        {
            return "file";
        }

        return normalized;
    }
}
