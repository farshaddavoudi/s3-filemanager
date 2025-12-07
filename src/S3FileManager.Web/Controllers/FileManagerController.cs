using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using S3FileManager.Core;
using S3FileManager.Web.Configuration;
using Syncfusion.Blazor.FileManager;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace S3FileManager.Web.Controllers;

[ApiController]
[Route("api/files")]
public class FileManagerController : ControllerBase
{
    private readonly IObjectStorageBackend _storage;
    private readonly IAccessPolicyProvider _accessPolicy;
    private readonly IAuditLogProvider _audit;
    private readonly ILogger<FileManagerController> _logger;
    private readonly string _rootAliasName;
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

    public FileManagerController(
        IObjectStorageBackend storage,
        IAccessPolicyProvider accessPolicy,
        IAuditLogProvider audit,
        AppConfig appConfig,
        ILogger<FileManagerController> logger)
    {
        _storage = storage;
        _accessPolicy = accessPolicy;
        _audit = audit;
        _logger = logger;
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

        Console.WriteLine($"[Operations] action='{action}', path='{currentPath}', request={request}");

        try
        {
            return action switch
            {
                "read" => await HandleReadAsync(currentPath, user, cancellationToken),
                "details" => await HandleDetailsAsync(currentPath, request, user, cancellationToken),
                "create" => await HandleCreateAsync(currentPath, request, user, cancellationToken),
                "delete" => await HandleDeleteAsync(currentPath, request, user, cancellationToken),
                "rename" => await HandleRenameAsync(currentPath, request, user, cancellationToken),
                "move" => await HandlePasteAsync(currentPath, request, user, cancellationToken),
                "paste" => await HandlePasteAsync(currentPath, request, user, cancellationToken),
                _ => BadRequest(new { error = "Unsupported action" })
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Operations] action='{action}', path='{currentPath}' failed: {ex}");
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
            var isDirectory = (lookup.TryGetValue(GetNameFromPath(name), out var item) && item.IsDirectory)
                              || name.EndsWith("/", StringComparison.Ordinal);

            var deletePath = ResolveDeletePath(path, name, isDirectory);

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
        // Syncfusion posts the current item name in `name`; older payloads may still use `names`.
        var name = FirstNonEmpty(request.Names?.FirstOrDefault(), request.Name);
        if (string.IsNullOrWhiteSpace(name)) return BadRequest(new { error = "Missing name for rename" });

        var newName = FirstNonEmpty(request.NewName, request.TargetPath);
        if (string.IsNullOrWhiteSpace(newName)) return BadRequest(new { error = "Missing new name" });

        var effectivePath = DeduplicateTrailingSegment(path);
        if (!string.Equals(effectivePath, path, StringComparison.Ordinal))
        {
            Console.WriteLine($"[HandleRenameAsync] detected duplicate tail segment; path='{path}' => '{effectivePath}'");
        }

        Console.WriteLine($"[HandleRenameAsync] path='{effectivePath}', name='{name}', newName='{newName}', targetPath='{request.TargetPath}', action='{request.Action}'");

        var items = await _storage.ListAsync(effectivePath, user, cancellationToken);
        var isDirectory = items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase))?.IsDirectory == true;

        var source = Combine(effectivePath, name);
        if (isDirectory) source = EnsureFolder(source);
        var destination = Combine(GetDirectoryPath(source), newName!);
        if (isDirectory) destination = EnsureFolder(destination);

        Console.WriteLine($"[HandleRenameAsync] source='{source}', destination='{destination}', isDirectory={isDirectory}");

        var sourcePerms = await _accessPolicy.GetPermissionsAsync(user, source, cancellationToken);
        var destPerms = await _accessPolicy.GetPermissionsAsync(user, destination, cancellationToken);
        if (!sourcePerms.CanWrite || !destPerms.CanWrite || !sourcePerms.CanDelete) return Forbid();

        Console.WriteLine($"[HandleRenameAsync] permissions: source(write={sourcePerms.CanWrite}, delete={sourcePerms.CanDelete}), dest(write={destPerms.CanWrite})");

        await _storage.MoveAsync(source, destination, user, cancellationToken);
        await _audit.LogAsync(new AuditEvent(DateTimeOffset.UtcNow, user.UserId, "Rename", $"{source} -> {destination}"), cancellationToken);

        var refreshPath = GetDirectoryPath(destination);
        Console.WriteLine($"[HandleRenameAsync] refreshPath='{refreshPath}'");
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

        // Log what we are sending back to help trace UI errors (e.g., thumbnail/null refs).
        try
        {
            var payload = new
            {
                cwd,
                files = files.Select(f => new
                {
                    f.Name,
                    f.Path,
                    f.FilterPath,
                    f.Id,
                    f.ParentId,
                    f.IsFile,
                    f.Type
                })
            };
            Console.WriteLine($"[BuildListingResult] path='{normalizedPath}' payload={JsonSerializer.Serialize(payload)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BuildListingResult] Failed to log payload: {ex.Message}");
        }

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

