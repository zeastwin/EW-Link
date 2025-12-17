using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EW_Link.Models;
using Microsoft.AspNetCore.Http;

namespace EW_Link.Services;

public interface IResourceStore
{
    void EnsureRoots();

    IReadOnlyList<ResourceEntry> List(ResourceTab tab, string? relativePath, string? filter, ResourceSortField sortField, SortDirection sortDirection);

    void CreateDirectoryIfNotExists(string fullPath);

    Task<ResourceEntry> SaveUpload(ResourceTab tab, string? relativePath, IFormFile file, CancellationToken cancellationToken = default);

    FileStream OpenRead(ResourceTab tab, string relativePath);

    void Delete(ResourceTab tab, string relativePath);

    void DeleteMany(ResourceTab tab, IEnumerable<string> relativePaths);
}
