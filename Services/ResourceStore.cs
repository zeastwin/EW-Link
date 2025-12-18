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

    public void Rename(ResourceTab tab, string relativePath, string newName)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Relative path is required.", nameof(relativePath));
        }

        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new InvalidOperationException("新名称不能为空。");
        }

        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("新名称包含非法字符。");
        }

        if (newName.Contains(Path.DirectorySeparatorChar) || newName.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidOperationException("新名称不能包含路径分隔符。");
        }

        var subRoot = _pathHelper.ResolveSubRoot(tab);
        var sourceFull = _pathHelper.ResolveSafeFullPath(subRoot, relativePath);

        var parentDir = Path.GetDirectoryName(sourceFull);
        if (string.IsNullOrEmpty(parentDir))
        {
            throw new InvalidOperationException("无法确定父目录，重命名终止。");
        }

        var targetFull = Path.Combine(parentDir, newName);

        if (string.Equals(sourceFull, targetFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("新名称与原名称相同。");
        }

        if (File.Exists(targetFull) || Directory.Exists(targetFull))
        {
            throw new InvalidOperationException("目标名称已存在。");
        }

        if (Directory.Exists(sourceFull))
        {
            Directory.Move(sourceFull, targetFull);
            _logger.LogInformation("Renamed directory: {Source} -> {Target}", sourceFull, targetFull);
            return;
        }

        if (File.Exists(sourceFull))
        {
            File.Move(sourceFull, targetFull);
            _logger.LogInformation("Renamed file: {Source} -> {Target}", sourceFull, targetFull);
            return;
        }

        throw new FileNotFoundException("路径无效或不存在。", sourceFull);
    }

    public void MoveMany(ResourceTab tab, IEnumerable<string> relativePaths, string? targetDirectoryRelativePath)
    {
        if (relativePaths == null)
        {
            throw new ArgumentNullException(nameof(relativePaths));
        }

        var pathList = relativePaths.Where(p => p != null).ToList();
        if (pathList.Count == 0)
        {
            throw new InvalidOperationException("未提供有效的路径。");
        }

        var subRoot = _pathHelper.ResolveSubRoot(tab);
        var targetDir = _pathHelper.ResolveSafeFullPath(subRoot, targetDirectoryRelativePath);

        if (!Directory.Exists(targetDir))
        {
            throw new DirectoryNotFoundException("目标目录不存在。");
        }

        foreach (var rawPath in pathList)
        {
            var trimmed = rawPath?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                throw new InvalidOperationException("包含空的路径参数。");
            }

            var sourceFull = _pathHelper.ResolveSafeFullPath(subRoot, trimmed);

            if (Directory.Exists(sourceFull))
            {
                if (IsSubPath(sourceFull, targetDir))
                {
                    throw new InvalidOperationException("不能将目录移动到其自身或子目录下。");
                }
            }

            if (!Directory.Exists(sourceFull) && !File.Exists(sourceFull))
            {
                throw new FileNotFoundException("路径无效或不存在。", sourceFull);
            }

            var targetFull = Path.Combine(targetDir, Path.GetFileName(sourceFull));
            if (File.Exists(targetFull) || Directory.Exists(targetFull))
            {
                throw new InvalidOperationException("目标目录下已存在同名项。");
            }
        }

        foreach (var rawPath in pathList)
        {
            var sourceFull = _pathHelper.ResolveSafeFullPath(subRoot, rawPath);
            var targetFull = Path.Combine(targetDir, Path.GetFileName(sourceFull));

            if (Directory.Exists(sourceFull))
            {
                Directory.Move(sourceFull, targetFull);
                _logger.LogInformation("Moved directory: {Source} -> {Target}", sourceFull, targetFull);
            }
            else if (File.Exists(sourceFull))
            {
                File.Move(sourceFull, targetFull);
                _logger.LogInformation("Moved file: {Source} -> {Target}", sourceFull, targetFull);
            }
        }
    }

    public List<(string entryName, Stream fileStream)> OpenStreamsForZip(ResourceTab tab, IEnumerable<string> relativePaths)
    {
        if (relativePaths == null)
        {
            throw new ArgumentNullException(nameof(relativePaths));
        }

        var subRoot = _pathHelper.ResolveSubRoot(tab);
        var streams = new List<(string entryName, Stream fileStream)>();
        var deduplicate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var rawPath in relativePaths)
            {
                var trimmed = rawPath?.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    throw new InvalidOperationException("包含空的路径参数。");
                }

                var fullPath = _pathHelper.ResolveSafeFullPath(subRoot, trimmed);
                if (File.Exists(fullPath))
                {
                    var entryName = NormalizeZipEntry(subRoot, fullPath);
                    if (deduplicate.Add(entryName))
                    {
                        streams.Add((entryName, CreateReadStream(fullPath)));
                    }
                    continue;
                }

                if (Directory.Exists(fullPath))
                {
                    var files = Directory.GetFiles(fullPath, "*", SearchOption.AllDirectories);
                    if (files.Length == 0)
                    {
                        var dirEntry = NormalizeDirectoryEntry(subRoot, fullPath);
                        if (deduplicate.Add(dirEntry))
                        {
                            streams.Add((dirEntry, new MemoryStream(Array.Empty<byte>())));
                        }
                        continue;
                    }

                    foreach (var file in files)
                    {
                        var entryName = NormalizeZipEntry(subRoot, file);
                        if (deduplicate.Add(entryName))
                        {
                            streams.Add((entryName, CreateReadStream(file)));
                        }
                    }

                    continue;
                }

                throw new FileNotFoundException("路径无效或不存在。", fullPath);
            }

            return streams;
        }
        catch
        {
            foreach (var item in streams)
            {
                item.fileStream.Dispose();
            }

            throw;
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

    private static string NormalizeZipEntry(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        if (string.IsNullOrEmpty(relative) || relative == ".")
        {
            throw new InvalidOperationException("不能直接打包资源根目录。");
        }
        return relative.Replace('\\', '/');
    }

    private static string NormalizeDirectoryEntry(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/').TrimEnd('/');
        if (string.IsNullOrEmpty(relative) || relative == ".")
        {
            throw new InvalidOperationException("不能直接打包资源根目录。");
        }
        return string.IsNullOrEmpty(relative) ? string.Empty : $"{relative}/";
    }

    private static FileStream CreateReadStream(string fullPath)
    {
        return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static bool IsSubPath(string basePath, string targetPath)
    {
        var normalizedBase = Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedTarget = Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedTarget.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase);
    }
}
