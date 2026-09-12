using System.IO;
using System.Text;

namespace Module.Proxy.SharedLogic;

internal static class AtomicTextFile
{
    public static void Write(string path, IEnumerable<string> lines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(lines);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("state path has no parent: " + fullPath);
        Directory.CreateDirectory(directory);

        var staging = fullPath + ".staging";
        try
        {
            using (var stream = new FileStream(
                staging,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                foreach (var line in lines)
                {
                    writer.WriteLine(line);
                }
                writer.Flush();
                stream.Flush(true);
            }

            File.Move(staging, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(staging))
            {
                File.Delete(staging);
            }
        }
    }

    public static void WriteAllText(string path, string content)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimEnd('\n');
        Write(path, normalized.Length == 0 ? [] : normalized.Split('\n'));
    }
}
