using System.IO;
using Module.Mangareader.ShareLogic;

namespace Module.Mangareader;

/// <summary>
/// History and Cover Builder reuse a cover they already hold only when the
/// render cache folder for that chapter is unchanged, because that folder is
/// derived from the chapter file's identity. If the identity stopped tracking
/// the file, a baked or replaced chapter would keep showing its old cover.
/// </summary>
public sealed class ChapterRenderCacheIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Citadel.RenderCache.Identity.Tests",
        Guid.NewGuid().ToString("N"));

    public ChapterRenderCacheIdentityTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void UnchangedChapterFile_KeepsTheSameCacheFolder()
    {
        var cache = new ChapterRenderCache();
        var chapter = CreateChapter("chapter.cbz", 100);

        Assert.Equal(cache.GetChapterFolder(chapter), cache.GetChapterFolder(chapter));
    }

    [Fact]
    public void RewrittenChapterFile_GetsANewCacheFolder()
    {
        var cache = new ChapterRenderCache();
        var chapter = CreateChapter("chapter.cbz", 100);
        var before = cache.GetChapterFolder(chapter);

        // Baking a cover rewrites the archive at the same path.
        File.WriteAllBytes(chapter.FilePath, new byte[400]);

        Assert.NotEqual(before, cache.GetChapterFolder(chapter));
    }

    private ChapterInfo CreateChapter(string name, int length)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new byte[length]);
        return new ChapterInfo(Path.GetFileNameWithoutExtension(name), path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
