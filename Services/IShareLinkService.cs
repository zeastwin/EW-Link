using System;

namespace EW_Link.Services;

public interface IShareLinkService
{
    string GenerateToken(ResourceTab tab, string path, DateTimeOffset expiresAt);

    bool TryParseToken(string token, out ResourceTab tab, out string path);
}
