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

    public string GenerateToken(ResourceTab tab, IEnumerable<string> paths, DateTimeOffset expiresAt)
    {
        var list = paths?.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList()
                   ?? throw new ArgumentNullException(nameof(paths));
        if (list.Count == 0)
        {
            throw new ArgumentException("At least one path is required.", nameof(paths));
        }

        var payload = new SharePayload
        {
            Tab = tab == ResourceTab.Temporary ? "temporary" : "permanent",
            Paths = list,
            ExpiresAt = expiresAt
        };

        var json = JsonSerializer.Serialize(payload);
        var protectedBytes = _protector.Protect(Encoding.UTF8.GetBytes(json));
        return WebEncoders.Base64UrlEncode(protectedBytes);
    }

    public bool TryParseToken(string token, out ResourceTab tab, out List<string> paths, out DateTimeOffset expiresAt)
    {
        tab = ResourceTab.Permanent;
        paths = new List<string>();
        expiresAt = DateTimeOffset.MinValue;

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
            if (payload == null || payload.Paths == null || payload.Paths.Count == 0)
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
            paths = payload.Paths;
            expiresAt = payload.ExpiresAt;
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
        public List<string> Paths { get; set; } = new();
        public DateTimeOffset ExpiresAt { get; set; }
    }
}
