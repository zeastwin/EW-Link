using System;
using System.IO;
using EW_Link.Options;

namespace EW_Link.Services;

public enum ResourceTab
{
    Permanent,
    Temporary
}

public class ResourcePathHelper
{
    private readonly ResourceOptions _options;
    private readonly string _rootWithSeparator;

    public ResourcePathHelper(ResourceOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        var root = string.IsNullOrWhiteSpace(options.Root) ? "/Data/resources" : options.Root;
        _rootWithSeparator = AppendDirectorySeparator(Path.GetFullPath(root));
    }

    public string ResolveSubRoot(ResourceTab tab)
    {
        var subDir = tab == ResourceTab.Permanent ? _options.PermanentSubDir : _options.TemporarySubDir;
        if (string.IsNullOrWhiteSpace(subDir))
        {
            throw new InvalidOperationException("Resource subdirectory name is not configured.");
        }

        var subRoot = Path.GetFullPath(Path.Combine(_rootWithSeparator, subDir));
        return AppendDirectorySeparator(subRoot);
    }

    public string ResolveSafeFullPath(string subRoot, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(subRoot))
        {
            throw new ArgumentException("Sub root is required.", nameof(subRoot));
        }

        var normalizedSubRoot = AppendDirectorySeparator(Path.GetFullPath(subRoot));
        var trimmedRelativePath = string.IsNullOrEmpty(relativePath) ? string.Empty : relativePath;

        if (trimmedRelativePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            throw new InvalidOperationException("Path contains invalid characters.");
        }

        if (trimmedRelativePath.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Path contains an invalid drive indicator.");
        }

        if (Path.IsPathRooted(trimmedRelativePath))
        {
            throw new InvalidOperationException("Absolute paths are not allowed.");
        }

        var segments = trimmedRelativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment == "..")
            {
                throw new InvalidOperationException("Path traversal is not allowed.");
            }
        }

        var combined = Path.GetFullPath(Path.Combine(normalizedSubRoot, trimmedRelativePath));
        if (!combined.StartsWith(normalizedSubRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Resolved path escapes the resource root.");
        }

        return combined;
    }

    private static string AppendDirectorySeparator(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return Path.DirectorySeparatorChar.ToString();
        }

        var lastChar = path[^1];
        if (lastChar != Path.DirectorySeparatorChar && lastChar != Path.AltDirectorySeparatorChar)
        {
            return path + Path.DirectorySeparatorChar;
        }

        return path;
    }
}
