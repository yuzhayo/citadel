using System.IO;

namespace Module.Mangareader.Logic;

public sealed class LibraryScanner
{
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

    private static IReadOnlyList<MangaTitle> Scan(
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
                .Where(path => string.Equals(
                    Path.GetExtension(path),
                    ".cbz",
                    StringComparison.OrdinalIgnoreCase))
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
