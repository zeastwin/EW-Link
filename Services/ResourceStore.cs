using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EW_Link.Models;
using EW_Link.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EW_Link.Services;

public enum ResourceSortField
{
    Name,
    LastWriteTime,
    Size
}

public enum SortDirection
{
    Asc,
    Desc
}

public class ResourceStore : IResourceStore
{
    private const long UploadLimitBytes = 1024L * 1024 * 1024;
    private readonly ResourcePathHelper _pathHelper;
    private readonly ResourceOptions _options;
    private readonly ILogger<ResourceStore> _logger;

    public ResourceStore(IOptions<ResourceOptions> options, ILogger<ResourceStore> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _pathHelper = new ResourcePathHelper(_options);
    }

    public void EnsureRoots()
    {
        var permanentRoot = _pathHelper.ResolveSubRoot(ResourceTab.Permanent);
        var temporaryRoot = _pathHelper.ResolveSubRoot(ResourceTab.Temporary);

        Directory.CreateDirectory(permanentRoot);
        Directory.CreateDirectory(temporaryRoot);

        _logger.LogInformation("Ensured resource roots. Permanent: {PermanentRoot}; Temporary: {TemporaryRoot}", permanentRoot, temporaryRoot);
    }

    public IReadOnlyList<ResourceEntry> List(ResourceTab tab, string? relativePath, string? filter, ResourceSortField sortField, SortDirection sortDirection)
    {
        var subRoot = _pathHelper.ResolveSubRoot(tab);
        var safeFullPath = _pathHelper.ResolveSafeFullPath(subRoot, relativePath);

        if (!Directory.Exists(safeFullPath))
        {
            throw new DirectoryNotFoundException($"Directory not found: {safeFullPath}");
        }

        var entries = new List<ResourceEntry>();

        foreach (var dir in Directory.GetDirectories(safeFullPath))
        {
            var dirName = Path.GetFileName(dir);
            entries.Add(new ResourceEntry
            {
                Name = dirName,
                RelativePath = CombineRelativePath(relativePath, dirName),
                IsDirectory = true,
                SizeBytes = 0,
                LastWriteTime = new DateTimeOffset(Directory.GetLastWriteTimeUtc(dir))
            });
        }

        foreach (var file in Directory.GetFiles(safeFullPath))
        {
            var info = new FileInfo(file);
            entries.Add(new ResourceEntry
            {
                Name = info.Name,
                RelativePath = CombineRelativePath(relativePath, info.Name),
                IsDirectory = false,
                SizeBytes = info.Length,
                LastWriteTime = new DateTimeOffset(info.LastWriteTimeUtc)
            });
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            entries = entries
                .Where(e => e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        IOrderedEnumerable<ResourceEntry> ordered = sortField switch
        {
            ResourceSortField.LastWriteTime => entries.OrderBy(e => e.LastWriteTime),
            ResourceSortField.Size => entries.OrderBy(e => e.SizeBytes),
            _ => entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
        };

        if (sortDirection == SortDirection.Desc)
        {
            ordered = sortField switch
            {
                ResourceSortField.LastWriteTime => entries.OrderByDescending(e => e.LastWriteTime),
                ResourceSortField.Size => entries.OrderByDescending(e => e.SizeBytes),
                _ => entries.OrderByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase)
            };
        }

        return ordered.ToList();
    }

    public void CreateDirectoryIfNotExists(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            throw new ArgumentException("Path is required.", nameof(fullPath));
        }

        if (!Directory.Exists(fullPath))
        {
            Directory.CreateDirectory(fullPath);
        }
    }

    public void CreateDirectory(ResourceTab tab, string? baseRelativePath, string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            throw new InvalidOperationException("文件夹名称不能为空。");
        }

        if (folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("文件夹名称包含非法字符。");
        }

        var subRoot = _pathHelper.ResolveSubRoot(tab);
        var targetPath = _pathHelper.ResolveSafeFullPath(subRoot, CombineRelativePath(baseRelativePath, folderName));
        Directory.CreateDirectory(targetPath);
        _logger.LogInformation("Created directory: {Path}", targetPath);
    }

    public async Task<ResourceEntry> SaveUpload(ResourceTab tab, string? relativePath, IFormFile file, CancellationToken cancellationToken = default)
    {
        if (file == null)
        {
            throw new ArgumentNullException(nameof(file));
        }

        if (file.Length > UploadLimitBytes)
        {
            throw new InvalidOperationException($"File exceeds the maximum allowed size of {UploadLimitBytes} bytes.");
        }

        var fileName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException("File name is required.");
        }

        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("File name contains invalid characters.");
        }

        var subRoot = _pathHelper.ResolveSubRoot(tab);
        var targetDirectory = _pathHelper.ResolveSafeFullPath(subRoot, relativePath);
        CreateDirectoryIfNotExists(targetDirectory);

        var targetFileName = GetAvailableFileName(targetDirectory, fileName);
        var targetPath = Path.Combine(targetDirectory, targetFileName);

        _logger.LogInformation("Saving upload to {TargetPath}", targetPath);

        await using (var stream = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await file.CopyToAsync(stream, cancellationToken);
        }

        var info = new FileInfo(targetPath);
        return new ResourceEntry
        {
            Name = info.Name,
            RelativePath = CombineRelativePath(relativePath, info.Name),
            IsDirectory = false,
            SizeBytes = info.Length,
            LastWriteTime = new DateTimeOffset(info.LastWriteTimeUtc)
        };
    }

    public FileStream OpenRead(ResourceTab tab, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Relative path is required.", nameof(relativePath));
        }

        var subRoot = _pathHelper.ResolveSubRoot(tab);
        var fullPath = _pathHelper.ResolveSafeFullPath(subRoot, relativePath);

        if (Directory.Exists(fullPath))
        {
            throw new InvalidOperationException("Cannot open a directory for reading.");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("File not found.", fullPath);
        }

        _logger.LogInformation("Opening file stream for {FilePath}", fullPath);

        return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public void Delete(ResourceTab tab, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Relative path is required.", nameof(relativePath));
        }

        var subRoot = _pathHelper.ResolveSubRoot(tab);
        var fullPath = _pathHelper.ResolveSafeFullPath(subRoot, relativePath);

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
            _logger.LogInformation("Deleted directory: {Path}", fullPath);
            return;
        }

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            _logger.LogInformation("Deleted file: {Path}", fullPath);
            return;
        }

        throw new FileNotFoundException("Path not found.", fullPath);
    }

    public void DeleteMany(ResourceTab tab, IEnumerable<string> relativePaths)
    {
        foreach (var path in relativePaths)
        {
            Delete(tab, path);
        }
    }

    private static string CombineRelativePath(string? basePath, string name)
    {
        if (string.IsNullOrEmpty(basePath))
        {
            return name;
        }

        var trimmed = basePath.TrimEnd('/', '\\');
        return $"{trimmed}/{name}";
    }

    private static string GetAvailableFileName(string directory, string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidate = fileName;
        var counter = 1;

        while (File.Exists(Path.Combine(directory, candidate)))
        {
            candidate = $"{name} ({counter}){extension}";
            counter++;
        }

        return candidate;
    }
}
