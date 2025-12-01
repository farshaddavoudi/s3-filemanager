namespace S3FileManager.Web.Configuration;

public sealed class AppConfig
{
    public string? SyncfusionLicenseKey { get; init; }
    public string MinioEndpoint { get; init; } = string.Empty;
    public string MinioAccessKey { get; init; } = string.Empty;
    public string MinioSecretKey { get; init; } = string.Empty;
    public string MinioBucket { get; init; } = string.Empty;

    public static AppConfig FromEnvironment()
    {
        var cfg = new AppConfig
        {
            SyncfusionLicenseKey = GetEnv("SYNCFUSION_LICENSEKEY"),
            MinioEndpoint = GetEnv("MINIO_ENDPOINT"),
            MinioAccessKey = GetEnv("MINIO_ACCESSKEY"),
            MinioSecretKey = GetEnv("MINIO_SECRETKEY"),
            MinioBucket = GetEnv("MINIO_BUCKET"),
        };

        cfg.Validate();

        return cfg;
    }

    private static string GetEnv(string key) => Environment.GetEnvironmentVariable(key) ?? string.Empty;

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(MinioEndpoint))
            throw new InvalidOperationException("MINIO_ENDPOINT is required.");
        if (string.IsNullOrWhiteSpace(MinioAccessKey))
            throw new InvalidOperationException("MINIO_ACCESSKEY is required.");
        if (string.IsNullOrWhiteSpace(MinioSecretKey))
            throw new InvalidOperationException("MINIO_SECRETKEY is required.");
        if (string.IsNullOrWhiteSpace(MinioBucket))
            throw new InvalidOperationException("MINIO_BUCKET is required.");
    }
}
