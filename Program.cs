using EW_Link.Options;
using EW_Link.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

const long uploadLimitBytes = 10L * 1024 * 1024 * 1024;
var requestHeadersTimeout = TimeSpan.FromMinutes(2);

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = uploadLimitBytes;
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = uploadLimitBytes;
    options.Limits.RequestHeadersTimeout = requestHeadersTimeout;
});

builder.Services.Configure<ResourceOptions>(builder.Configuration.GetSection("Resources"));
builder.Services.PostConfigure<ResourceOptions>(options =>
{
    var envRoot = builder.Configuration["RESOURCES_ROOT"];
    if (!string.IsNullOrWhiteSpace(envRoot))
    {
        options.Root = envRoot;
    }
});

builder.Services.AddDataProtection()
    .SetApplicationName("ew-link")
    .PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["DATA_PROTECTION_KEYS_ROOT"] ?? "/var/aspnet-keys"));

builder.Services.AddSingleton<IResourceStore, ResourceStore>();
builder.Services.AddSingleton<IZipStreamService, ZipStreamService>();
builder.Services.AddSingleton<IShareLinkService, ShareLinkService>();
builder.Services.AddHostedService<TemporaryCleanupService>();
builder.Services.AddHostedService<TrashCleanupService>();

builder.Services.AddRazorPages();

var app = builder.Build();

var formOptions = app.Services.GetRequiredService<IOptions<FormOptions>>().Value;
var resourceOptions = app.Services.GetRequiredService<IOptions<ResourceOptions>>().Value;
var resourceStore = app.Services.GetRequiredService<IResourceStore>();
resourceStore.EnsureRoots();

app.Logger.LogInformation(
    "Resource root: {ResourceRoot}; Upload limits - Form: {FormLimitBytes} bytes, Kestrel: {KestrelLimitBytes} bytes, HeadersTimeout: {HeadersTimeout}",
    resourceOptions.Root,
    formOptions.MultipartBodyLengthLimit,
    uploadLimitBytes,
    requestHeadersTimeout);

app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();

app.Run();
