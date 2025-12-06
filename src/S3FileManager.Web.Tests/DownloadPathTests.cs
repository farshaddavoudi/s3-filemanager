using S3FileManager.Web.Controllers;
using Xunit;

namespace S3FileManager.Web.Tests;

public class DownloadPathTests
{
    [Fact]
    public void ResolveDownloadPath_UsesDataEntryAbsolutePath()
    {
        var json = """
        {"action":"download","path":"//mahnam//","names":["/mahnam/github-recovery-codes.txt"],"data":[{"path":"/mahnam/github-recovery-codes.txt","name":"github-recovery-codes.txt","id":"/mahnam/github-recovery-codes.txt","filterPath":"/mahnam/","filterId":"/mahnam/","parentId":"/mahnam/"}]}
        """;

        var result = FileManagerController.ResolveDownloadPath(null, json);

        Assert.Equal("/mahnam/github-recovery-codes.txt", result);
    }

    [Fact]
    public void ResolveDownloadPath_FallsBackToNamesAbsolute()
    {
        var json = """
        {"names":["/foo/bar.txt"],"path":"/foo/"}
        """;

        var result = FileManagerController.ResolveDownloadPath(null, json);

        Assert.Equal("/foo/bar.txt", result);
    }

    [Fact]
    public void ResolveDownloadPath_CombinesRelativeNameWithPath()
    {
        var json = """
        {"names":["bar.txt"],"path":"/foo/"}
        """;

        var result = FileManagerController.ResolveDownloadPath(null, json);

        Assert.Equal("/foo/bar.txt", result);
    }

    [Fact]
    public void ResolveDownloadPath_UsesItemNameWhenNoNames()
    {
        var json = """
        {"path":"/alpha/","data":[{"path":null,"name":"file.bin","parentId":"/alpha/"}]}
        """;

        var result = FileManagerController.ResolveDownloadPath(null, json);

        Assert.Equal("/alpha/file.bin", result);
    }
}
