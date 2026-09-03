using System.IO;
using Module.Mangareader.Archive;

namespace Module.Mangareader.ShareLogic;

public sealed class LibraryScanner
{
    private static readonly HashSet<string> ChapterExtensions = new(
        [".cbz", ".cbr", ".rar"],
        StringComparer.OrdinalIgnoreCase);

    private readonly ArchivePageReader _archives = new();

    public Task<IReadOnlyList<MangaTitle>> ScanAsync(
        string libraryPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);
        var fullPath = Path.GetFullPath(libraryPath.Trim());

        return Task.Run<IReadOnlyList<MangaTitle>>(
            () => Scan(fullPath, cancellationToken),
            cancellationToken);
    }

    private IReadOnlyList<MangaTitle> Scan(
        string libraryPath,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(libraryPath))
        {
            throw new DirectoryNotFoundException($"Library folder was not found: {libraryPath}");
        }

        var titleFolders = Directory
            .GetDirectories(libraryPath, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(
                path => Path.GetFileName(path) ?? string.Empty,
                NaturalStringComparer.OrdinalIgnoreCase);

        var titles = new List<MangaTitle>();
        foreach (var folder in titleFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chapters = Directory
                .GetFiles(folder, "*", SearchOption.TopDirectoryOnly)
                .Where(path => ChapterExtensions.Contains(Path.GetExtension(path)))
                .Where(_archives.IsSupportedArchive)
                .OrderBy(
                    path => Path.GetFileName(path) ?? string.Empty,
                    NaturalStringComparer.OrdinalIgnoreCase)
                .Select(path => new ChapterInfo(Path.GetFileNameWithoutExtension(path), path))
                .ToArray();

            if (chapters.Length == 0) continue;

            titles.Add(new MangaTitle(
                Path.GetFileName(folder),
                folder,
                chapters));
        }

        return titles;
    }
}
