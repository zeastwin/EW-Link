using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EW_Link.Services;

public interface IZipStreamService
{
    Task WriteZipAsync(Stream responseBody, IEnumerable<(string entryName, Stream fileStream)> items, CancellationToken cancellationToken = default);
}
