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
                if (entry.IsDir)
                {
                    // common prefix (virtual folder)
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
        // TODO: implement PutObject / UploadObjectAsync using MinIO SDK.
        await Task.CompletedTask;
    }

    public async Task DeleteAsync(string path, UserContext user, CancellationToken cancellationToken = default)
    {
        // TODO: implement RemoveObject / RemoveObjectsAsync.
        await Task.CompletedTask;
    }

    public async Task MoveAsync(string fromPath, string toPath, UserContext user, CancellationToken cancellationToken = default)
    {
        // TODO: implement Copy + Delete pattern for move/rename.
        await Task.CompletedTask;
    }

    public async Task<Stream> OpenReadAsync(string path, UserContext user, CancellationToken cancellationToken = default)
    {
        // TODO: implement GetObject to a stream.
        // Returning an empty MemoryStream for now.
        return new MemoryStream();
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
}
