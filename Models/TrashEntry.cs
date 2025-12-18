using System;

namespace EW_Link.Models;

public record TrashEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string OriginalPath { get; init; }
    public required bool IsDirectory { get; init; }
    public required DateTimeOffset DeletedAt { get; init; }
    public long SizeBytes { get; init; }
}
