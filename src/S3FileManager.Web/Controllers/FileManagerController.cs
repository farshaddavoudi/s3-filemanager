using Microsoft.AspNetCore.Mvc;
using S3FileManager.Core;
using S3FileManager.Web.Configuration;
using Syncfusion.Blazor.FileManager;
using System.Text.Json;

namespace S3FileManager.Web.Controllers;

[ApiController]
[Route("api/files")]
public class FileManagerController : ControllerBase
{
    private readonly IObjectStorageBackend _storage;
    private readonly IAccessPolicyProvider _accessPolicy;
    private readonly IAuditLogProvider _audit;
    private readonly string _rootAliasName;

    public FileManagerController(
        IObjectStorageBackend storage,
        IAccessPolicyProvider accessPolicy,
        IAuditLogProvider audit,
        AppConfig appConfig)
    {
        _storage = storage;
        _accessPolicy = accessPolicy;
        _audit = audit;
        _rootAliasName = string.IsNullOrWhiteSpace(appConfig.FileManagerRootAlias)
            ? "File Storage"
            : appConfig.FileManagerRootAlias;
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

    [HttpPost("operations")]
    public async Task<IActionResult> Operations([FromBody] FileManagerRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var user = BuildUserContext();
            var path = NormalizePath(request.Path);
            var perms = await _accessPolicy.GetPermissionsAsync(user, path, cancellationToken);

            switch (request.Action?.ToLowerInvariant())
            {
                case "read":
                    if (!perms.CanRead) return Forbid();
                    var items = await _storage.ListAsync(path, user, cancellationToken);
                    await _audit.LogAsync(new AuditEvent(DateTimeOffset.UtcNow, user.UserId, "List", path), cancellationToken);
                    var cwd = MapToCwd(path, items, _rootAliasName);
                    return Ok(new { cwd, files = items.Select(MapToFileManagerItem).ToList() });

                case "create":
                    if (!perms.CanWrite && !perms.CanUpload) return Forbid();
                    var newFolderName = request.NewName ?? "New Folder";
                    var folderPath = EnsureFolder(Combine(path, newFolderName));
                    await _storage.UploadAsync(folderPath, Stream.Null, user, cancellationToken);
                    await _audit.LogAsync(new AuditEvent(DateTimeOffset.UtcNow, user.UserId, "CreateFolder", folderPath), cancellationToken);
                    return Ok(new { result = "success" });

                case "delete":
                    if (!perms.CanDelete) return Forbid();
                    foreach (var name in request.Names ?? Enumerable.Empty<string>())
                    {
                        var deletePath = Combine(path, name);
                        await _storage.DeleteAsync(deletePath, user, cancellationToken);
                        await _audit.LogAsync(new AuditEvent(DateTimeOffset.UtcNow, user.UserId, "Delete", deletePath), cancellationToken);
                    }
                    return Ok(new { result = "success" });

                case "rename":
                    if (!perms.CanWrite) return Forbid();
                    var source = Combine(path, request.Names?.FirstOrDefault() ?? string.Empty);
                    var destination = Combine(GetDirectoryPath(source), request.NewName ?? string.Empty);
                    await _storage.MoveAsync(source, destination, user, cancellationToken);
                    await _audit.LogAsync(new AuditEvent(DateTimeOffset.UtcNow, user.UserId, "Rename", $"{source} -> {destination}"), cancellationToken);
                    return Ok(new { result = "success" });

                case "move":
                    if (!perms.CanWrite) return Forbid();
                    foreach (var name in request.Names ?? Enumerable.Empty<string>())
                    {
                        var sourcePath = Combine(path, name);
                        var destPath = Combine(NormalizePath(request.TargetPath), name);
                        await _storage.MoveAsync(sourcePath, destPath, user, cancellationToken);
                        await _audit.LogAsync(new AuditEvent(DateTimeOffset.UtcNow, user.UserId, "Move", $"{sourcePath} -> {destPath}"), cancellationToken);
                    }
                    return Ok(new { result = "success" });

                default:
                    return BadRequest(new { error = "Unsupported action" });
            }
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message, details = ex.ToString() });
        }
    }

    [HttpPost("upload")]
    [RequestSizeLimit(512_000_000)] // 512 MB default cap; adjust as needed
    public async Task<IActionResult> Upload([FromForm] string? path, CancellationToken cancellationToken)
    {
        var user = BuildUserContext();
        var normalizedPath = NormalizePath(path);
        var perms = await _accessPolicy.GetPermissionsAsync(user, normalizedPath, cancellationToken);
        if (!perms.CanUpload) return Forbid();

        foreach (var formFile in Request.Form.Files)
        {
            var targetPath = Combine(normalizedPath, formFile.FileName);
            await using var stream = formFile.OpenReadStream();
            await _storage.UploadAsync(targetPath, stream, user, cancellationToken);
            await _audit.LogAsync(new AuditEvent(DateTimeOffset.UtcNow, user.UserId, "Upload", targetPath), cancellationToken);
        }

        return Ok(new { result = "success" });
    }

    [HttpPost("download")]
    public async Task<IActionResult> Download([FromForm] string? path, [FromForm] string? downloadInput, CancellationToken cancellationToken)
    {
        var user = BuildUserContext();
        var requestedPath = ResolveDownloadPath(path, downloadInput);
        var normalizedPath = NormalizePath(requestedPath);

        var perms = await _accessPolicy.GetPermissionsAsync(user, normalizedPath, cancellationToken);
        if (!perms.CanRead) return Forbid();

        var stream = await _storage.OpenReadAsync(normalizedPath, user, cancellationToken);
        await _audit.LogAsync(new AuditEvent(DateTimeOffset.UtcNow, user.UserId, "Download", normalizedPath), cancellationToken);

        var fileName = Path.GetFileName(normalizedPath.TrimEnd('/'));
        return File(stream, "application/octet-stream", fileName);
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        var cleaned = path.Replace("\\", "/");
        if (!cleaned.StartsWith("/")) cleaned = "/" + cleaned;
        return cleaned;
    }

    private static string Combine(string basePath, string name)
    {
        var trimmedBase = basePath.TrimEnd('/');
        var trimmedName = name.TrimStart('/');
        return $"{trimmedBase}/{trimmedName}";
    }

    private static string EnsureFolder(string path)
    {
        return path.EndsWith("/") ? path : path + "/";
    }

    private static string GetDirectoryPath(string path)
    {
        var normalized = NormalizePath(path);
        if (normalized == "/") return "/";
        var trimmed = normalized.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        if (lastSlash <= 0) return "/";
        var result = trimmed[..lastSlash];
        return string.IsNullOrEmpty(result) ? "/" : result;
    }

    private static FileManagerDirectoryContent MapToFileManagerItem(FileItem item)
    {
        // Ensure path is never null or empty
        var itemPath = string.IsNullOrWhiteSpace(item.Path) ? "/" : item.Path;
        var itemName = !string.IsNullOrWhiteSpace(item.Name) ? item.Name : "Unknown";

        // Calculate filterPath - for root level items, use "/"
        var filterPath = GetDirectoryPath(itemPath);
        var id = itemPath;

        return new FileManagerDirectoryContent
        {
            Name = itemName,
            IsFile = !item.IsDirectory,
            Size = item.Size ?? 0,
            DateModified = (item.LastModified ?? DateTimeOffset.UtcNow).UtcDateTime,
            DateCreated = (item.LastModified ?? DateTimeOffset.UtcNow).UtcDateTime,
            HasChild = item.IsDirectory,
            Type = item.IsDirectory ? "Directory" : (!string.IsNullOrWhiteSpace(item.Name) ? Path.GetExtension(item.Name) : ""),
            FilterPath = filterPath,
            Path = itemPath,
            Id = id,
            ParentId = filterPath
        };
    }

    private static FileManagerDirectoryContent MapToCwd(string path, IReadOnlyList<FileItem> items, string rootAliasName)
    {
        var normalized = NormalizePath(path);
        var name = GetNameFromPath(normalized);
        var hasChild = items.Any(i => i.IsDirectory);

        // Get the parent path for filterPath
        var filterPath = GetDirectoryPath(normalized);
        var id = normalized;

        return new FileManagerDirectoryContent
        {
            Name = string.IsNullOrWhiteSpace(name) || name == "/" ? rootAliasName : name,
            Size = 0,
            DateModified = DateTime.UtcNow,
            DateCreated = DateTime.UtcNow,
            HasChild = hasChild,
            IsFile = false,
            Type = "Folder",
            FilterPath = filterPath == "/" ? string.Empty : filterPath,
            Path = normalized,
            Id = id,
            ParentId = string.IsNullOrEmpty(filterPath) ? null : GetDirectoryPath(filterPath)
        };
    }

    private static string GetNameFromPath(string path)
    {
        var normalized = NormalizePath(path).TrimEnd('/');
        if (normalized == "/") return "/";
        var last = normalized.LastIndexOf('/');
        return last >= 0 ? normalized[(last + 1)..] : normalized;
    }

    private static string? ResolveDownloadPath(string? path, string? downloadInput)
    {
        if (!string.IsNullOrWhiteSpace(path)) return path;
        if (!string.IsNullOrWhiteSpace(downloadInput))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<DownloadInput>(downloadInput);
                var selected = parsed?.Items?.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(selected?.Name))
                {
                    return Combine(NormalizePath(selected.Path ?? "/"), selected.Name);
                }
            }
            catch
            {
                // ignore parse errors and fall through
            }
        }
        return "/";
    }

    public sealed class FileManagerRequest
    {
        public string? Action { get; set; }
        public string? Path { get; set; }
        public string? TargetPath { get; set; }
        public string? NewName { get; set; }
        public List<string>? Names { get; set; }
    }

    private sealed class DownloadInput
    {
        public List<DownloadItem>? Items { get; set; }
    }

    private sealed class DownloadItem
    {
        public string? Name { get; set; }
        public string? Path { get; set; }
    }
}
