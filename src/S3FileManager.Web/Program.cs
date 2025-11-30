extern alias sfbase;

using S3FileManager.Core;
using S3FileManager.Storage.Minio;
using Minio;
using Syncfusion.Licensing;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
sfbase::Syncfusion.Blazor.SyncfusionBlazor.AddSyncfusionBlazor(builder.Services);
// IMPORTANT: Syncfusion Blazor components require a valid Syncfusion license.
// Register your own license key at startup; do NOT commit real keys.
var syncfusionLicenseKey = builder.Configuration["Syncfusion:LicenseKey"];
if (!string.IsNullOrWhiteSpace(syncfusionLicenseKey))
{
    SyncfusionLicenseProvider.RegisterLicense(syncfusionLicenseKey);
}

// Core abstractions
builder.Services.AddSingleton<IAccessPolicyProvider, SimpleAllowAllAccessPolicyProvider>();
builder.Services.AddSingleton<IAuditLogProvider, DefaultConsoleAuditLogProvider>();

// MinIO backend wiring
builder.Services.AddSingleton<IMinioClient>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var endpoint  = cfg["MINIO__ENDPOINT"] ?? "http://localhost:9000";
    var accessKey = cfg["MINIO__ACCESSKEY"] ?? "minioadmin";
    var secretKey = cfg["MINIO__SECRETKEY"] ?? "minioadmin";

    return new MinioClient()
        .WithEndpoint(endpoint)
        .WithCredentials(accessKey, secretKey)
        .Build();
});

builder.Services.AddSingleton<IObjectStorageBackend>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var bucket = cfg["MINIO__BUCKET"] ?? "ftp";
    var client = sp.GetRequiredService<IMinioClient>();
    return new MinioStorageBackend(client, bucket);
});

var app = builder.Build();

app.UseRouting();
app.UseAuthorization();

app.MapControllers();

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
