using System;

namespace EW_Link.Models;

public class ResourceEntry
{
    public string Name { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public bool IsDirectory { get; set; }

    public long SizeBytes { get; set; }

    public DateTimeOffset LastWriteTime { get; set; }
}
