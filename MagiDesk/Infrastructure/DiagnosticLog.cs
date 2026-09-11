using System.IO;
using System.Text;
using System.Threading.Channels;

namespace MagiDesk.Infrastructure;

internal static class DiagnosticLog
{
    // Explicit opt-in; browsing information and per-mouse-event traces stay off by default.
    public static bool Verbose { get; } = Environment.GetEnvironmentVariable("MAGIDESK_DIAGNOSTICS") == "1";
    private static readonly BoundedLogWriter Writer = new(Path.Combine(Path.GetTempPath(), "magidesk.log"), verbose: Verbose);
    public static void Write(string text) => Writer.TryWrite(() => text);
    public static void Write(Func<string> text) => Writer.TryWrite(text);
    public static void WriteSensitive(string text) => Writer.TryWrite(() => text, sensitive: true);
}

/// <summary>One writer, bounded queue, bounded records and one rotated backup.</summary>
internal sealed class BoundedLogWriter : IAsyncDisposable
{
    private readonly Channel<Func<string>> _queue;
    private readonly Task _worker;
    private readonly string _path;
    private readonly int _maxBytes;
    private readonly bool _verbose;
    public BoundedLogWriter(string path, int maxBytes = 2 * 1024 * 1024, int capacity = 128, bool verbose = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 3);
        _path = path; _maxBytes = maxBytes; _verbose = verbose;
        _queue = Channel.CreateBounded<Func<string>>(new BoundedChannelOptions(capacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _worker = Task.Run(ConsumeAsync);
    }
    // TryWrite never waits for disk or for queue space. Drop diagnostics on overload.
    public bool TryWrite(Func<string> text, bool sensitive = false)
        => (!sensitive || _verbose) && _queue.Writer.TryWrite(text);
    private async Task ConsumeAsync()
    {
        await foreach (var makeText in _queue.Reader.ReadAllAsync())
        {
            try
            {
                var text = makeText();
                // UTF-8 uses at most 3 bytes per UTF-16 code unit.
                if (text.Length > _maxBytes / 3) text = text[..(_maxBytes / 3)];
                int bytes = Encoding.UTF8.GetByteCount(text);
                if (File.Exists(_path) && new FileInfo(_path).Length + bytes > _maxBytes)
                    File.Move(_path, _path + ".1", overwrite: true);
                File.AppendAllText(_path, text, new UTF8Encoding(false));
            }
            catch { /* Diagnostics must not break normal operation. */ }
        }
    }
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }
}
