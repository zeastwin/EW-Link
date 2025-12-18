using System;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace EW_Link.Services;

public class ShareLinkService : IShareLinkService
{
    private const string Purpose = "share-link";
    private readonly IDataProtector _protector;

    public ShareLinkService(IDataProtectionProvider provider)
    {
        if (provider == null) throw new ArgumentNullException(nameof(provider));
        _protector = provider.CreateProtector(Purpose);
    }

    public string GenerateToken(ResourceTab tab, string path, DateTimeOffset expiresAt)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path is required.", nameof(path));
        }

        var payload = new SharePayload
        {
            Tab = tab == ResourceTab.Temporary ? "temporary" : "permanent",
            Path = path,
            ExpiresAt = expiresAt
        };

        var json = JsonSerializer.Serialize(payload);
        var protectedBytes = _protector.Protect(Encoding.UTF8.GetBytes(json));
        return WebEncoders.Base64UrlEncode(protectedBytes);
    }

    public bool TryParseToken(string token, out ResourceTab tab, out string path)
    {
        tab = ResourceTab.Permanent;
        path = string.Empty;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            var bytes = WebEncoders.Base64UrlDecode(token);
            var unprotected = _protector.Unprotect(bytes);
            var json = Encoding.UTF8.GetString(unprotected);
            var payload = JsonSerializer.Deserialize<SharePayload>(json);
            if (payload == null || string.IsNullOrWhiteSpace(payload.Path))
            {
                return false;
            }

            if (payload.ExpiresAt < DateTimeOffset.UtcNow)
            {
                return false;
            }

            tab = string.Equals(payload.Tab, "temporary", StringComparison.OrdinalIgnoreCase)
                ? ResourceTab.Temporary
                : ResourceTab.Permanent;
            path = payload.Path;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private class SharePayload
    {
        public string Tab { get; set; } = default!;
        public string Path { get; set; } = default!;
        public DateTimeOffset ExpiresAt { get; set; }
    }
}
