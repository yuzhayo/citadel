using System.IO;
using System.Windows.Media.Imaging;
using Module.Mangareader.Features.Downloader.Lister;
using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.AutoCover;

/// <summary>
/// The immutable cover candidate relayed when a queue item is added, or built by
/// the remote detail for a manual fetch. It carries the remote title it came from
/// plus a destination folder — never a Library entry, a queue record or job state.
/// </summary>
public sealed record CoverCandidate(RemoteTitleSummary Title, string Root, string FolderName)
{
    public string CoverUrl => Title.CoverUrl ?? string.Empty;

    public IReadOnlyList<string> CoverUrls => Title.CoverCandidates().ToArray();
}

public enum AutoCoverTrigger
{
    /// <summary>Relayed when a queue item was added for a title Lister reports absent.</summary>
    Automatic,

    /// <summary>The user pressed Fetch Cover below the remote cover.</summary>
    Manual,
}

public enum AutoCoverOutcomeKind
{
    Published,
    Skipped,
    Cancelled,
    Failed,
}

public sealed record AutoCoverOutcome(AutoCoverOutcomeKind Kind, string? Path, string? Message)
{
    public bool Succeeded => Kind is AutoCoverOutcomeKind.Published or AutoCoverOutcomeKind.Skipped;
}

/// <summary>
/// Writes one title's <c>cover.png</c> into its deterministic downloaded-title
/// folder. Auto Cover is an independent feature: it is not a final step of a
/// chapter download, not part of Cover Builder, and it never invokes a Library
/// scan or refresh, Cover Builder, Lister or the Update Checker.
///
/// Both accepted triggers run this one command and differ only in what an
/// existing cover means: an automatic invocation skips it, a manual one asks
/// first. Neither can change a chapter job's status, and a failure here is local.
///
/// There is no durable pending work and no polling, so there is no store: a
/// candidate is either handled now or not at all.
/// </summary>
public sealed class AutoCoverFeature
{
    public const string CoverFileName = "cover.png";

    private readonly Func<string, CancellationToken, Task<byte[]>> _fetch;
    private readonly ListerFeature _lister;

    /// <param name="fetch">
    /// The transport seam. Production supplies one HTTP fetch; tests supply bytes
    /// directly so no network is needed to prove the publish rules.
    /// </param>
    /// <param name="lister">
    /// The read-only local availability contract, consulted by the automatic
    /// trigger only.
    /// </param>
    public AutoCoverFeature(
        Func<string, CancellationToken, Task<byte[]>> fetch,
        ListerFeature lister)
    {
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _lister = lister ?? throw new ArgumentNullException(nameof(lister));
    }

    public async Task<AutoCoverOutcome> SaveCoverAsync(
        CoverCandidate candidate,
        AutoCoverTrigger trigger,
        Func<string, bool>? confirmOverwrite,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (candidate.CoverUrls.Count == 0)
        {
            return Fail("Title ini tidak punya URL cover dari provider.");
        }

        // The candidate carries a folder name that can come from persisted state, so
        // the destination is contained before anything touches the filesystem: an
        // unsafe value becomes a local failure with a reason the caller can show,
        // never a cover.png somewhere outside the Library. This also covers the
        // empty root and the empty folder name, which the same rule refuses.
        if (!DownloaderPathContainment.TryResolve(
                candidate.Root,
                candidate.FolderName,
                fileName: null,
                out var folder,
                out var problem))
        {
            return Fail("Folder target cover tidak dapat dipakai: " + problem);
        }

        var finalPath = Path.Combine(folder, CoverFileName);

        // The automatic trigger is defined by absence, so a title that already
        // exists in local storage gets nothing written behind the user's back.
        if (trigger == AutoCoverTrigger.Automatic)
        {
            var listed = await _lister.ListAsync(candidate.Title, cancellationToken).ConfigureAwait(false);
            if (listed.TitleExists)
            {
                return new AutoCoverOutcome(
                    AutoCoverOutcomeKind.Skipped,
                    null,
                    $"Title sudah ada di local storage ('{listed.LocalFolderName}'); cover tidak ditulis otomatis.");
            }
        }

        if (File.Exists(finalPath))
        {
            if (trigger == AutoCoverTrigger.Automatic)
            {
                return new AutoCoverOutcome(
                    AutoCoverOutcomeKind.Skipped,
                    finalPath,
                    "Cover sudah ada; tidak ditimpa.");
            }

            if (confirmOverwrite?.Invoke(finalPath) != true)
            {
                return new AutoCoverOutcome(
                    AutoCoverOutcomeKind.Cancelled,
                    finalPath,
                    "Cover yang sudah ada dipertahankan.");
            }
        }

        byte[]? png = null;
        string? lastFailure = null;
        foreach (var url in candidate.CoverUrls)
        {
            byte[] payload;
            try
            {
                payload = await _fetch(url, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastFailure = "Cover tidak dapat diunduh: " + exception.GetBaseException().Message;
                continue;
            }

            try
            {
                png = AsPng(payload);
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastFailure = "Payload cover bukan gambar yang valid: "
                    + exception.GetBaseException().Message;
            }
        }
        if (png is null) return Fail(lastFailure ?? "Tidak ada kandidat cover yang dapat digunakan.");

        // Creating the deterministic title folder is allowed. The temporary name
        // carries no chapter extension, so a partial write can never be mistaken
        // for a chapter by a Library scan.
        var temporaryPath = Path.Combine(folder, CoverFileName + $".{Guid.NewGuid():N}.tmp");
        try
        {
            // Last point before the filesystem is touched: a caller whose lifetime
            // ended during the fetch leaves neither a file nor a folder behind.
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(temporaryPath, png, cancellationToken).ConfigureAwait(false);

            // Checked again: the write can take long enough for the caller's lifetime
            // to end, and the move is the step that actually publishes. Without this
            // a disposed host could still put a cover on disk.
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, finalPath, overwrite: true);
            return new AutoCoverOutcome(AutoCoverOutcomeKind.Published, finalPath, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Fail("Cover tidak dapat dipublikasikan: " + exception.GetBaseException().Message);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    /// <summary>
    /// Guarantees the published file really is a PNG. Provider posters are
    /// commonly JPEG or WebP, and writing those bytes under a <c>.png</c> name
    /// would mislabel the file, so anything that is not already PNG is decoded and
    /// re-encoded. A payload that cannot be decoded is rejected here, which is the
    /// actual validation: no unknown bytes are ever published.
    /// </summary>
    private static byte[] AsPng(byte[] payload)
    {
        if (payload.Length == 0)
        {
            throw new InvalidDataException("payload cover kosong.");
        }

        if (payload.Length >= 4
            && payload[0] == 0x89 && payload[1] == 0x50 && payload[2] == 0x4E && payload[3] == 0x47)
        {
            return payload;
        }

        var decoder = BitmapDecoder.Create(
            new MemoryStream(payload, writable: false),
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidDataException("payload cover tidak punya frame gambar.");
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(decoder.Frames[0]));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static AutoCoverOutcome Fail(string message) =>
        new(AutoCoverOutcomeKind.Failed, null, message);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
        }
    }
}