    [HttpGet("image")]
    public Task<IActionResult> GetImage([FromQuery] string? path, [FromQuery] string? id, [FromQuery] string? name, CancellationToken cancellationToken)
        => GetImageInternal(path, id, name, cancellationToken, "GET");

    [HttpPost("image")]
    public Task<IActionResult> GetImagePost([FromBody] Syncfusion.Blazor.FileManager.FileManagerDirectoryContent payload, CancellationToken cancellationToken)
        => GetImageInternal(payload.Path, payload.Id, payload.Name, cancellationToken, "POST");

    private async Task<IActionResult> GetImageInternal(string? path, string? id, string? name, CancellationToken cancellationToken, string verb)
    {
        var user = BuildUserContext();

        var resolvedPath = ResolveImagePath(path, id, name);

        Console.WriteLine($"[GetImage] verb={verb} path='{path}' id='{id}' name='{name}' => resolvedPath='{resolvedPath}'");

        if (string.IsNullOrWhiteSpace(resolvedPath) || resolvedPath == "/")
        {
            return BadRequest(new { error = "Invalid image path" });
        }

        var perms = await _accessPolicy.GetPermissionsAsync(user, resolvedPath, cancellationToken);
        if (!perms.CanRead) return Forbid();

        var stream = await _storage.OpenReadAsync(resolvedPath, user, cancellationToken);
        await _audit.LogAsync(new AuditEvent(DateTimeOffset.UtcNow, user.UserId, "GetImage", resolvedPath), cancellationToken);

        var fileNameForCt = Path.GetFileName(resolvedPath.TrimEnd('/'));
        if (string.IsNullOrWhiteSpace(fileNameForCt))
        {
            fileNameForCt = "image";
        }

        if (!ContentTypeProvider.TryGetContentType(fileNameForCt, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        return File(stream, contentType, enableRangeProcessing: true);
    }

    [HttpPost("download")]
    public async Task<IActionResult> Download([FromForm] string? path, [FromForm] string? downloadInput, CancellationToken cancellationToken)
    {
        var user = BuildUserContext();
        var requestedPath = ResolveDownloadPath(path, downloadInput);
        _logger.LogInformation("Download requested. path='{Path}', downloadInput='{DownloadInput}', resolved='{Resolved}'",
            path ?? "(null)", downloadInput ?? "(null)", requestedPath ?? "(null)");

        var normalizedPath = NormalizePath(requestedPath);
        _logger.LogInformation("Download normalized path='{NormalizedPath}'", normalizedPath);

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

    /// <summary>
    /// If the last two path segments are identical (e.g., "/foo/foo"), drop the final duplicate.
    /// This guards against duplicated folder segments occasionally sent by the client during rename.
    /// </summary>
    private static string DeduplicateTrailingSegment(string path)
    {
        var canonical = CanonicalPath(path);
        if (canonical == "/") return canonical;

        var segments = canonical.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 && segments[^1].Equals(segments[^2], StringComparison.OrdinalIgnoreCase))
        {
            var trimmed = "/" + string.Join("/", segments[..^1]);
            return string.IsNullOrWhiteSpace(trimmed) ? "/" : trimmed;
        }

        return canonical;
    }

    private static string Combine(string basePath, string name)
    {
        var trimmedBase = basePath.TrimEnd('/');
        var trimmedName = name.TrimStart('/');

        // If the incoming name is already an absolute path (starts with "/"), prefer it directly
        // to avoid double-prefixing when the client sends absolute paths.
        if (name.StartsWith("/", StringComparison.Ordinal))
        {
            return CanonicalPath(name);
        }

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
        var parentId = string.IsNullOrWhiteSpace(parentCanonical) || parentCanonical == "/"
            ? "/"
            : EnsureFolder(parentCanonical);
        var filterPath = parentId; // Syncfusion expects filterPath to be parent path with trailing slash (or "/")
        var permission = new AccessPermission
        {
            Read = true,
            Write = true,
            Upload = true,
            Download = true,
            Copy = true,
            WriteContents = true
        };

        // Syncfusion builds thumbnail links using FilterId; keep it in sync with FilterPath/ParentId.
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
            FilterId = filterPath,
            Path = pathForUi,
            Id = pathForUi,
            ParentId = parentId,
            Permission = permission
        };
    }

    private static FileManagerDirectoryContent MapToCwd(string path, IReadOnlyList<FileItem> items, string rootAliasName)
    {
        var canonical = CanonicalPath(path);
        var pathForUi = canonical == "/" ? "/" : EnsureFolder(canonical);
        var name = Decode(GetNameFromPath(canonical));
        var hasChild = items.Any(); // mark true if any child (files or folders)

        // Get the parent path for filterPath
        var filterPathCanonical = GetDirectoryPath(canonical);
        var filterPathUi = string.IsNullOrWhiteSpace(filterPathCanonical) || filterPathCanonical == "/"
            ? "/"
            : EnsureFolder(filterPathCanonical);
        var id = pathForUi == "/" ? "/" : pathForUi;
        var permission = new AccessPermission
        {
            Read = true,
            Write = true,
            Upload = true,
            Download = true,
            Copy = true,
            WriteContents = true
        };

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
            FilterId = filterPathUi,
            Path = pathForUi,
            Id = id,
            ParentId = filterPathUi,
            Permission = permission
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

    private static readonly JsonSerializerOptions DownloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static string? ResolveDownloadPath(string? path, string? downloadInput)
    {
        // Syncfusion posts the selection as `downloadInput` (form-urlencoded JSON).
        // Shapes observed:
        //  - { names: ["/folder/file.txt"], path:"/folder/", data:[{ path:"/folder/file.txt", name:"file.txt", ... }] }
        //  - legacy: { items:[{ path:"/folder/", name:"file.txt" }] }
        //
        // Goal: return an absolute, canonical object path to the selected file.
        if (!string.IsNullOrWhiteSpace(downloadInput))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<DownloadInput>(downloadInput, DownloadJsonOptions);
                var selected = parsed?.Items?.FirstOrDefault() ?? parsed?.Data?.FirstOrDefault();

                // 1) If the item already carries a full path/id, use it directly.
                var candidatePath = FirstNonEmpty(
                    selected?.Path,
                    selected?.Id,
                    parsed?.Names?.FirstOrDefault()
                );
                if (!string.IsNullOrWhiteSpace(candidatePath))
                {
                    // Names may already be absolute (starting with '/'), ensure canonical form.
                    if (candidatePath.StartsWith("/", StringComparison.Ordinal))
                    {
                        return CanonicalPath(candidatePath);
                    }

                    // Relative name: combine with the form's path or item's parent/filter path.
                    var basePath = NormalizePath(FirstNonEmpty(
                        path,
                        parsed?.Path,
                        selected?.ParentId,
                        selected?.FilterPath,
                        selected?.FilterId,
                        "/"));
                    return Combine(basePath, candidatePath);
                }

                // 2) Fall back to combining the item name with best-effort base path.
                if (!string.IsNullOrWhiteSpace(selected?.Name))
                {
                    var basePath = NormalizePath(FirstNonEmpty(
                        path,
                        parsed?.Path,
                        selected?.ParentId,
                        selected?.FilterPath,
                        selected?.FilterId,
                        "/"));
                    return Combine(basePath, selected.Name);
                }
            }
            catch
            {
                // ignore parse errors and fall through
            }
        }

        if (!string.IsNullOrWhiteSpace(path)) return path;

        return "/";
    }

