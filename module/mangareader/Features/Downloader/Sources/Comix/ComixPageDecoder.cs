using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Module.Mangareader.Sources;

namespace Module.Mangareader.Features.Downloader.Sources.Comix;



/// <summary>
/// Comix page descrambling, kept entirely inside the Comix boundary: the queue
/// and the generic transport never inspect scramble headers or instantiate
/// this type.
///
/// Algorithm 3 with an N×N grid is the only supported variant. The permutation
/// is generated from the declared seed with xorshift32 and applied as an
/// inverse tile permutation; a declared hash that is not in the known set
/// follows the current upstream fallback of mixing it into the seed state.
/// </summary>
public sealed class ComixPageDecoder
{
    public const int SupportedAlgorithm = 3;
    public const int MinimumGrid = 2;

    public static bool IsSupported(ComixScrambleHeader header) =>
        header.Algorithm == SupportedAlgorithm && header.Grid >= MinimumGrid;

    /// <summary>
    /// Restores page order and returns PNG bytes. Throws for an unsupported
    /// algorithm, a non-positive grid, or an image that cannot be decoded —
    /// each of those is a visible page failure rather than a silent pass.
    /// </summary>
    public byte[] Descramble(byte[] scrambled, ComixScrambleHeader header)
    {
        ArgumentNullException.ThrowIfNull(scrambled);
        ArgumentNullException.ThrowIfNull(header);
        if (!IsSupported(header))
        {
            throw new InvalidDataException(
                $"Comix scramble variant is not supported: algorithm {header.Algorithm}, grid {header.Grid}.");
        }

        var source = Decode(scrambled);
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var permutation = GeneratePermutation(header.Grid, header.Seed, header.Hash);

        var tileWidth = width / header.Grid;
        var tileHeight = height / header.Grid;
        if (tileWidth < 1 || tileHeight < 1)
        {
            throw new InvalidDataException(
                $"Page {width}x{height} is too small for a {header.Grid}x{header.Grid} grid.");
        }

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            for (var index = 0; index < permutation.Count; index++)
            {
                var sourceRect = TileRect(index, header.Grid, width, height, tileWidth, tileHeight);
                var target = TileRect(
                    permutation[index], header.Grid, width, height, tileWidth, tileHeight);
                context.DrawImage(
                    new CroppedBitmap(source, sourceRect),
                    new Rect(target.X, target.Y, target.Width, target.Height));
            }
        }

        var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        rendered.Freeze();
        return EncodePng(rendered);
    }

    /// <summary>
    /// Tile geometry for one grid index, giving the final row and column any
    /// remainder pixels so a page whose dimensions are not exact multiples of
    /// the grid still reconstructs at full size.
    /// </summary>
    internal static Int32Rect TileRect(
        int index,
        int grid,
        int width,
        int height,
        int tileWidth,
        int tileHeight)
    {
        var column = index % grid;
        var row = index / grid;
        var x = column * tileWidth;
        var y = row * tileHeight;
        var rectWidth = column == grid - 1 ? width - x : tileWidth;
        var rectHeight = row == grid - 1 ? height - y : tileHeight;
        return new Int32Rect(x, y, rectWidth, rectHeight);
    }

    /// <summary>
    /// The forward permutation: destination index for each source tile.
    /// <c>dest[permutation[i]] = src[i]</c>, so applying it once restores page
    /// order and applying its inverse reproduces the scrambled layout.
    /// </summary>
    internal static IReadOnlyList<int> GeneratePermutation(int grid, long seed, int? hash)
    {
        if (grid < MinimumGrid)
        {
            throw new ArgumentOutOfRangeException(nameof(grid), "grid must be at least 2");
        }

        var count = grid * grid;
        var order = new int[count];
        for (var index = 0; index < count; index++)
        {
            order[index] = index;
        }

        var state = SeedState(seed, hash);
        for (var index = count - 1; index > 0; index--)
        {
            var swap = (int)(XorShift32(ref state) % (uint)(index + 1));
            (order[index], order[swap]) = (order[swap], order[index]);
        }

        return order;
    }

    /// <summary>
    /// Declared hashes that upstream treats as "use the seed directly". An
    /// unlisted hash follows the current fallback of mixing it into the state,
    /// which is how the newer unlisted sample was decoded during research.
    /// </summary>
    private static readonly int[] KnownHashes =
    [
        30903, 4722, 53887, 37102, 6206, 54509, 35122, 46808,
    ];

    private static uint SeedState(long seed, int? hash)
    {
        var state = (uint)(seed & 0xFFFFFFFFL);
        if (hash is { } declared && Array.IndexOf(KnownHashes, declared) < 0)
        {
            state ^= (uint)(declared & 0xFFFFFFFFL);
        }

        // xorshift32 has no fixed point at zero, so a zero state is advanced to
        // a deterministic non-zero one instead of producing an identity shuffle.
        return state == 0 ? 0x9E3779B9u : state;
    }

    private static uint XorShift32(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    private static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidDataException("Scrambled page has no image frame.");
        }

        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
