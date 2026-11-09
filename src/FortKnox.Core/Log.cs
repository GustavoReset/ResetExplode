using System.Text;

namespace FortKnox.Core;

public enum LogLevel { Debug, Info, Warn, Error }

public interface ILog
{
    void Write(LogLevel level, string message, Exception? ex = null);
    void Info(string message) => Write(LogLevel.Info, message);
    void Warn(string message, Exception? ex = null) => Write(LogLevel.Warn, message, ex);
    void Error(string message, Exception? ex = null) => Write(LogLevel.Error, message, ex);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public void Write(LogLevel level, string message, Exception? ex = null) { }
}

/// <summary>
/// Log local em arquivo, um por dia, com limite de tamanho e de dias guardados. Nunca é enviado para lugar nenhum.
/// Quem loga não escreve segredos (senhas, chaves); o relatório de erro exportado remove o caminho do perfil.
/// </summary>
public sealed class FileLog : ILog, IDisposable
{
    private readonly string _folder;
    private readonly long _maxBytesPerDay;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private DateOnly _day;
    private long _written;

    public FileLog(string folder, long maxBytesPerDay = 5 * 1024 * 1024, int keepDays = 14)
    {
        _folder = folder;
        _maxBytesPerDay = maxBytesPerDay;
        Directory.CreateDirectory(folder);
        Prune(keepDays);
    }

    public string CurrentFile => Path.Combine(_folder, $"fortknox-{_day:yyyyMMdd}.log");

    public void Write(LogLevel level, string message, Exception? ex = null)
    {
        var now = DateTime.Now;
        var line = new StringBuilder()
            .Append(now.ToString("HH:mm:ss.fff")).Append(' ').Append(level.ToString().ToUpperInvariant().PadRight(5)).Append(' ')
            .Append(message);
        if (ex != null) line.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
        lock (_gate)
        {
            try
            {
                var today = DateOnly.FromDateTime(now);
                if (_writer == null || today != _day) Open(today);
                if (_written > _maxBytesPerDay) return; // limite do dia: descarta em vez de encher o disco
                _writer!.WriteLine(line.ToString());
                _written += line.Length + 2;
            }
            catch (IOException) { /* log nunca derruba o app */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void Open(DateOnly day)
    {
        _writer?.Dispose();
        _day = day;
        var fs = new FileStream(CurrentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _written = fs.Length;
        _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
    }

    private void Prune(int keepDays)
    {
        var limit = DateTime.Now.AddDays(-keepDays);
        foreach (var f in Directory.EnumerateFiles(_folder, "fortknox-*.log"))
            try { if (File.GetLastWriteTime(f) < limit) File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose() { lock (_gate) { _writer?.Dispose(); _writer = null; } }
}
