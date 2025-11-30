namespace S3FileManager.Core;

public sealed record UserContext(
    string UserId,
    IReadOnlyList<string> Roles,
    IReadOnlyDictionary<string, string> Claims
);

public sealed record FileItem(
    string Name,
    string Path,
    bool IsDirectory,
    long? Size,
    DateTimeOffset? LastModified
);

[Flags]
public enum PermissionFlags
{
    None    = 0,
    Read    = 1 << 0,
    Write   = 1 << 1,
    Delete  = 1 << 2,
    Upload  = 1 << 3
}

public sealed record EffectivePermissions(
    PermissionFlags Flags
)
{
    public bool CanRead   => Flags.HasFlag(PermissionFlags.Read);
    public bool CanWrite  => Flags.HasFlag(PermissionFlags.Write);
    public bool CanDelete => Flags.HasFlag(PermissionFlags.Delete);
    public bool CanUpload => Flags.HasFlag(PermissionFlags.Upload);
}

public sealed record AuditEvent(
    DateTimeOffset Timestamp,
    string UserId,
    string Action,
    string Path,
    string? Details = null
);

public interface IObjectStorageBackend
{
    Task<IReadOnlyList<FileItem>> ListAsync(string path, UserContext user, CancellationToken cancellationToken = default);
    Task UploadAsync(string path, Stream content, UserContext user, CancellationToken cancellationToken = default);
    Task DeleteAsync(string path, UserContext user, CancellationToken cancellationToken = default);
    Task MoveAsync(string fromPath, string toPath, UserContext user, CancellationToken cancellationToken = default);
    Task<Stream> OpenReadAsync(string path, UserContext user, CancellationToken cancellationToken = default);
}

public interface IAccessPolicyProvider
{
    Task<EffectivePermissions> GetPermissionsAsync(UserContext user, string path, CancellationToken cancellationToken = default);
}

public interface IAuditSink
{
    Task LogAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
}
