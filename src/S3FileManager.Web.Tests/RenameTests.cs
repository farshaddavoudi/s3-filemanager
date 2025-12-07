using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using S3FileManager.Core;
using S3FileManager.Web.Configuration;
using S3FileManager.Web.Controllers;
using Xunit;
using Request = S3FileManager.Web.Controllers.FileManagerController.FileManagerRequest;

namespace S3FileManager.Web.Tests;

public class RenameTests
{
    [Fact]
    public async Task Rename_File_DeduplicatesPathAndMovesCorrectly()
    {
        var storage = new FakeStorage(new[]
        {
            new FileItem("ms-dotnet.jpg", "/farshad/ms-dotnet.jpg", false, 123, null)
        });
        var controller = CreateController(storage);

        var request = new Request
        {
            Action = "rename",
            Path = "/farshad/farshad", // duplicated segment (bug repro)
            Name = "ms-dotnet.jpg",
            NewName = "dotnet.jpg"
        };

        var result = await controller.Operations(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Single(storage.Moves);
        Assert.Equal("/farshad/ms-dotnet.jpg", storage.Moves[0].From);
        Assert.Equal("/farshad/dotnet.jpg", storage.Moves[0].To);
    }

    [Fact]
    public async Task Rename_EmptyFolder_WhenPathPointsToFolderItself_MovesFolder()
    {
        var storage = new FakeStorage(Array.Empty<FileItem>());
        var controller = CreateController(storage);

        var request = new Request
        {
            Action = "rename",
            Path = "/alpha/alpha/Test/", // points at the folder itself
            Name = "Test",
            NewName = "Renamed"
        };

        var result = await controller.Operations(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Single(storage.Moves);
        Assert.Equal("/alpha/alpha/Test/", storage.Moves[0].From);
        Assert.Equal("/alpha/alpha/Renamed/", storage.Moves[0].To);
    }

    [Fact]
    public async Task Rename_Folder_WithDuplicatedParentSegment_NormalizesAndRenamesCorrectFolder()
    {
        var storage = new FakeStorage(Array.Empty<FileItem>());
        var controller = CreateController(storage);

        var request = new Request
        {
            Action = "rename",
            Path = "/mahnam3/mahnam3/Test/", // duplicated parent segment from client
            Name = "Test",
            NewName = "Fest"
        };

        var result = await controller.Operations(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Single(storage.Moves);
        Assert.Equal("/mahnam3/Test/", storage.Moves[0].From);
        Assert.Equal("/mahnam3/Fest/", storage.Moves[0].To);
    }

    [Fact]
    public async Task Rename_Folder_WithChildren_UsesListingToDetectDirectory()
    {
        var storage = new FakeStorage(new[]
        {
            new FileItem("Child", "/alpha/Child/", true, null, null)
        });
        var controller = CreateController(storage);

        var request = new Request
        {
            Action = "rename",
            Path = "/alpha",
            Name = "Child",
            NewName = "RenamedChild"
        };

        var result = await controller.Operations(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Single(storage.Moves);
        Assert.Equal("/alpha/Child/", storage.Moves[0].From);
        Assert.Equal("/alpha/RenamedChild/", storage.Moves[0].To);
    }

    private static FileManagerController CreateController(FakeStorage storage)
    {
        return new FileManagerController(
            storage,
            new AllowAllAccessPolicy(),
            new NoopAuditLog(),
            new AppConfig
            {
                MinioEndpoint = "http://localhost",
                MinioAccessKey = "key",
                MinioSecretKey = "secret",
                MinioBucket = "bucket"
            },
            NullLogger<FileManagerController>.Instance);
    }

    private sealed class FakeStorage : IObjectStorageBackend
    {
        private readonly IReadOnlyList<FileItem> _items;

        public FakeStorage(IReadOnlyList<FileItem> items)
        {
            _items = items;
        }

        public List<(string From, string To)> Moves { get; } = new();

        public Task<IReadOnlyList<FileItem>> ListAsync(string path, UserContext user, CancellationToken cancellationToken = default)
            => Task.FromResult(_items);

        public Task UploadAsync(string path, Stream content, UserContext user, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteAsync(string path, UserContext user, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task MoveAsync(string fromPath, string toPath, UserContext user, CancellationToken cancellationToken = default)
        {
            Moves.Add((fromPath, toPath));
            return Task.CompletedTask;
        }

        public Task<Stream> OpenReadAsync(string path, UserContext user, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream>(Stream.Null);
    }

    private sealed class AllowAllAccessPolicy : IAccessPolicyProvider
    {
        public Task<EffectivePermissions> GetPermissionsAsync(UserContext user, string path, CancellationToken cancellationToken = default)
            => Task.FromResult(new EffectivePermissions(PermissionFlags.Read | PermissionFlags.Write | PermissionFlags.Delete | PermissionFlags.Upload));
    }

    private sealed class NoopAuditLog : IAuditLogProvider
    {
        public Task LogAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
