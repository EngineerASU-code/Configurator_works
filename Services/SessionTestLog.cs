using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Configurator;

/// <summary>Session diagnostics persisted incrementally so useful evidence survives a crash.</summary>
public sealed class SessionTestLog : IDisposable
{
    public static SessionTestLog Current { get; set; }
    public static void Trace(string category, string message) => Current?.Add(category, message);

    private readonly object _sync = new();
    private const int MaxMemoryLines = 5000;      // the on-disk log keeps everything; memory keeps the tail
    private const int MaxMessageChars = 4000;
    private const int KeepLogFiles = 20;
    private static readonly TimeSpan MaxLogAge = TimeSpan.FromDays(14);
    private readonly List<string> _lines = new();
    private readonly DateTime _started = DateTime.Now;
    private readonly FileStream _stream;
    private readonly StreamWriter _writer;
    public string LogFilePath { get; }

    public SessionTestLog()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Configurator", "Diagnostics");
        Directory.CreateDirectory(directory);
        CleanupOldLogs(directory);
        LogFilePath = Path.Combine(directory, $"Configurator_{DateTime.Now:yyyyMMdd_HHmmss}_{Environment.ProcessId}.log");
        try
        {
            _stream = new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.WriteThrough);
            _writer = new StreamWriter(_stream, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SessionTestLog] Could not open persistent diagnostic log: {ex}");
        }
        Add("СЕССИЯ", $"Начата диагностическая запись; file='{LogFilePath}'");
    }

    /// <summary>Keeps the newest log files and drops the ones older than two weeks. Best effort.</summary>
    private static void CleanupOldLogs(string directory)
    {
        try
        {
            var files = new DirectoryInfo(directory).GetFiles("Configurator_*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            for (int i = 0; i < files.Count; i++)
            {
                if (i >= KeepLogFiles || DateTime.UtcNow - files[i].LastWriteTimeUtc > MaxLogAge)
                {
                    try { files[i].Delete(); } catch { /* in use by another running instance */ }
                }
            }
        }
        catch { /* diagnostics must never block startup */ }
    }

    public void Add(string category, string message)
    {
        var user = UserAuthorizationService.CurrentUser?.UserName ?? "не авторизован";
        string text = Sanitize(message);
        if (text.Length > MaxMessageChars) text = text[..MaxMessageChars] + "…";
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {category} | Пользователь: {user} | {text}";
        lock (_sync)
        {
            _lines.Add(line);
            if (_lines.Count > MaxMemoryLines) _lines.RemoveRange(0, _lines.Count - MaxMemoryLines);
            try
            {
                if (_writer != null && _stream != null)
                {
                    _writer.WriteLine(line);
                    _writer.Flush();
                    _stream.Flush(flushToDisk: true);
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[SessionTestLog] Persist failed: {ex}"); }
        }
    }

    public void Export(string path)
    {
        string[] snapshot;
        lock (_sync) snapshot = _lines.ToArray();
        var header = new[]
        {
            "Configurator — диагностический лог тестирования",
            $"Версия: {typeof(SessionTestLog).Assembly.GetName().Version?.ToString(3) ?? "unknown"}",
            $"Начало сессии: {_started:yyyy-MM-dd HH:mm:ss}",
            $"Экспорт: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"Автоматический лог: {LogFilePath}",
            new string('-', 90)
        };
        File.WriteAllLines(path, header.Concat(snapshot), new UTF8Encoding(true));
    }

    public void Clear()
    {
        lock (_sync) _lines.Clear();
        Add("ДИАГНОСТИКА", "Буфер текущей сессии очищен; файл автологирования не удалён.");
    }

    public void Dispose()
    {
        lock (_sync)
        {
            try { _writer?.Flush(); _stream?.Flush(flushToDisk: true); } catch (Exception ex) { Debug.WriteLine($"[SessionTestLog] Final flush failed: {ex}"); }
            try { _writer?.Dispose(); } catch (Exception ex) { Debug.WriteLine($"[SessionTestLog] Dispose writer failed: {ex}"); }
            // StreamWriter owns the FileStream.
        }
    }

    private static string Sanitize(string value) => (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
}
