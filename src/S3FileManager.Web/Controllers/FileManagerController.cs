using Microsoft.AspNetCore.Mvc;
using S3FileManager.Core;

namespace S3FileManager.Web.Controllers;

[ApiController]
[Route("api/files")]
public class FileManagerController : ControllerBase
{
    private readonly IObjectStorageBackend _storage;
    private readonly IAccessPolicyProvider _accessPolicy;
    private readonly IAuditLogProvider _audit;

    public FileManagerController(
        IObjectStorageBackend storage,
        IAccessPolicyProvider accessPolicy,
        IAuditLogProvider audit)
    {
        _storage = storage;
        _accessPolicy = accessPolicy;
        _audit = audit;
    }

    private UserContext BuildUserContext()
    {
        // TODO: build from authenticated user / claims.
        // For now, use a dummy user.
        return new UserContext(
            UserId: User?.Identity?.Name ?? "anonymous",
            Roles: Array.Empty<string>(),
            Claims: new Dictionary<string, string>()
        );
    }

    [HttpGet("list")]
    public async Task<ActionResult<IReadOnlyList<FileItem>>> List([FromQuery] string? path, CancellationToken cancellationToken)
    {
        var user = BuildUserContext();
        var normalizedPath = path ?? "/";

        var perms = await _accessPolicy.GetPermissionsAsync(user, normalizedPath, cancellationToken);
        if (!perms.CanRead)
            return Forbid();

        var items = await _storage.ListAsync(normalizedPath, user, cancellationToken);

        await _audit.LogAsync(new AuditEvent(
            Timestamp: DateTimeOffset.UtcNow,
            UserId: user.UserId,
            Action: "List",
            Path: normalizedPath
        ), cancellationToken);

        return Ok(items);
    }

    // TODO: add upload, delete, move, download endpoints.
}
