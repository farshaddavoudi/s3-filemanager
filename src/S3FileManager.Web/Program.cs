using DotNetEnv;
using Minio;
using S3FileManager.Core;
using S3FileManager.Storage.Minio;
using S3FileManager.Web.Configuration;
using Syncfusion.Blazor;
using Syncfusion.Licensing;

var builder = WebApplication.CreateBuilder(args);

// Load .env into environment variables (walks up from current directory).
Env.TraversePath().Load();

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddSyncfusionBlazor();

// Bind environment config
var appConfig = AppConfig.FromEnvironment();
builder.Services.AddSingleton(appConfig);

// IMPORTANT: Syncfusion Blazor components require a valid Syncfusion license.
// Register your own license key at startup; do NOT commit real keys.
if (!string.IsNullOrWhiteSpace(appConfig.SyncfusionLicenseKey))
{
    SyncfusionLicenseProvider.RegisterLicense(appConfig.SyncfusionLicenseKey);
}

// Core abstractions
builder.Services.AddSingleton<IAccessPolicyProvider, SimpleAllowAllAccessPolicyProvider>();
builder.Services.AddSingleton<IAuditLogProvider, DefaultConsoleAuditLogProvider>();

// MinIO backend wiring
builder.Services.AddSingleton<IMinioClient>(_ =>
{
    var client = new MinioClient()
        .WithCredentials(appConfig.MinioAccessKey, appConfig.MinioSecretKey);

    if (Uri.TryCreate(appConfig.MinioEndpoint, UriKind.Absolute, out var uri))
    {
        client = uri.IsDefaultPort
            ? client.WithEndpoint(uri.Host)
            : client.WithEndpoint(uri.Host, uri.Port);

        if (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            client = client.WithSSL();
    }
    else
    {
        client = client.WithEndpoint(appConfig.MinioEndpoint);
    }

    return client.Build();
});

builder.Services.AddSingleton<IObjectStorageBackend>(sp =>
{
    var client = sp.GetRequiredService<IMinioClient>();
    return new MinioStorageBackend(client, appConfig.MinioBucket);
});

var app = builder.Build();

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

app.MapControllers();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();

/// <summary>
/// Temporary simple access policy: allow everything.
/// Replace with a real implementation later.
/// </summary>
public sealed class SimpleAllowAllAccessPolicyProvider : IAccessPolicyProvider
{
    public Task<EffectivePermissions> GetPermissionsAsync(UserContext user, string path, CancellationToken cancellationToken = default)
        => Task.FromResult(new EffectivePermissions(
            PermissionFlags.Read | PermissionFlags.Write | PermissionFlags.Delete | PermissionFlags.Upload
        ));
}
