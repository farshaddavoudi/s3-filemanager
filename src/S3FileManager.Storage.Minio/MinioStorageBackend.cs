using Minio;
using Minio.DataModel;
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
        // TODO: implement prefix-based listing and map to FileItem.
        // For now, return an empty list as a placeholder.
        return Array.Empty<FileItem>();
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
}
