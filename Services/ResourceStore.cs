using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    private const long UploadLimitBytes = 10L * 1024 * 1024 * 1024;
    private const string TrashFolderName = ".trash";
    private const string UploadTempFolderName = ".uploading";
    private const string MetadataFileName = "metadata.json";
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
        Directory.CreateDirectory(GetTrashRoot(permanentRoot));
        Directory.CreateDirectory(GetTrashRoot(temporaryRoot));
        Directory.CreateDirectory(GetUploadTempRoot(permanentRoot));
        Directory.CreateDirectory(GetUploadTempRoot(temporaryRoot));

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
            if (string.Equals(dirName, TrashFolderName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(dirName, UploadTempFolderName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
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

        var tempRoot = GetUploadTempRoot(subRoot);
        Directory.CreateDirectory(tempRoot);
        var tempPath = Path.Combine(tempRoot, $"{Guid.NewGuid():N}.upload");

        _logger.LogInformation("Saving upload to temp {TempPath} then moving to {TargetPath}", tempPath, targetPath);

        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await file.CopyToAsync(stream, cancellationToken);
            }

            var tempInfo = new FileInfo(tempPath);
            if (tempInfo.Length != file.Length)
            {
                throw new IOException("Uploaded file size mismatch, aborting.");
            }

            File.Move(tempPath, targetPath);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception cleanupEx)
            {
                _logger.LogWarning(cleanupEx, "Failed to cleanup temp upload file: {TempPath}", tempPath);
            }

            throw;
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
        MoveToTrash(tab, new[] { relativePath });
    }

    public void DeleteMany(ResourceTab tab, IEnumerable<string> relativePaths)
    {
        MoveToTrash(tab, relativePaths);
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

    private void MoveToTrash(ResourceTab tab, IEnumerable<string> relativePaths)
    {
        if (relativePaths == null)
        {
            throw new ArgumentNullException(nameof(relativePaths));
        }

        var paths = relativePaths.Where(p => p != null).ToList();
        if (paths.Count == 0)
        {
            throw new InvalidOperationException("未提供有效的路径。");
        }

        var subRoot = _pathHelper.ResolveSubRoot(tab);
        var trashRoot = GetTrashRoot(subRoot);
        Directory.CreateDirectory(trashRoot);

        foreach (var raw in paths)
        {
            var trimmed = raw?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                throw new InvalidOperationException("包含空的路径参数。");
            }

            if (trimmed.StartsWith(TrashFolderName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("回收站目录不允许直接操作。");
            }

            var sourceFull = _pathHelper.ResolveSafeFullPath(subRoot, trimmed);
            var isDirectory = Directory.Exists(sourceFull);
            var isFile = File.Exists(sourceFull);

            if (!isDirectory && !isFile)
            {
                throw new FileNotFoundException("路径无效或不存在。", sourceFull);
            }

            var entryId = Guid.NewGuid().ToString("N");
            var container = Path.Combine(trashRoot, entryId);
            Directory.CreateDirectory(container);

            var name = Path.GetFileName(sourceFull);
            var targetPath = Path.Combine(container, name);

            var metadata = new TrashMetadata
            {
                Id = entryId,
                OriginalPath = trimmed,
                OriginalName = name,
                IsDirectory = isDirectory,
                DeletedAt = DateTimeOffset.UtcNow,
                SizeBytes = isFile ? new FileInfo(sourceFull).Length : GetDirectorySize(sourceFull)
            };

            if (isDirectory)
            {
                Directory.Move(sourceFull, targetPath);
            }
            else
            {
                File.Move(sourceFull, targetPath);
            }

            WriteMetadata(container, metadata);
            _logger.LogInformation("Moved to trash: {Source} -> {Target}", sourceFull, container);
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

    public IReadOnlyList<TrashEntry> ListTrash(ResourceTab tab)
    {
        var subRoot = _pathHelper.ResolveSubRoot(tab);
        var trashRoot = GetTrashRoot(subRoot);
        Directory.CreateDirectory(trashRoot);

        var results = new List<TrashEntry>();
        foreach (var dir in Directory.EnumerateDirectories(trashRoot))
        {
            try
            {
                var metadata = ReadMetadata(dir);
                if (metadata == null)
                {
                    continue;
                }

                results.Add(new TrashEntry
                {
                    Id = metadata.Id,
                    Name = metadata.OriginalName,
                    OriginalPath = metadata.OriginalPath,
                    IsDirectory = metadata.IsDirectory,
                    DeletedAt = metadata.DeletedAt,
                    SizeBytes = metadata.SizeBytes
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取回收站条目失败：{Dir}", dir);
            }
        }

        return results
            .OrderByDescending(e => e.DeletedAt)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void RestoreFromTrash(ResourceTab tab, IEnumerable<string> ids)
    {
        if (ids == null)
        {
            throw new ArgumentNullException(nameof(ids));
        }

        var subRoot = _pathHelper.ResolveSubRoot(tab);
        var trashRoot = GetTrashRoot(subRoot);

        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidOperationException("包含无效的回收站 ID。");
            }

            var entryDir = Path.Combine(trashRoot, id);
            if (!Directory.Exists(entryDir))
            {
                throw new DirectoryNotFoundException($"回收站条目不存在：{id}");
            }

            var metadata = ReadMetadata(entryDir) ?? throw new InvalidOperationException("回收站元数据缺失或无效。");
            var targetFull = _pathHelper.ResolveSafeFullPath(subRoot, metadata.OriginalPath);

            if (File.Exists(targetFull) || Directory.Exists(targetFull))
            {
                throw new InvalidOperationException($"目标位置已存在同名项：{metadata.OriginalPath}");
            }

            var parent = Path.GetDirectoryName(targetFull);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            var storedPath = Path.Combine(entryDir, metadata.OriginalName);
            if (metadata.IsDirectory)
            {
                Directory.Move(storedPath, targetFull);
            }
            else
            {
                File.Move(storedPath, targetFull);
            }

            Directory.Delete(entryDir, recursive: true);
            _logger.LogInformation("Restored from trash: {Original} (Id: {Id})", targetFull, id);
        }
    }

    public void PurgeTrash(ResourceTab tab, IEnumerable<string> ids)
    {
        if (ids == null)
        {
            throw new ArgumentNullException(nameof(ids));
        }

        var subRoot = _pathHelper.ResolveSubRoot(tab);
        var trashRoot = GetTrashRoot(subRoot);

        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var entryDir = Path.Combine(trashRoot, id);
            if (Directory.Exists(entryDir))
            {
                Directory.Delete(entryDir, recursive: true);
                _logger.LogInformation("Purged trash entry: {Id}", id);
            }
        }
    }

    public void CleanupTrash(ResourceTab tab, DateTimeOffset cutoff)
    {
        var subRoot = _pathHelper.ResolveSubRoot(tab);
        var trashRoot = GetTrashRoot(subRoot);
        if (!Directory.Exists(trashRoot))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(trashRoot))
        {
            try
            {
                var metadata = ReadMetadata(dir);
                if (metadata == null)
                {
                    continue;
                }

                if (metadata.DeletedAt < cutoff)
                {
                    Directory.Delete(dir, recursive: true);
                    _logger.LogInformation("Cleaned expired trash entry: {Dir}", dir);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean trash entry: {Dir}", dir);
            }
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

    private static string GetTrashRoot(string subRoot) => Path.Combine(subRoot, TrashFolderName);

    private static string GetUploadTempRoot(string subRoot) => Path.Combine(subRoot, UploadTempFolderName);

    private static bool IsSubPath(string basePath, string targetPath)
    {
        var normalizedBase = Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedTarget = Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedTarget.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase);
    }

    private static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        long size = 0;
        var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                size += info.Length;
            }
            catch
            {
                // ignore single file errors
            }
        }

        return size;
    }

    private static void WriteMetadata(string directory, TrashMetadata metadata)
    {
        var json = JsonSerializer.Serialize(metadata);
        File.WriteAllText(Path.Combine(directory, MetadataFileName), json);
    }

    private static TrashMetadata? ReadMetadata(string directory)
    {
        var metadataPath = Path.Combine(directory, MetadataFileName);
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        var json = File.ReadAllText(metadataPath);
        return JsonSerializer.Deserialize<TrashMetadata>(json);
    }

    private class TrashMetadata
    {
        public required string Id { get; set; }
        public required string OriginalPath { get; set; }
        public required string OriginalName { get; set; }
        public required bool IsDirectory { get; set; }
        public DateTimeOffset DeletedAt { get; set; }
        public long SizeBytes { get; set; }
    }
}
