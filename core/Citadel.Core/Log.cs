using System.Text;

namespace Citadel.Core;

/// <summary>
/// Sinks Main and Modules. One mutex per sink, a thread-local reentrancy
/// guard, a dedicated writer thread, a bounded in-memory ring plus the
/// log.txt tail. Entries never block the caller: they enqueue and the
/// writer thread flushes batches to disk.
///
/// Citadel original where it differs from tdesktop: upstream writes
/// synchronously under one mutex per sink and reads the file back in
/// full(); here the ring is the dump source and the writer thread owns
/// the file.
/// </summary>
public static class Log
{
    private const int RingCapacity = 2048;

    private static readonly object MainSinkLock = new();
    private static readonly object ModulesSinkLock = new();
    private static readonly object QueueLock = new();
    private static readonly ManualResetEventSlim Signal = new(false);

    private static readonly Queue<string> Pending = new();
    private static readonly string?[] Ring = new string?[RingCapacity];
    private static int _ringStart;
    private static int _ringCount;

    [ThreadStatic] private static bool _writing;

    private static Thread? _writer;
    private static StreamWriter? _file;
    private static volatile bool _finished;

    public static bool Started { get; private set; }

    /// <summary>
    /// Opens log.txt in <paramref name="directory"/> (default
    /// %AppData%\Citadel) and flushes everything staged so far. Safe to
    /// call once; later calls are ignored. Entries logged before Start
    /// are staged in the ring and written once the file opens.
    /// </summary>
    public static void Start(string? directory = null)
    {
        lock (QueueLock)
        {
            if (Started) return;
            var dir = directory ?? DefaultDirectory();
            try
            {
                Directory.CreateDirectory(dir);
                _file = new StreamWriter(
                    Path.Combine(dir, "log.txt"), append: true, Encoding.UTF8)
                { AutoFlush = false };
            }
            catch (Exception ex)
            {
                // Started stays false: a failed open is retryable, and marking
                // it started would strand every later entry in Pending forever.
                RingAdd($"[Log] cannot open log.txt: {ex.Message}");
                Signal.Set();
                return;
            }
            Started = true;
            Signal.Set();
        }
        EnsureWriter();
    }

    public static void Main(string message) => Write(MainSinkLock, "Main", message);

    public static void Modules(string message) => Write(ModulesSinkLock, "Modules", message);

    /// <summary>Dump-on-demand: the bounded ring, oldest first.</summary>
    public static string Full()
    {
        lock (QueueLock)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < _ringCount; i++)
            {
                sb.AppendLine(Ring[(_ringStart + i) % RingCapacity]);
            }
            return sb.ToString();
        }
    }

    public static void Finish()
    {
        lock (QueueLock)
        {
            _finished = true;
            Signal.Set();
        }
        _writer?.Join(TimeSpan.FromSeconds(2));
        lock (QueueLock)
        {
            _file?.Dispose();
            _file = null;
            _writer = null;
            _finished = false;
            Started = false;
        }
    }

    private static void Write(object sinkLock, string sink, string message)
    {
        if (_writing) return; // reentrancy guard: never recurse from our own failure paths
        _writing = true;
        try
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{sink}] {message}";
            lock (sinkLock)
            {
                lock (QueueLock)
                {
                    RingAdd(line);
                    Pending.Enqueue(line);
                }
            }
            EnsureWriter();
            Signal.Set();
        }
        finally
        {
            _writing = false;
        }
    }

    private static void RingAdd(string line)
    {
        var index = (_ringStart + _ringCount) % RingCapacity;
        if (_ringCount == RingCapacity)
        {
            _ringStart = (_ringStart + 1) % RingCapacity;
        }
        else
        {
            _ringCount++;
        }
        Ring[index] = line;
    }

    private static void EnsureWriter()
    {
        if (_writer is not null) return;
        lock (QueueLock)
        {
            if (_writer is not null) return;
            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "Citadel.Log" };
            _writer.Start();
        }
    }

    private static void WriterLoop()
    {
        while (true)
        {
            Signal.Wait();
            Signal.Reset();

            // Drain everything writable; entries staged before Start stay
            // queued until the file exists.
            while (TryDrainBatch()) { }

            lock (QueueLock)
            {
                if (_finished) break;
            }
        }
    }

    private static bool TryDrainBatch()
    {
        List<string> batch;
        StreamWriter file;
        lock (QueueLock)
        {
            if (_file is null || Pending.Count == 0) return false;
            file = _file;
            batch = new List<string>(Pending.Count);
            while (Pending.Count > 0) batch.Add(Pending.Dequeue());
        }

        try
        {
            foreach (var line in batch) file.WriteLine(line);
            file.Flush();
        }
        catch
        {
            // A dead file must not take logging down; the ring still holds it.
        }
        return true;
    }

    private static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Citadel");
}
