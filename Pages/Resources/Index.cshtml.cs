using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.IO.Pipelines;
using System.IO.Compression;
using System.Text;
using EW_Link.Models;
using EW_Link.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EW_Link.Pages.Resources;

public class IndexModel : PageModel
{
    private const long UploadLimitBytes = 10L * 1024 * 1024 * 1024;
    private const long PreviewLimitBytes = 20L * 1024 * 1024;
    private readonly IResourceStore _resourceStore;
    private readonly ILogger<IndexModel> _logger;
    private readonly IZipStreamService _zipStreamService;
    private readonly IShareLinkService _shareLinkService;
    private readonly int? _sharePortOverride;
    private static readonly HashSet<string> TextPreviewExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".json", ".xml", ".cs", ".js", ".css", ".html", ".md", ".ini", ".config"
    };

    public IndexModel(IResourceStore resourceStore, ILogger<IndexModel> logger, IZipStreamService zipStreamService, IShareLinkService shareLinkService, IConfiguration configuration)
    {
        _resourceStore = resourceStore ?? throw new ArgumentNullException(nameof(resourceStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _zipStreamService = zipStreamService ?? throw new ArgumentNullException(nameof(zipStreamService));
        _shareLinkService = shareLinkService ?? throw new ArgumentNullException(nameof(shareLinkService));
        if (int.TryParse(configuration?["Share:ForcePort"], out var portValue) && portValue > 0)
        {
            _sharePortOverride = portValue;
        }
    }

    public ResourceTab SelectedTab { get; private set; }
    public bool IsTrashView { get; private set; }
    public string? CurrentPath { get; private set; }
    public string? Filter { get; private set; }
    public ResourceSortField SortField { get; private set; }
    public SortDirection SortDirection { get; private set; }

    public IReadOnlyList<ResourceEntry> Entries { get; private set; } = Array.Empty<ResourceEntry>();
    public IReadOnlyList<TrashEntry> TrashEntries { get; private set; } = Array.Empty<TrashEntry>();
    public List<BreadcrumbItem> Breadcrumbs { get; private set; } = new();
    public int DirectoryCount { get; private set; }
    public int FileCount { get; private set; }
    public long TotalSize { get; private set; }

    public string SelectedTabString => SelectedTab == ResourceTab.Temporary ? "temporary" : "permanent";
    public string SortFieldParam => SortField switch
    {
        ResourceSortField.Name => "name",
        ResourceSortField.Size => "size",
        _ => "time"
    };
    public string SortDirectionParam => SortDirection == SortDirection.Asc ? "asc" : "desc";
    public string? ParentPath => CalculateParentPath(CurrentPath);

    public IActionResult OnGet([FromQuery] string? tab, [FromQuery] string? path, [FromQuery(Name = "q")] string? filter, [FromQuery] string? sort, [FromQuery] string? dir, [FromQuery] string? view)
    {
        if (!TryLoadPageData(tab, path, filter, sort, dir, view, out var errorResult))
        {
            return errorResult!;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostUpload([FromForm] string? tab, [FromForm] string? path, [FromForm(Name = "q")] string? filter, [FromForm] string? sort, [FromForm] string? dir, [FromForm] string? view, IFormFile[]? files)
    {
        if (!TryLoadPageData(tab, path, filter, sort, dir, view, out var errorResult))
        {
            return errorResult!;
        }

        if (files == null || files.Length == 0)
        {
            ModelState.AddModelError(string.Empty, "请选择要上传的文件。");
            return Page();
        }

        var successCount = 0;
        foreach (var file in files)
        {
            if (file == null)
            {
                continue;
            }

            if (file.Length > UploadLimitBytes)
            {
                _logger.LogWarning("上传失败：文件超出限制。Tab: {Tab}; Path: {Path}; FileName: {FileName}", SelectedTab, CurrentPath, file.FileName);
                ModelState.AddModelError(string.Empty, $"文件 {file.FileName} 超过 10GB 限制，已跳过。");
                continue;
            }

            try
            {
                await _resourceStore.SaveUpload(SelectedTab, CurrentPath, file, HttpContext.RequestAborted);
                successCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "上传失败。Tab: {Tab}; Path: {Path}; FileName: {FileName}", SelectedTab, CurrentPath, file.FileName);
                ModelState.AddModelError(string.Empty, $"上传失败：{file.FileName} - {ex.Message}");
            }
        }

        if (successCount == 0)
        {
            return Page();
        }

        TempData["SuccessMessage"] = $"上传成功 {successCount} 个文件。";
        return RedirectToPage("/Resources/Index", new
        {
            tab = SelectedTabString,
            path = CurrentPath,
            q = Filter,
            sort = SortFieldParam,
            dir = SortDirectionParam
        });
    }

    public async Task<IActionResult> OnPostDownloadZip([FromForm] string? tab, [FromForm] string? path, [FromForm(Name = "q")] string? filter, [FromForm] string? sort, [FromForm] string? dir, [FromForm] string? view, [FromForm] string[]? paths)
    {
        if (!TryLoadPageData(tab, path, filter, sort, dir, view, out var errorResult))
        {
            return errorResult!;
        }

        if (paths == null || paths.Length == 0)
        {
            ModelState.AddModelError(string.Empty, "请选择要下载的文件或文件夹。");
            return Page();
        }

        List<(string entryName, Stream fileStream)> streams;
        try
        {
            streams = _resourceStore.OpenStreamsForZip(SelectedTab, paths);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量下载校验失败。Tab: {Tab}; Path: {Path}", SelectedTab, CurrentPath);
            ModelState.AddModelError(string.Empty, $"批量下载失败：{ex.Message}");
            return Page();
        }

        Response.ContentType = "application/zip";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"resources.zip\"";

        try
        {
            // 使用 BodyWriter 生成的流避免 Kestrel 禁用同步 IO 时 ZipArchive 的同步写入报错
            await using var responseStream = Response.BodyWriter.AsStream(leaveOpen: true);
            await _zipStreamService.WriteZipAsync(responseStream, streams, HttpContext.RequestAborted);
            _logger.LogInformation("批量下载 ZIP 成功，条目数：{Count}，Tab: {Tab}, Path: {Path}", streams.Count, SelectedTab, CurrentPath);
        }
        finally
        {
            foreach (var entry in streams)
            {
                entry.fileStream.Dispose();
            }
        }

        return new EmptyResult();
    }

    public IActionResult OnPostCreateDirectory([FromForm] string? tab, [FromForm] string? path, [FromForm] string? filter, [FromForm] string? sort, [FromForm] string? dir, [FromForm] string? view, [FromForm] string? folderName)
    {
        if (!TryLoadPageData(tab, path, filter, sort, dir, view, out var errorResult))
        {
            return errorResult!;
        }

        if (string.IsNullOrWhiteSpace(folderName))
        {
            TempData["ErrorMessage"] = "文件夹名称不能为空。";
            return RedirectToPage("/Resources/Index", new { tab = SelectedTabString, path = CurrentPath, q = Filter, sort = SortFieldParam, dir = SortDirectionParam });
        }

        try
        {
            _resourceStore.CreateDirectory(SelectedTab, CurrentPath, folderName.Trim());
            TempData["SuccessMessage"] = $"已创建文件夹：{folderName}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            _logger.LogWarning(ex, "创建文件夹失败。Tab: {Tab}; Path: {Path}; Name: {Name}", SelectedTab, CurrentPath, folderName);
            TempData["ErrorMessage"] = $"创建失败：{ex.Message}";
        }

        return RedirectToPage("/Resources/Index", new { tab = SelectedTabString, path = CurrentPath, q = Filter, sort = SortFieldParam, dir = SortDirectionParam });
    }

    public IActionResult OnPostDelete([FromForm] string? tab, [FromForm] string? path, [FromForm(Name = "q")] string? filter, [FromForm] string? sort, [FromForm] string? dir, [FromForm] string? view, [FromForm] string[]? paths, [FromForm] string? target)
    {
        if (!TryLoadPageData(tab, path, filter, sort, dir, view, out var errorResult))
        {
            return errorResult!;
        }

        var targets = new List<string>();
        if (paths != null && paths.Length > 0)
        {
            targets.AddRange(paths.Where(p => !string.IsNullOrWhiteSpace(p))!);
        }
        else if (!string.IsNullOrWhiteSpace(target))
        {
            targets.Add(target);
        }

        if (targets.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "请选择要删除的路径。");
            return Page();
        }

        try
        {
            _resourceStore.DeleteMany(SelectedTab, targets);
            TempData["SuccessMessage"] = $"已移入回收站：{targets.Count} 项。";
        }
        catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException or FileNotFoundException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "删除失败。Tab: {Tab}; Targets: {Targets}", SelectedTab, string.Join(',', targets));
            TempData["ErrorMessage"] = "删除失败：路径无效或不存在。";
        }

        return RedirectToPage("/Resources/Index", new
        {
            tab = SelectedTabString,
            path = CurrentPath,
            q = Filter,
            sort = SortFieldParam,
            dir = SortDirectionParam,
            view = IsTrashView ? "trash" : null
        });
    }

    public IActionResult OnPostMove([FromForm] string? tab, [FromForm] string? path, [FromForm(Name = "q")] string? filter, [FromForm] string? sort, [FromForm] string? dir, [FromForm] string? view, [FromForm] string[]? paths, [FromForm] string? targetDir)
    {
        if (!TryLoadPageData(tab, path, filter, sort, dir, view, out var errorResult))
        {
            return errorResult!;
        }

        var targets = paths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? new List<string>();
        if (targets.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "请选择要移动的路径。");
            return Page();
        }

        try
        {
            _resourceStore.MoveMany(SelectedTab, targets, targetDir);
            TempData["SuccessMessage"] = $"已移动 {targets.Count} 项。";
        }
        catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException or FileNotFoundException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "移动失败。Tab: {Tab}; Targets: {Targets}; TargetDir: {TargetDir}", SelectedTab, string.Join(',', targets), targetDir);
            TempData["ErrorMessage"] = $"移动失败：{ex.Message}";
        }

        return RedirectToPage("/Resources/Index", new
        {
            tab = SelectedTabString,
            path = CurrentPath,
            q = Filter,
            sort = SortFieldParam,
            dir = SortDirectionParam,
            view = IsTrashView ? "trash" : null
        });
    }

    public IActionResult OnPostRename([FromForm] string? tab, [FromForm] string? path, [FromForm(Name = "q")] string? filter, [FromForm] string? sort, [FromForm] string? dir, [FromForm] string? view, [FromForm] string? renamePath, [FromForm] string? newName)
    {
        if (!TryLoadPageData(tab, path, filter, sort, dir, view, out var errorResult))
        {
            return errorResult!;
        }

        if (string.IsNullOrWhiteSpace(renamePath) || string.IsNullOrWhiteSpace(newName))
        {
            ModelState.AddModelError(string.Empty, "重命名参数无效。");
            return Page();
        }

        try
        {
            _resourceStore.Rename(SelectedTab, renamePath, newName.Trim());
            TempData["SuccessMessage"] = $"已重命名：{newName}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException or FileNotFoundException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "重命名失败。Tab: {Tab}; Path: {Path}; NewName: {NewName}", SelectedTab, renamePath, newName);
            TempData["ErrorMessage"] = $"重命名失败：{ex.Message}";
        }

        return RedirectToPage("/Resources/Index", new
        {
            tab = SelectedTabString,
            path = CurrentPath,
            q = Filter,
            sort = SortFieldParam,
            dir = SortDirectionParam,
            view = IsTrashView ? "trash" : null
        });
    }

    public IActionResult OnGetDownload([FromQuery] string? tab, [FromQuery] string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest("Path is required.");
        }

        var selectedTab = ParseTab(tab);
        FileStream? stream = null;
        try
        {
            stream = _resourceStore.OpenRead(selectedTab, path);
            var fileName = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = "download";
            }

            var provider = new FileExtensionContentTypeProvider();
            if (!provider.TryGetContentType(fileName, out var contentType))
            {
                contentType = "application/octet-stream";
            }

            _logger.LogInformation("Download requested. Tab: {Tab}; Path: {Path}; FileName: {FileName}", selectedTab, path, fileName);

            return File(stream, contentType, fileName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException or FileNotFoundException or UnauthorizedAccessException)
        {
            stream?.Dispose();
            _logger.LogWarning(ex, "Download failed for path {Path} in tab {Tab}", path, selectedTab);
            TempData["ErrorMessage"] = "下载失败：路径无效或文件不存在。";
            return RedirectToPage("/Resources/Index", new { tab = tab, path = CalculateParentPath(path) ?? string.Empty });
        }
    }

    public IActionResult OnGetDirs([FromQuery] string? tab, [FromQuery] string? path)
    {
        var selectedTab = ParseTab(tab);
        try
        {
            var entries = _resourceStore.List(selectedTab, path ?? string.Empty, null, ResourceSortField.Name, SortDirection.Asc)
                .Where(e => e.IsDirectory)
                .Select(e => new { name = e.Name, path = e.RelativePath })
                .ToList();
            return new JsonResult(new
            {
                success = true,
                currentPath = path ?? string.Empty,
                parentPath = CalculateParentPath(path),
                items = entries
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException or FileNotFoundException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "获取目录列表失败。Tab: {Tab}; Path: {Path}", selectedTab, path);
            return new JsonResult(new { success = false, message = "加载目录失败：路径无效或不存在。" });
        }
    }

    public async Task<IActionResult> OnGetPreview([FromQuery] string? tab, [FromQuery] string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest(new { message = "Path is required." });
        }

        var selectedTab = ParseTab(tab);
        try
        {
            using var stream = _resourceStore.OpenRead(selectedTab, path);
            var fileName = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return BadRequest(new { message = "Path is required." });
            }

            if (stream.Length > PreviewLimitBytes)
            {
                return new JsonResult(new { success = false, message = "文件过大，超过预览限制。" });
            }

            var provider = new FileExtensionContentTypeProvider();
            if (!provider.TryGetContentType(fileName, out var contentType))
            {
                contentType = "application/octet-stream";
            }

            if (IsTextPreview(fileName, contentType))
            {
                stream.Position = 0;
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
                var content = await reader.ReadToEndAsync();
                return new JsonResult(new { success = true, kind = "text", content, contentType });
            }

            if (IsImagePreview(contentType))
            {
                var previewUrl = BuildPreviewFileUrl(selectedTab, path);
                return new JsonResult(new { success = true, kind = "image", previewUrl, contentType });
            }

            if (IsAudioPreview(contentType))
            {
                var previewUrl = BuildPreviewFileUrl(selectedTab, path);
                return new JsonResult(new { success = true, kind = "audio", previewUrl, contentType });
            }

            if (IsVideoPreview(contentType))
            {
                var previewUrl = BuildPreviewFileUrl(selectedTab, path);
                return new JsonResult(new { success = true, kind = "video", previewUrl, contentType });
            }

            if (IsPdfPreview(contentType, fileName))
            {
                var previewUrl = BuildPreviewFileUrl(selectedTab, path);
                return new JsonResult(new { success = true, kind = "pdf", previewUrl, contentType });
            }

            if (IsZipPreview(contentType, fileName))
            {
                stream.Position = 0;
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
                var entries = archive.Entries.Take(200)
                    .Select(e => new { name = e.FullName, size = e.Length })
                    .ToList();
                var truncated = archive.Entries.Count > entries.Count;
                return new JsonResult(new { success = true, kind = "zip", entries, truncated });
            }

            return new JsonResult(new { success = false, message = "暂不支持该文件类型预览。" });
        }
        catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException or FileNotFoundException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.LogWarning(ex, "预览失败，路径 {Path}，Tab {Tab}", path, selectedTab);
            return new JsonResult(new { success = false, message = "预览失败：路径无效或文件不支持。" });
        }
    }

    public IActionResult OnGetPreviewFile([FromQuery] string? tab, [FromQuery] string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest("Path is required.");
        }

        var selectedTab = ParseTab(tab);
        FileStream? stream = null;
        try
        {
            stream = _resourceStore.OpenRead(selectedTab, path);
            if (stream == null)
            {
                throw new InvalidOperationException("无法打开文件流。");
            }
            var fileName = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = "preview";
            }

            var provider = new FileExtensionContentTypeProvider();
            if (!provider.TryGetContentType(fileName, out var contentType))
            {
                contentType = "application/octet-stream";
            }

            if (stream.Length > PreviewLimitBytes)
            {
                stream.Dispose();
                return BadRequest("文件过大，超过预览限制。");
            }

            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return File(stream, contentType);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException or FileNotFoundException or UnauthorizedAccessException)
        {
            stream?.Dispose();
            _logger.LogWarning(ex, "预览文件流失败，路径 {Path}，Tab {Tab}", path, selectedTab);
            return BadRequest("预览失败：路径无效或不支持。");
        }
    }

    public IActionResult OnPostRestoreTrash([FromForm] string? tab, [FromForm] string[]? ids)
    {
        var selectedTab = ParseTab(tab);
        var restoreIds = ids?.Where(i => !string.IsNullOrWhiteSpace(i)).ToList() ?? new List<string>();
        if (restoreIds.Count == 0)
        {
            TempData["ErrorMessage"] = "请选择要还原的项目。";
            return RedirectToPage("/Resources/Index", new { tab = tab, view = "trash" });
        }

        try
        {
            _resourceStore.RestoreFromTrash(selectedTab, restoreIds);
            TempData["SuccessMessage"] = $"已还原 {restoreIds.Count} 项。";
        }
        catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException or FileNotFoundException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "还原失败。Tab: {Tab}; Ids: {Ids}", selectedTab, string.Join(',', restoreIds));
            TempData["ErrorMessage"] = $"还原失败：{ex.Message}";
        }

        return RedirectToPage("/Resources/Index", new { tab = tab, view = "trash" });
    }

    public IActionResult OnPostPurgeTrash([FromForm] string? tab, [FromForm] string[]? ids)
    {
        var selectedTab = ParseTab(tab);
        var purgeIds = ids?.Where(i => !string.IsNullOrWhiteSpace(i)).ToList() ?? new List<string>();
        if (purgeIds.Count == 0)
        {
            TempData["ErrorMessage"] = "请选择要彻底删除的项目。";
            return RedirectToPage("/Resources/Index", new { tab = tab, view = "trash" });
        }

        try
        {
            _resourceStore.PurgeTrash(selectedTab, purgeIds);
            TempData["SuccessMessage"] = $"已彻底删除 {purgeIds.Count} 项。";
        }
        catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException or FileNotFoundException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "彻底删除失败。Tab: {Tab}; Ids: {Ids}", selectedTab, string.Join(',', purgeIds));
            TempData["ErrorMessage"] = $"彻底删除失败：{ex.Message}";
        }

        return RedirectToPage("/Resources/Index", new { tab = tab, view = "trash" });
    }

    private ResourceTab ParseTab(string? tab)
    {
        return string.Equals(tab, "permanent", StringComparison.OrdinalIgnoreCase)
            ? ResourceTab.Permanent
            : ResourceTab.Temporary;
    }

    private bool TryLoadPageData(string? tab, string? path, string? filter, string? sort, string? dir, string? view, out IActionResult? errorResult)
    {
        errorResult = null;

        SelectedTab = ParseTab(tab);
        IsTrashView = string.Equals(view, "trash", StringComparison.OrdinalIgnoreCase);
        CurrentPath = path ?? string.Empty;
        Filter = string.IsNullOrWhiteSpace(filter) ? null : filter;
        SortField = ParseSortField(sort);
        SortDirection = ParseSortDirection(dir);

        try
        {
            if (IsTrashView)
            {
                TrashEntries = _resourceStore.ListTrash(SelectedTab);
                Entries = Array.Empty<ResourceEntry>();
                DirectoryCount = 0;
                FileCount = 0;
                TotalSize = 0;
                Breadcrumbs = new List<BreadcrumbItem> { new("回收站", string.Empty) };
            }
            else
            {
                Entries = _resourceStore.List(SelectedTab, CurrentPath, Filter, SortField, SortDirection);
                ComputeStats();
                Breadcrumbs = BuildBreadcrumbs(CurrentPath);
            }
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException or FileNotFoundException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Load page data failed. Tab: {Tab}; Path: {Path}", SelectedTab, CurrentPath);
            TempData["ErrorMessage"] = IsTrashView ? "回收站加载失败。" : "路径无效或目录不存在，已回到根目录。";
            errorResult = RedirectToPage("/Resources/Index", new
            {
                tab = SelectedTabString,
                path = string.Empty,
                q = (string?)null,
                sort = SortFieldParam,
                dir = SortDirectionParam,
                view = IsTrashView ? "trash" : null
            });
            return false;
        }
    }

    private ResourceSortField ParseSortField(string? sort)
    {
        return sort?.ToLowerInvariant() switch
        {
            "name" => ResourceSortField.Name,
            "size" => ResourceSortField.Size,
            _ => ResourceSortField.LastWriteTime
        };
    }

    private SortDirection ParseSortDirection(string? dir)
    {
        return string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase) ? SortDirection.Asc : SortDirection.Desc;
    }

    private void ComputeStats()
    {
        DirectoryCount = Entries.Count(e => e.IsDirectory);
        FileCount = Entries.Count - DirectoryCount;
        TotalSize = Entries.Where(e => !e.IsDirectory).Sum(e => e.SizeBytes);
    }

    private List<BreadcrumbItem> BuildBreadcrumbs(string? path)
    {
        var breadcrumbs = new List<BreadcrumbItem> { new("根目录", string.Empty) };

        if (string.IsNullOrWhiteSpace(path))
        {
            return breadcrumbs;
        }

        var segments = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        var parts = new List<string>();
        foreach (var segment in segments)
        {
            parts.Add(segment);
            breadcrumbs.Add(new BreadcrumbItem(segment, string.Join('/', parts)));
        }

        return breadcrumbs;
    }

    private static string? CalculateParentPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var segments = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length <= 1)
        {
            return string.Empty;
        }

        return string.Join('/', segments.Take(segments.Length - 1));
    }

    public string FormatSize(long size)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = size;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }

    public record BreadcrumbItem(string Name, string RelativePath);

    private static bool IsTextPreview(string fileName, string contentType)
    {
        var ext = Path.GetExtension(fileName);
        return contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
               || TextPreviewExtensions.Contains(ext)
               || string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsImagePreview(string contentType)
    {
        return contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAudioPreview(string contentType)
    {
        return contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVideoPreview(string contentType)
    {
        return contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPdfPreview(string contentType, string fileName)
    {
        return string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase)
               || string.Equals(Path.GetExtension(fileName), ".pdf", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsZipPreview(string contentType, string fileName)
    {
        return string.Equals(contentType, "application/zip", StringComparison.OrdinalIgnoreCase)
               || string.Equals(Path.GetExtension(fileName), ".zip", StringComparison.OrdinalIgnoreCase);
    }

    public IActionResult OnPostCreateShare([FromForm] string? tab, [FromForm] string[]? targets)
    {
        var selectedTab = ParseTab(tab);
        var paths = targets?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (paths == null || paths.Count == 0)
        {
            return new JsonResult(new { success = false, message = "请选择有效的路径。" });
        }

        try
        {
            var token = _shareLinkService.GenerateToken(selectedTab, paths, DateTimeOffset.UtcNow.AddDays(7));
            var url = BuildShareUrl(token);
            return new JsonResult(new { success = true, url });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning(ex, "生成分享链接失败。Tab: {Tab}; Paths: {Paths}", selectedTab, string.Join(',', paths));
            return new JsonResult(new { success = false, message = "生成分享链接失败。" });
        }
    }

    public async Task<IActionResult> OnGetShareDownload([FromQuery] string? token)
    {
        if (!_shareLinkService.TryParseToken(token ?? string.Empty, out var tab, out var paths, out _))
        {
            return BadRequest("链接无效或已过期。");
        }

        FileStream? stream = null;
        try
        {
            if (paths.Count == 1)
            {
                var path = paths[0];
                try
                {
                    stream = _resourceStore.OpenRead(tab, path);
                    var fileName = Path.GetFileName(path);
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        fileName = "download";
                    }

                    var provider = new FileExtensionContentTypeProvider();
                    if (!provider.TryGetContentType(fileName, out var contentType))
                    {
                        contentType = "application/octet-stream";
                    }

                    Response.Headers["X-Content-Type-Options"] = "nosniff";
                    return File(stream, contentType, fileName);
                }
                catch (InvalidOperationException)
                {
                    stream?.Dispose();
                    // 如果是目录，则转为打包下载
                }
            }

            var streams = _resourceStore.OpenStreamsForZip(tab, paths);
            Response.ContentType = "application/zip";
            Response.Headers["Content-Disposition"] = "attachment; filename=\"share.zip\"";
            await using var responseStream = Response.BodyWriter.AsStream(leaveOpen: true);
            await _zipStreamService.WriteZipAsync(responseStream, streams, HttpContext.RequestAborted);
            foreach (var entry in streams)
            {
                entry.fileStream.Dispose();
            }
            return new EmptyResult();
        }
        catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException or FileNotFoundException or UnauthorizedAccessException)
        {
            stream?.Dispose();
            _logger.LogWarning(ex, "分享链接下载失败。Tab: {Tab}; Paths: {Paths}", tab, string.Join(',', paths));
            return BadRequest("链接无效或文件不存在。");
        }
    }

    private string BuildShareUrl(string token)
    {
        var relative = Url.Page("/Resources/Index", "ShareDownload", new { token });
        if (string.IsNullOrWhiteSpace(relative))
        {
            return string.Empty;
        }

        if (Request.Host.HasValue)
        {
            var builder = new UriBuilder(Request.Scheme, Request.Host.Host)
            {
                Port = _sharePortOverride ?? Request.Host.Port ?? -1
            };
            var final = new Uri(builder.Uri, relative);
            return final.ToString();
        }

        return Url.Page("/Resources/Index", "ShareDownload", new { token }, Request.Scheme) ?? string.Empty;
    }

    private string BuildPreviewFileUrl(ResourceTab tab, string path)
    {
        return Url.Page("/Resources/Index", "PreviewFile", new
        {
            tab = tab == ResourceTab.Temporary ? "temporary" : "permanent",
            path
        }) ?? string.Empty;
    }
}
