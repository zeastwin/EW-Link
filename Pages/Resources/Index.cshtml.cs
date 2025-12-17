using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EW_Link.Models;
using EW_Link.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;

namespace EW_Link.Pages.Resources;

public class IndexModel : PageModel
{
    private const long UploadLimitBytes = 1024L * 1024 * 1024;
    private readonly IResourceStore _resourceStore;
    private readonly ILogger<IndexModel> _logger;
    private readonly IZipStreamService _zipStreamService;

    public IndexModel(IResourceStore resourceStore, ILogger<IndexModel> logger, IZipStreamService zipStreamService)
    {
        _resourceStore = resourceStore ?? throw new ArgumentNullException(nameof(resourceStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _zipStreamService = zipStreamService ?? throw new ArgumentNullException(nameof(zipStreamService));
    }

    public ResourceTab SelectedTab { get; private set; }
    public string? CurrentPath { get; private set; }
    public string? Filter { get; private set; }
    public ResourceSortField SortField { get; private set; }
    public SortDirection SortDirection { get; private set; }

    public IReadOnlyList<ResourceEntry> Entries { get; private set; } = Array.Empty<ResourceEntry>();
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

    public IActionResult OnGet([FromQuery] string? tab, [FromQuery] string? path, [FromQuery(Name = "q")] string? filter, [FromQuery] string? sort, [FromQuery] string? dir)
    {
        if (!TryLoadPageData(tab, path, filter, sort, dir, out var errorResult))
        {
            return errorResult!;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostUpload([FromForm] string? tab, [FromForm] string? path, [FromForm(Name = "q")] string? filter, [FromForm] string? sort, [FromForm] string? dir, IFormFile? file)
    {
        if (!TryLoadPageData(tab, path, filter, sort, dir, out var errorResult))
        {
            return errorResult!;
        }

        if (file == null || file.Length == 0)
        {
            ModelState.AddModelError(string.Empty, "请选择要上传的文件。");
            return Page();
        }

        if (file.Length > UploadLimitBytes)
        {
            ModelState.AddModelError(string.Empty, "文件不能超过 1GB。");
            return Page();
        }

        try
        {
            await _resourceStore.SaveUpload(SelectedTab, CurrentPath, file, HttpContext.RequestAborted);
            TempData["SuccessMessage"] = $"上传成功：{file.FileName}";
            return RedirectToPage("/Resources/Index", new
            {
                tab = SelectedTabString,
                path = CurrentPath,
                q = Filter,
                sort = SortFieldParam,
                dir = SortDirectionParam
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "上传失败。Tab: {Tab}; Path: {Path}; FileName: {FileName}", SelectedTab, CurrentPath, file.FileName);
            ModelState.AddModelError(string.Empty, $"上传失败：{ex.Message}");
            return Page();
        }
    }

    public async Task<IActionResult> OnPostDownloadZip([FromForm] string? tab, [FromForm] string? path, [FromForm(Name = "q")] string? filter, [FromForm] string? sort, [FromForm] string? dir, [FromForm] string[]? paths)
    {
        if (!TryLoadPageData(tab, path, filter, sort, dir, out var errorResult))
        {
            return errorResult!;
        }

        if (paths == null || paths.Length == 0)
        {
            ModelState.AddModelError(string.Empty, "请选择要下载的文件。");
            return Page();
        }

        var streams = new List<(string entryName, Stream fileStream)>();
        try
        {
            foreach (var item in paths)
            {
                var entryPath = item?.Trim();
                if (string.IsNullOrWhiteSpace(entryPath))
                {
                    throw new InvalidOperationException("包含无效的路径。");
                }

                var stream = _resourceStore.OpenRead(SelectedTab, entryPath);
                streams.Add((entryPath, stream));
            }
        }
        catch (Exception ex)
        {
            foreach (var entry in streams)
            {
                entry.fileStream.Dispose();
            }

            _logger.LogError(ex, "批量下载校验失败。Tab: {Tab}; Path: {Path}", SelectedTab, CurrentPath);
            ModelState.AddModelError(string.Empty, $"批量下载失败：{ex.Message}");
            return Page();
        }

        Response.ContentType = "application/zip";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"resources.zip\"";

        try
        {
            await _zipStreamService.WriteZipAsync(Response.Body, streams, HttpContext.RequestAborted);
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

    public IActionResult OnPostDelete([FromForm] string? tab, [FromForm] string? path, [FromForm(Name = "q")] string? filter, [FromForm] string? sort, [FromForm] string? dir, [FromForm] string[]? paths, [FromForm] string? target)
    {
        if (!TryLoadPageData(tab, path, filter, sort, dir, out var errorResult))
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
            TempData["SuccessMessage"] = $"已删除 {targets.Count} 项。";
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
            dir = SortDirectionParam
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

    private ResourceTab ParseTab(string? tab)
    {
        return string.Equals(tab, "temporary", StringComparison.OrdinalIgnoreCase)
            ? ResourceTab.Temporary
            : ResourceTab.Permanent;
    }

    private bool TryLoadPageData(string? tab, string? path, string? filter, string? sort, string? dir, out IActionResult? errorResult)
    {
        errorResult = null;

        SelectedTab = ParseTab(tab);
        CurrentPath = path ?? string.Empty;
        Filter = string.IsNullOrWhiteSpace(filter) ? null : filter;
        SortField = ParseSortField(sort);
        SortDirection = ParseSortDirection(dir);

        try
        {
            Entries = _resourceStore.List(SelectedTab, CurrentPath, Filter, SortField, SortDirection);
            ComputeStats();
            Breadcrumbs = BuildBreadcrumbs(CurrentPath);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException or FileNotFoundException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Load page data failed. Tab: {Tab}; Path: {Path}", SelectedTab, CurrentPath);
            TempData["ErrorMessage"] = "路径无效或目录不存在，已回到根目录。";
            errorResult = RedirectToPage("/Resources/Index", new
            {
                tab = SelectedTabString,
                path = string.Empty,
                q = (string?)null,
                sort = SortFieldParam,
                dir = SortDirectionParam
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
}
