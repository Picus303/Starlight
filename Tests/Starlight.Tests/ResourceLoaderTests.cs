using System.IO.Compression;
using Starlight.Game.Resources;
using Xunit;

namespace Starlight.Tests;

public sealed class ResourceLoaderTests
{
    // Verifies that ZipLoader matches files from the requested directory only,
    // instead of also leaking in sibling prefixes such as "foo" and "foobar".
    [Fact]
    public void ZipLoader_ListFiles_DoesNotMatchSiblingDirectoryPrefixes()
    {
        using var stream = new MemoryStream();

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            CreateEntry(archive, "foo/a.json", "{}");
            CreateEntry(archive, "foo/nested/c.json", "{}");
            CreateEntry(archive, "foobar/b.json", "{}");
        }

        stream.Position = 0;

        using var readArchive = new ZipArchive(stream, ZipArchiveMode.Read);
        var loader = new ZipLoader(readArchive);

        var files = loader.ListFiles("foo", "*.json");

        Assert.Equal(["foo/a.json", "foo/nested/c.json"], files.OrderBy(x => x).ToArray());
    }

    private static void CreateEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);

        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}
