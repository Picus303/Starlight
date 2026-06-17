using System.IO.Compression;
using Starlight.Game.Resources;
using Xunit;

namespace Starlight.Tests;

public sealed class ResourceLoaderTests
{
    // Verifies that FolderLoader returns normalized relative paths so callers do
    // not depend on absolute filesystem paths or platform-specific separators.
    [Fact]
    public void FolderLoader_ListFiles_ReturnsNormalizedRelativePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "Starlight.Tests", $"{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "foo", "nested"));
        File.WriteAllText(Path.Combine(root, "foo", "a.json"), "{}");
        File.WriteAllText(Path.Combine(root, "foo", "nested", "b.json"), "{}");

        try
        {
            var loader = new FolderLoader(new DirectoryInfo(root));
            var files = loader.ListFiles("foo", "*.json");

            Assert.Equal(["foo/a.json", "foo/nested/b.json"], files.OrderBy(x => x).ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // Verifies that the normalized path returned by ZipLoader can be passed back
    // into ReadRaw without any backend-specific path conversion.
    [Fact]
    public void ZipLoader_ListFiles_ReturnsPathsReusableByReadRaw()
    {
        using var stream = new MemoryStream();

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            CreateEntry(archive, "foo/nested/a.json", "{\"ok\":true}");
        }

        stream.Position = 0;

        using var readArchive = new ZipArchive(stream, ZipArchiveMode.Read);
        var loader = new ZipLoader(readArchive);
        var path = Assert.Single(loader.ListFiles("foo", "*.json"));

        var raw = loader.ReadRaw(path);

        Assert.Equal("{\"ok\":true}", System.Text.Encoding.UTF8.GetString(raw));
    }

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
