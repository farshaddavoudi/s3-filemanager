using S3FileManager.Web.Controllers;
using Xunit;

namespace S3FileManager.Web.Tests;

public class ResolvePathTests
{
    [Theory]
    [InlineData("/babri", "/babri/zandi/", true, "/babri/zandi/")]
    [InlineData("/babri", "zandi/", true, "/babri/zandi/")]
    [InlineData("/babri", "zandi.txt", false, "/babri/zandi.txt")]
    public void ResolveDeletePath_NormalizesPaths(string current, string name, bool isDir, string expected)
    {
        var result = FileManagerController.ResolveDeletePath(current, name, isDir);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("/farshad/", "/farshad/farshad.png", null, "/farshad/farshad.png")]
    [InlineData("/farshad/", null, "farshad.png", "/farshad/farshad.png")]
    [InlineData("/", "image.png", null, "/image.png")]
    public void ResolveImagePath_UsesAbsoluteIdWhenProvided(string path, string? id, string? name, string expected)
    {
        var result = FileManagerController.ResolveImagePath(path, id, name);
        Assert.Equal(expected, result);
    }
}