    internal static string ResolveDeletePath(string currentPath, string name, bool isDirectory)
    {
        var isAbsolute = name.StartsWith("/", StringComparison.Ordinal);
        var deletePath = isAbsolute ? CanonicalPath(name) : Combine(currentPath, name);
        if (isDirectory) deletePath = EnsureFolder(deletePath);
        return deletePath;
    }

    internal static string ResolveImagePath(string? path, string? id, string? name)
    {
        var basePath = NormalizePath(path);
        var primary = FirstNonEmpty(id, name);

        if (!string.IsNullOrWhiteSpace(primary))
        {
            if (primary.StartsWith("/", StringComparison.Ordinal))
            {
                return CanonicalPath(primary);
            }

            return CanonicalPath(Combine(basePath, primary));
        }

        return basePath;
    }

    public sealed class FileManagerRequest
    {
        public string? Action { get; set; }
        public string? Path { get; set; }
        public string? TargetPath { get; set; }
        public string? NewName { get; set; }
        public string? Name { get; set; }
        public List<string>? Names { get; set; }

        public override string ToString()
        {
            // Helpful for debugging payload mismatches from the client
            return $"Action='{Action}', Path='{Path}', TargetPath='{TargetPath}', NewName='{NewName}', Name='{Name}', Names=[{string.Join(",", Names ?? new List<string>())}]";
        }
    }

    private sealed class DownloadInput
    {
        public string? Path { get; set; }

        // Syncfusion currently posts selected items under `data`; older code used `items`.
        [JsonPropertyName("data")]
        public List<DownloadItem>? Data { get; set; }

        [JsonPropertyName("items")]
        public List<DownloadItem>? Items { get; set; }

        public List<string>? Names { get; set; }
    }

    private sealed class DownloadItem
    {
        public string? Name { get; set; }
        public string? Path { get; set; }
        public string? Id { get; set; }
        public string? ParentId { get; set; }
        public string? FilterPath { get; set; }
        public string? FilterId { get; set; }
    }
}
