using Minio;
using Minio.DataModel;
using Minio.DataModel.Args;
using S3FileManager.Core;

namespace S3FileManager.Storage.Minio;

/// <summary>
/// Basic MinIO implementation of IObjectStorageBackend.
/// NOTE: This is a starter skeleton and does not yet handle
/// all edge cases (large objects, multipart, etc.).
/// </summary>
public sealed class MinioStorageBackend : IObjectStorageBackend
{
    private readonly IMinioClient _client;
    private readonly string _bucketName;

    public MinioStorageBackend(IMinioClient client, string bucketName)
    {
        _client = client;
        _bucketName = bucketName;
    }

    public async Task<IReadOnlyList<FileItem>> ListAsync(string path, UserContext user, CancellationToken cancellationToken = default)
    {
        try
        {
            var prefix = NormalizePrefix(path);
            var items = new List<FileItem>();

            var listArgs = new ListObjectsArgs()
                .WithBucket(_bucketName)
                .WithPrefix(prefix)
                .WithRecursive(false);

            var results = _client.ListObjectsEnumAsync(listArgs, cancellationToken: cancellationToken);

            await foreach (var entry in results.ConfigureAwait(false))
            {
                // Skip the placeholder object that represents the current folder itself
                if (!string.IsNullOrEmpty(prefix) &&
                    entry.Key.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entry.IsDir)
                {
                    // common prefix (virtual folder)
                    items.Add(MapPrefixToDirectory(entry.Key));
                }
                else if (entry.Key.EndsWith("/", StringComparison.Ordinal))
                {
                    // Placeholder "folder object" (zero-byte key ending with '/')
                    items.Add(MapPrefixToDirectory(entry.Key));
                }
                else
                {
                    items.Add(MapObjectToFile(entry));
                }
            }

            // remove duplicates in case both dir listing and object listing return same virtual folder
            return items
                .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error listing objects in bucket '{_bucketName}' with path '{path}': {ex.Message}");
            throw new InvalidOperationException($"Failed to list objects in bucket '{_bucketName}': {ex.Message}", ex);
        }
    }

