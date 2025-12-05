using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
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
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

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
        var user = BuildUserContext();
        var action = request.Action?.ToLowerInvariant();
        var currentPath = NormalizePath(request.Path);

        try
        {
            return action switch
            {
                "read"    => await HandleReadAsync(currentPath, user, cancellationToken),
                "details" => await HandleDetailsAsync(currentPath, request, user, cancellationToken),
                "create"  => await HandleCreateAsync(currentPath, request, user, cancellationToken),
                "delete"  => await HandleDeleteAsync(currentPath, request, user, cancellationToken),
                "rename"  => await HandleRenameAsync(currentPath, request, user, cancellationToken),
                "move"    => await HandlePasteAsync(currentPath, request, user, cancellationToken),
                "paste"   => await HandlePasteAsync(currentPath, request, user, cancellationToken),
                _ => BadRequest(new { error = "Unsupported action" })
            };
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Internal server error", message = ex.Message });
        }
    }

    private async Task<IActionResult> HandleReadAsync(string path, UserContext user, CancellationToken cancellationToken)
    {
        var perms = await _accessPolicy.GetPermissionsAsync(user, path, cancellationToken);
        if (!perms.CanRead) return Forbid();

        var items = await _storage.ListAsync(path, user, cancellationToken);
        await _audit.LogAsync(new AuditEvent(DateTimeOffset.UtcNow, user.UserId, "List", path), cancellationToken);

        return BuildListingResult(path, items);
    }

    private async Task<IActionResult> HandleDetailsAsync(string path, FileManagerRequest request, UserContext user, CancellationToken cancellationToken)
    {
        var perms = await _accessPolicy.GetPermissionsAsync(user, path, cancellationToken);
        if (!perms.CanRead) return Forbid();

        var items = await _storage.ListAsync(path, user, cancellationToken);
        var requestedNames = (request.Names ?? new List<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedItems = requestedNames.Count == 0
            ? items
            : items.Where(i => requestedNames.Contains(i.Name)).ToList();

        var cwd = MapToCwd(path, items, _rootAliasName);
        var details = selectedItems.Select(MapToFileManagerItem).ToList();
        return Ok(new { cwd, details });
    }

    private async Task<IActionResult> HandleCreateAsync(string path, FileManagerRequest request, UserContext user, CancellationToken cancellationToken)
    {
        var perms = await _accessPolicy.GetPermissionsAsync(user, path, cancellationToken);
        if (!perms.CanWrite && !perms.CanUpload) return Forbid();

        var newFolderName = FirstNonEmpty(request.NewName, request.Name, "New Folder");
        var folderPath = EnsureFolder(Combine(path, newFolderName));

        await _storage.UploadAsync(folderPath, Stream.Null, user, cancellationToken);
        await _audit.LogAsync(new AuditEvent(DateTimeOffset.UtcNow, user.UserId, "CreateFolder", folderPath), cancellationToken);

        return await BuildListingResultAsync(path, user, cancellationToken);
    }

    private async Task<IActionResult> HandleDeleteAsync(string path, FileManagerRequest request, UserContext user, CancellationToken cancellationToken)
    {
        var names = request.Names ?? new List<string>();
        if (names.Count == 0) return BadRequest(new { error = "No items to delete" });

        Console.WriteLine($"[HandleDeleteAsync] Current path: '{path}', Items to delete: {string.Join(", ", names.Select(n => $"'{n}'"))}");

        var items = await _storage.ListAsync(path, user, cancellationToken);
        Console.WriteLine($"[HandleDeleteAsync] Found {items.Count} items in current path");
        
        var lookup = items.ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            // Syncfusion is expected to send names relative to the current path,
            // but some browsers/flows send full paths (including trailing slash for folders).
            var isAbsolute = name.StartsWith("/", StringComparison.Ordinal);

            // Try to detect directory flag either from the current listing or from the incoming name shape.
            var baseName = GetNameFromPath(name);
            var isDirectory = (lookup.TryGetValue(baseName, out var item) && item.IsDirectory)
                              || name.EndsWith("/", StringComparison.Ordinal);

            var deletePath = isAbsolute
                ? CanonicalPath(name)
                : Combine(path, name);

            if (isDirectory)
                deletePath = EnsureFolder(deletePath);

            Console.WriteLine($"[HandleDeleteAsync] Item '{name}': isDirectory={isDirectory}, resolved deletePath='{deletePath}'");

            var perms = await _accessPolicy.GetPermissionsAsync(user, deletePath, cancellationToken);
            if (!perms.CanDelete) return Forbid();

            await _storage.DeleteAsync(deletePath, user, cancellationToken);
            await _audit.LogAsync(new AuditEvent(DateTimeOffset.UtcNow, user.UserId, "Delete", deletePath), cancellationToken);
        }

        return await BuildListingResultAsync(path, user, cancellationToken);
    }

    private async Task<IActionResult> HandleRenameAsync(string path, FileManagerRequest request, UserContext user, CancellationToken cancellationToken)
    {
        var name = request.Names?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(name)) return BadRequest(new { error = "Missing name for rename" });
        var newName = FirstNonEmpty(request.NewName, request.Name, request.TargetPath);
        if (string.IsNullOrWhiteSpace(newName)) return BadRequest(new { error = "Missing new name" });

        var items = await _storage.ListAsync(path, user, cancellationToken);
        var isDirectory = items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase))?.IsDirectory == true;

        var source = Combine(path, name);
        if (isDirectory) source = EnsureFolder(source);
        var destination = Combine(GetDirectoryPath(source), newName!);
        if (isDirectory) destination = EnsureFolder(destination);

        var sourcePerms = await _accessPolicy.GetPermissionsAsync(user, source, cancellationToken);
        var destPerms = await _accessPolicy.GetPermissionsAsync(user, destination, cancellationToken);
        if (!sourcePerms.CanWrite || !destPerms.CanWrite || !sourcePerms.CanDelete) return Forbid();

        await _storage.MoveAsync(source, destination, user, cancellationToken);
        await _audit.LogAsync(new AuditEvent(DateTimeOffset.UtcNow, user.UserId, "Rename", $"{source} -> {destination}"), cancellationToken);

        var refreshPath = GetDirectoryPath(destination);
        return await BuildListingResultAsync(refreshPath, user, cancellationToken);
    }

    private async Task<IActionResult> HandlePasteAsync(string path, FileManagerRequest request, UserContext user, CancellationToken cancellationToken)
    {
        var names = request.Names ?? new List<string>();
        if (names.Count == 0) return BadRequest(new { error = "No items to move" });

        var targetPath = NormalizePath(string.IsNullOrWhiteSpace(request.TargetPath) ? path : request.TargetPath!);
        var items = await _storage.ListAsync(path, user, cancellationToken);
        var lookup = items.ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            var sourcePath = Combine(path, name);
            var destPath = Combine(targetPath, name);
            var isDirectory = lookup.TryGetValue(name, out var item) && item.IsDirectory;
            if (isDirectory)
            {
                sourcePath = EnsureFolder(sourcePath);
                destPath = EnsureFolder(destPath);
            }

            var sourcePerms = await _accessPolicy.GetPermissionsAsync(user, sourcePath, cancellationToken);
            var destPerms = await _accessPolicy.GetPermissionsAsync(user, destPath, cancellationToken);
            if (!sourcePerms.CanDelete || !(destPerms.CanWrite || destPerms.CanUpload)) return Forbid();

            await _storage.MoveAsync(sourcePath, destPath, user, cancellationToken);
            await _audit.LogAsync(new AuditEvent(DateTimeOffset.UtcNow, user.UserId, "Move", $"{sourcePath} -> {destPath}"), cancellationToken);
        }

        return await BuildListingResultAsync(targetPath, user, cancellationToken);
    }

    private IActionResult BuildListingResult(string path, IReadOnlyList<FileItem> items)
    {
        var normalizedPath = NormalizePath(path);
        var cwd = MapToCwd(normalizedPath, items, _rootAliasName);
        var currentKey = CanonicalKey(normalizedPath);
        var files = items
            .Select(MapToFileManagerItem)
            .Where(f => !CanonicalKey(f.Path).Equals(currentKey, StringComparison.OrdinalIgnoreCase)) // drop self placeholder
            .OrderBy(f => f.IsFile) // prefer directory entry when duplicate keys exist
            .GroupBy(f => CanonicalKey(f.Path), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        return Ok(new { cwd, files });
    }

    private async Task<IActionResult> BuildListingResultAsync(string path, UserContext user, CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizePath(path);
        var items = await _storage.ListAsync(normalizedPath, user, cancellationToken);
        return BuildListingResult(normalizedPath, items);
    }

    [HttpPost("upload")]
    [RequestSizeLimit(512_000_000)] // 512 MB default cap; adjust as needed
    public async Task<IActionResult> Upload([FromForm] string? path, CancellationToken cancellationToken)
    {
        var user = BuildUserContext();
        var normalizedPath = NormalizePath(path);
        var perms = await _accessPolicy.GetPermissionsAsync(user, normalizedPath, cancellationToken);
        if (!perms.CanUpload && !perms.CanWrite) return Forbid();

        foreach (var formFile in Request.Form.Files)
        {
            var targetPath = Combine(normalizedPath, formFile.FileName);
            await using var stream = formFile.OpenReadStream();
            await _storage.UploadAsync(targetPath, stream, user, cancellationToken);
            await _audit.LogAsync(new AuditEvent(DateTimeOffset.UtcNow, user.UserId, "Upload", targetPath), cancellationToken);
        }

        return await BuildListingResultAsync(normalizedPath, user, cancellationToken);
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
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "download";
        }

        if (!ContentTypeProvider.TryGetContentType(fileName, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        return File(stream, contentType, fileName);
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        return CanonicalPath(path);
    }

    private static string Combine(string basePath, string name)
    {
        var trimmedBase = basePath.TrimEnd('/');
        var trimmedName = name.TrimStart('/');
        return $"{trimmedBase}/{trimmedName}";
    }

    private static string EnsureFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        if (path == "/") return "/";
        return path.EndsWith("/") ? path : path + "/";
    }

    private static string GetDirectoryPath(string path)
    {
        var normalized = CanonicalPath(path);
        if (normalized == "/") return "/";
        var lastSlash = normalized.LastIndexOf('/');
        if (lastSlash <= 0) return "/";
        var result = normalized[..lastSlash];
        return string.IsNullOrEmpty(result) ? "/" : result;
    }

    private static FileManagerDirectoryContent MapToFileManagerItem(FileItem item)
    {
        // Ensure path is never null or empty
        var rawPath = string.IsNullOrWhiteSpace(item.Path) ? "/" : item.Path;
        var canonical = CanonicalPath(rawPath);
        var pathForUi = item.IsDirectory && canonical != "/"
            ? EnsureFolder(canonical)
            : canonical;

        var itemName = !string.IsNullOrWhiteSpace(item.Name) ? Decode(item.Name) : "Unknown";

        // Calculate filterPath - for root level items, use "/"
        var parentCanonical = GetDirectoryPath(canonical);
        var parentId = parentCanonical == "/" ? "/" : EnsureFolder(parentCanonical);

        return new FileManagerDirectoryContent
        {
            Name = itemName,
            IsFile = !item.IsDirectory,
            Size = item.Size ?? 0,
            DateModified = (item.LastModified ?? DateTimeOffset.UtcNow).UtcDateTime,
            DateCreated = (item.LastModified ?? DateTimeOffset.UtcNow).UtcDateTime,
            HasChild = item.IsDirectory,
            Type = item.IsDirectory ? "Directory" : (!string.IsNullOrWhiteSpace(item.Name) ? Path.GetExtension(item.Name) : ""),
            FilterPath = parentId,
            Path = pathForUi,
            Id = pathForUi,
            ParentId = parentId
        };
    }

    private static FileManagerDirectoryContent MapToCwd(string path, IReadOnlyList<FileItem> items, string rootAliasName)
    {
        var canonical = CanonicalPath(path);
        var pathForUi = canonical == "/" ? "/" : EnsureFolder(canonical);
        var name = Decode(GetNameFromPath(canonical));
        var hasChild = items.Any(i => i.IsDirectory);

        // Get the parent path for filterPath
        var filterPathCanonical = GetDirectoryPath(canonical);
        var filterPathUi = filterPathCanonical == "/" ? string.Empty : EnsureFolder(filterPathCanonical);
        var id = pathForUi;

        return new FileManagerDirectoryContent
        {
            Name = string.IsNullOrWhiteSpace(name) || name == "/" ? rootAliasName : name,
            Size = 0,
            DateModified = DateTime.UtcNow,
            DateCreated = DateTime.UtcNow,
            HasChild = hasChild,
            IsFile = false,
            Type = "Folder",
            FilterPath = filterPathUi,
            Path = pathForUi,
            Id = id,
            ParentId = string.IsNullOrEmpty(filterPathUi) ? null : GetDirectoryPath(filterPathUi)
        };
    }

    private static string GetNameFromPath(string path)
    {
        var normalized = NormalizePath(path).TrimEnd('/');
        if (normalized == "/") return "/";
        var last = normalized.LastIndexOf('/');
        return last >= 0 ? normalized[(last + 1)..] : normalized;
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace("+", " "));

    private static string CanonicalPath(string path)
    {
        var decoded = Decode(path);
        var cleaned = decoded.Replace("\\", "/");
        if (!cleaned.StartsWith("/")) cleaned = "/" + cleaned;
        while (cleaned.Contains("//", StringComparison.Ordinal))
            cleaned = cleaned.Replace("//", "/", StringComparison.Ordinal);
        cleaned = cleaned.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "/";
        return cleaned;
    }

    private static string CanonicalKey(string path) => CanonicalPath(path);

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return string.Empty;
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
        public string? Name { get; set; }
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
