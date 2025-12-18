using System;

namespace EW_Link.Services;

public interface IShareLinkService
{
    string GenerateToken(ResourceTab tab, IEnumerable<string> paths, DateTimeOffset expiresAt);

    bool TryParseToken(string token, out ResourceTab tab, out List<string> paths, out DateTimeOffset expiresAt);
}
