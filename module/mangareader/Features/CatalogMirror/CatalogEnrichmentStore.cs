using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Module.Mangareader.Features.CatalogMirror;

// Atomic read/replace of one metadata.json per normalized title identity.
// It never stores cover bytes, appends history, touches the network, or
// modifies the snapshot, the Library or the queue. A newer schema is reported
// and never overwritten; a corrupt or oversized file is reported with the
// bytes preserved.
public sealed class CatalogEnrichmentStore(CatalogMirrorPaths paths)
{
    private const int SchemaVersion = 1;
    private const long MaxMetadataBytes = 4_194_304;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static readonly Encoding NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly CatalogMirrorPaths _paths = paths;

    public async Task WriteAsync(CatalogTitleEnrichment enrichment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enrichment);
        ValidateIdentity(enrichment.SourceId, enrichment.TitleId, enrichment.TitleHid);

        var document = new EnrichmentDocument(SchemaVersion, enrichment);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Json);
        if (bytes.Length > MaxMetadataBytes)
        {
            throw new CatalogSnapshotException("Title enrichment exceeds its size bound.");
        }

        var target = _paths.EnrichmentMetadataPath(enrichment.SourceId, enrichment.TitleId);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = _paths.TempPathForAtomicWrite(target);
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (Exception)
            {
                // Best effort: a stray temp file never affects reads.
            }

            throw;
        }
    }

    public async Task<CatalogTitleEnrichment?> ReadAsync(
        string sourceId,
        string titleId,
        CancellationToken cancellationToken)
    {
        // Blank ids are refused by the path hash below with the same exception.
        var target = _paths.EnrichmentMetadataPath(sourceId, titleId);
        if (!File.Exists(target))
        {
            return null;
        }

        if (new FileInfo(target).Length > MaxMetadataBytes)
        {
            throw new CatalogSnapshotException("Title enrichment exceeds its size bound.");
        }

        EnrichmentDocument document;
        try
        {
            var json = await File.ReadAllTextAsync(target, cancellationToken).ConfigureAwait(false);
            document = JsonSerializer.Deserialize<EnrichmentDocument>(json, Json)
                ?? throw new CatalogSnapshotException("Title enrichment is empty.");
        }
        catch (JsonException exception)
        {
            throw new CatalogSnapshotException("Title enrichment is corrupt and was preserved.", exception);
        }

        if (document.SchemaVersion != SchemaVersion)
        {
            throw new CatalogSnapshotException(
                document.SchemaVersion > SchemaVersion
                    ? "Title enrichment schema is newer and was preserved."
                    : "Title enrichment schema is unreadable and was preserved.");
        }

        ArgumentNullException.ThrowIfNull(document.Enrichment);
        ValidateIdentity(
            document.Enrichment.SourceId,
            document.Enrichment.TitleId,
            document.Enrichment.TitleHid);
        return document.Enrichment;
    }

    private static void ValidateIdentity(string sourceId, string titleId, string titleHid)
    {
        if (string.IsNullOrWhiteSpace(sourceId)
            || string.IsNullOrWhiteSpace(titleId)
            || string.IsNullOrWhiteSpace(titleHid))
        {
            throw new CatalogSnapshotException("Title enrichment has an invalid identity.");
        }
    }

    private sealed record EnrichmentDocument(int SchemaVersion, CatalogTitleEnrichment? Enrichment);
}