    public async Task UploadAsync(string path, Stream content, UserContext user, CancellationToken cancellationToken = default)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));

        try
        {
            var isDirectoryPlaceholder = path.EndsWith("/", StringComparison.Ordinal);
            var objectKey = NormalizeObjectKey(path, preserveTrailingSlash: isDirectoryPlaceholder);

            var (uploadStream, length, disposeAfter) = await EnsureSeekableStreamAsync(
                isDirectoryPlaceholder ? Stream.Null : content,
                cancellationToken);

            var putArgs = new PutObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectKey)
                .WithStreamData(uploadStream)
                .WithObjectSize(length)
                .WithContentType("application/octet-stream");

            await _client.PutObjectAsync(putArgs, cancellationToken).ConfigureAwait(false);

            if (disposeAfter)
            {
                await uploadStream.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to upload object '{path}' to bucket '{_bucketName}'.", ex);
        }
    }

    public async Task DeleteAsync(string path, UserContext user, CancellationToken cancellationToken = default)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));

        try
        {
            var isDirectory = IsDirectoryPath(path);
            Console.WriteLine($"[DeleteAsync] Input path: '{path}', IsDirectory: {isDirectory}");

            if (!isDirectory)
            {
                var objectKey = NormalizeObjectKey(path);
                Console.WriteLine($"[DeleteAsync] Deleting file with key: '{objectKey}'");
                var args = new RemoveObjectArgs()
                    .WithBucket(_bucketName)
                    .WithObject(objectKey);

                await _client.RemoveObjectAsync(args, cancellationToken).ConfigureAwait(false);
                return;
            }

            var prefix = NormalizePrefix(path);
            Console.WriteLine($"[DeleteAsync] Normalized prefix: '{prefix}'");
            
            var keys = await CollectKeysForPrefixAsync(prefix, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"[DeleteAsync] Found {keys.Count} keys under prefix");
            foreach (var key in keys)
            {
                Console.WriteLine($"[DeleteAsync]   - Key: '{key}'");
            }
            
            // For empty folders, we need to explicitly delete the placeholder object (folder/ key)
            if (keys.Count == 0)
            {
                // Try to delete the folder placeholder object itself
                var folderKey = prefix.TrimEnd('/');
                if (!string.IsNullOrEmpty(folderKey))
                {
                    folderKey += "/";
                    Console.WriteLine($"[DeleteAsync] No keys found, attempting to delete folder placeholder: '{folderKey}'");
                    var args = new RemoveObjectArgs()
                        .WithBucket(_bucketName)
                        .WithObject(folderKey);

                    try
                    {
                        await _client.RemoveObjectAsync(args, cancellationToken).ConfigureAwait(false);
                        Console.WriteLine($"[DeleteAsync] Successfully deleted folder placeholder: '{folderKey}'");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[DeleteAsync] Failed to delete folder placeholder: {ex.Message}");
                        // Folder placeholder might not exist, which is okay
                        // (could be a virtual folder created by nested objects)
                    }
                }
                return;
            }

            // Prefer batch deletion when there are multiple objects
            if (keys.Count > 1)
            {
                Console.WriteLine($"[DeleteAsync] Batch deleting {keys.Count} objects");
                var removeArgs = new RemoveObjectsArgs()
                    .WithBucket(_bucketName)
                    .WithObjects(keys);

                var errors = await _client.RemoveObjectsAsync(removeArgs, cancellationToken).ConfigureAwait(false);
                if (errors?.Count > 0)
                {
                    var first = errors[0];
                    throw new InvalidOperationException($"Failed to delete '{first.Key}': {first.Message}");
                }
            }
            else
            {
                Console.WriteLine($"[DeleteAsync] Deleting single object: '{keys[0]}'");
                var args = new RemoveObjectArgs()
                    .WithBucket(_bucketName)
                    .WithObject(keys[0]);

                await _client.RemoveObjectAsync(args, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DeleteAsync] Exception: {ex.Message}");
            throw new InvalidOperationException($"Failed to delete '{path}' in bucket '{_bucketName}'.", ex);
        }
    }

    public async Task MoveAsync(string fromPath, string toPath, UserContext user, CancellationToken cancellationToken = default)
    {
        if (fromPath is null) throw new ArgumentNullException(nameof(fromPath));
        if (toPath is null) throw new ArgumentNullException(nameof(toPath));

        try
        {
            var looksLikeDirectory = IsDirectoryPath(fromPath);
            var sourcePrefix = looksLikeDirectory ? NormalizePrefix(fromPath) : NormalizeObjectKey(fromPath);

            Console.WriteLine($"[Minio.MoveAsync] from='{fromPath}', to='{toPath}', looksLikeDirectory={looksLikeDirectory}, initialSourcePrefix='{sourcePrefix}'");

            // If not explicitly a directory, detect by checking if there are objects under the prefix.
            if (!looksLikeDirectory)
            {
                var tentativePrefix = NormalizePrefix(fromPath);
                looksLikeDirectory = await PrefixHasObjectsAsync(tentativePrefix, cancellationToken).ConfigureAwait(false);
                if (looksLikeDirectory)
                {
                    sourcePrefix = tentativePrefix;
                    Console.WriteLine($"[Minio.MoveAsync] Detected directory based on prefix contents. Using sourcePrefix='{sourcePrefix}'");
                }
            }

            if (!looksLikeDirectory)
            {
                var destinationKey = NormalizeObjectKey(toPath);
                Console.WriteLine($"[Minio.MoveAsync] Treating as file move. sourceKey='{sourcePrefix}', destinationKey='{destinationKey}'");
                await CopyObjectAsync(sourcePrefix, destinationKey, cancellationToken).ConfigureAwait(false);
                await DeleteAsync(fromPath, user, cancellationToken).ConfigureAwait(false);
                return;
            }

            var destinationPrefix = NormalizePrefix(EnsureTrailingSlash(toPath));
            var keys = await CollectKeysForPrefixAsync(sourcePrefix, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"[Minio.MoveAsync] Treating as directory move. keysFound={keys.Count}, sourcePrefix='{sourcePrefix}', destinationPrefix='{destinationPrefix}'");

            foreach (var key in keys)
            {
                var relative = key[sourcePrefix.Length..];
                var destinationKey = string.Concat(destinationPrefix, relative);
                Console.WriteLine($"[Minio.MoveAsync] Copy '{key}' -> '{destinationKey}'");
                await CopyObjectAsync(key, destinationKey, cancellationToken).ConfigureAwait(false);
            }

            // remove source objects
            if (keys.Count > 0)
            {
                var removeArgs = new RemoveObjectsArgs()
                    .WithBucket(_bucketName)
                    .WithObjects(keys);

                var errors = await _client.RemoveObjectsAsync(removeArgs, cancellationToken).ConfigureAwait(false);
                if (errors?.Count > 0)
                {
                    var first = errors[0];
                    throw new InvalidOperationException($"Failed to delete '{first.Key}' after move: {first.Message}");
                }
                else
                {
                    Console.WriteLine($"[Minio.MoveAsync] Removed {keys.Count} source objects after copy.");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Minio.MoveAsync] ERROR moving '{fromPath}' -> '{toPath}': {ex}");
            throw new InvalidOperationException($"Failed to move '{fromPath}' to '{toPath}' in bucket '{_bucketName}'.", ex);
        }
    }

    public async Task<Stream> OpenReadAsync(string path, UserContext user, CancellationToken cancellationToken = default)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));

        try
        {
            var key = NormalizeObjectKey(path);
            var memoryStream = new MemoryStream();

            var getArgs = new GetObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(key)
                .WithCallbackStream(stream => stream.CopyTo(memoryStream));

            await _client.GetObjectAsync(getArgs, cancellationToken).ConfigureAwait(false);
            memoryStream.Position = 0;
            return memoryStream;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to open '{path}' for read from bucket '{_bucketName}'.", ex);
        }
    }

    private static string NormalizePrefix(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/" || path == ".")
            return string.Empty;

        var cleaned = path.Replace("\\", "/");
        if (cleaned.StartsWith("/"))
            cleaned = cleaned[1..];

        if (!cleaned.EndsWith("/"))
            cleaned += "/";

        return cleaned;
    }

    private static FileItem MapObjectToFile(Item item)
    {
        var name = GetNameFromKey(item.Key);
        return new FileItem(
            Name: name,
            Path: NormalizePublicPath(item.Key),
            IsDirectory: false,
            Size: (long?)item.Size,
            LastModified: null
        );
    }

    private static FileItem MapPrefixToDirectory(string prefix)
    {
        var name = GetNameFromKey(prefix.TrimEnd('/'));
        var dirPath = NormalizePublicPath(prefix, ensureTrailingSlash: true);
        return new FileItem(
            Name: name,
            Path: dirPath,
            IsDirectory: true,
            Size: null,
            LastModified: null
        );
    }

    private static string GetNameFromKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return "/";
        var trimmed = key.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx >= 0 ? trimmed[(idx + 1)..] : trimmed;
    }

    private static string NormalizePublicPath(string key, bool ensureTrailingSlash = false)
    {
        var cleaned = key.Replace("\\", "/");
        if (!cleaned.StartsWith("/"))
            cleaned = "/" + cleaned;

        if (ensureTrailingSlash && !cleaned.EndsWith("/"))
            cleaned += "/";

        return cleaned;
    }

    private static string NormalizeObjectKey(string path, bool preserveTrailingSlash = false)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        var cleaned = path.Replace("\\", "/").TrimStart('/');
        return preserveTrailingSlash ? cleaned : cleaned.TrimEnd('/');
    }

    private static string EnsureTrailingSlash(string path)
    {
        return path.EndsWith("/", StringComparison.Ordinal) ? path : path + "/";
    }

    private static bool IsDirectoryPath(string path) => path.EndsWith("/", StringComparison.Ordinal);

    private static async Task<(Stream Stream, long Length, bool DisposeAfter)> EnsureSeekableStreamAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        if (source.CanSeek)
        {
            return (source, source.Length - source.Position, false);
        }

        var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        return (buffer, buffer.Length, true);
    }

    private async Task<bool> PrefixHasObjectsAsync(string prefix, CancellationToken cancellationToken)
    {
        var listArgs = new ListObjectsArgs()
            .WithBucket(_bucketName)
            .WithPrefix(prefix)
            .WithRecursive(true);

        var results = _client.ListObjectsEnumAsync(listArgs, cancellationToken: cancellationToken);
        await foreach (var _ in results.ConfigureAwait(false))
        {
            return true;
        }

        return false;
    }

    private async Task<List<string>> CollectKeysForPrefixAsync(string prefix, CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        var listArgs = new ListObjectsArgs()
            .WithBucket(_bucketName)
            .WithPrefix(prefix)
            .WithRecursive(true);

        var results = _client.ListObjectsEnumAsync(listArgs, cancellationToken: cancellationToken);
        await foreach (var entry in results.ConfigureAwait(false))
        {
            // Skip virtual directory entries (common prefixes) but include actual objects
            // Folder placeholder objects (ending with /) are returned as regular objects, not IsDir
            if (entry.IsDir) continue;
            keys.Add(entry.Key);
        }

        return keys;
    }

    private async Task CopyObjectAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken)
    {
        try
        {
            Console.WriteLine($"[Minio.CopyObjectAsync] '{sourceKey}' -> '{destinationKey}'");

            var copySourceArgs = new CopySourceObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(sourceKey);

            var copyArgs = new CopyObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(destinationKey)
                .WithCopyObjectSource(copySourceArgs);

            await _client.CopyObjectAsync(copyArgs, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Minio.CopyObjectAsync] ERROR copying '{sourceKey}' -> '{destinationKey}': {ex}");
            throw;
        }
    }
}
