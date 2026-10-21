using System.Runtime.InteropServices;
using System.Text.Json;

namespace HdtDrive;

/// Vigia os metadados do cache do rclone (cache\vfsMeta\m365), só arquivos locais:
/// arquivo que termina de baixar vira "no PC" no Explorer na hora e entra na atividade;
/// arquivo que sai do cache volta a mostrar a nuvem.
sealed class CacheWatcher : IDisposable
{
    readonly string _root;
    readonly Func<string> _letter;
    readonly Action<string, long> _downloaded;   // caminho relativo à unidade, tamanho
    readonly FileSystemWatcher _watcher;
    readonly System.Threading.Timer _timer;
    readonly object _lock = new();
    readonly HashSet<string> _due = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _complete = new(StringComparer.OrdinalIgnoreCase);   // já baixados por inteiro
    volatile bool _seeded;

    public CacheWatcher(Func<string> letter, Action<string, long> downloaded)
    {
        _root = Path.Combine(Paths.CacheDir, "vfsMeta", "m365");
        _letter = letter;
        _downloaded = downloaded;
        Directory.CreateDirectory(_root);
        _timer = new System.Threading.Timer(_ => Flush());
        _watcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Created += (_, e) => Touch(e.FullPath);
        _watcher.Changed += (_, e) => Touch(e.FullPath);
        _watcher.Deleted += (_, e) => Touch(e.FullPath);
        _watcher.Renamed += (_, e) => { Touch(e.OldFullPath); Touch(e.FullPath); };
        _watcher.Error += (_, e) => Log.Error("Vigia do cache", e.GetException());
        _watcher.EnableRaisingEvents = true;
        // O que já estava baixado antes do app abrir não é "baixado agora"
        Task.Run(Seed);
    }

    void Seed()
    {
        try
        {
            foreach (var meta in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                try { if (Read(meta) is { Complete: true }) lock (_lock) _complete.Add(Rel(meta)); }
                catch (Exception ex) when (ex is IOException or JsonException) { }   // metadado sendo gravado: o vigia pega depois
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Leitura inicial do cache", ex); }
        _seeded = true;
    }

    void Touch(string fullPath)
    {
        if (Path.GetFileName(fullPath).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) return;
        lock (_lock) _due.Add(fullPath);
        _timer.Change(700, Timeout.Infinite);   // o rclone grava o metadado várias vezes seguidas
    }

    void Flush()
    {
        string[] due;
        lock (_lock) { due = _due.ToArray(); _due.Clear(); }
        var retry = false;
        foreach (var meta in due)
        {
            try
            {
                if (Directory.Exists(meta)) continue;
                var rel = Rel(meta);
                var m = File.Exists(meta) ? Read(meta) : null;
                bool report;
                lock (_lock)
                {
                    if (m is not { Complete: true }) { _complete.Remove(rel); report = false; }
                    // Só a passagem para "baixado inteiro" conta: o rclone regrava o metadado a cada acesso
                    else report = _complete.Add(rel) && _seeded && !ExplorerReads(rel);
                }
                SHChangeNotify(0x00002000 /* SHCNE_UPDATEITEM */, 0x0005 /* SHCNF_PATHW */, Path.Combine(_letter() + "\\", rel), IntPtr.Zero);
                if (report) _downloaded(rel, m!.Size);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // metadado sendo gravado pelo rclone: tenta de novo daqui a pouco
                lock (_lock) _due.Add(meta);
                retry = true;
            }
            catch (Exception ex) { Log.Error($"Vigia do cache: {meta}", ex); }
        }
        if (retry) _timer.Change(1000, Timeout.Infinite);
    }

    /// Arquivos que o próprio Explorer lê para desenhar ícones: baixam sozinhos, não são "abertos" pelo usuário
    static bool ExplorerReads(string rel) =>
        Path.GetExtension(rel).ToLowerInvariant() is ".lnk" or ".url" or ".ico" or ".ini" ||
        Path.GetFileName(rel).Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase);

    string Rel(string meta) => Path.GetRelativePath(_root, meta);

    sealed record Meta(long Size, bool Complete);

    /// Baixado = não é escrita local pendente (Dirty) e os trechos em cache cobrem o arquivo inteiro.
    static Meta? Read(string meta)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(meta));
        var r = doc.RootElement;
        var size = r.TryGetProperty("Size", out var s) ? s.GetInt64() : 0;
        var dirty = r.TryGetProperty("Dirty", out var d) && d.ValueKind == JsonValueKind.True;
        long covered = 0;
        if (r.TryGetProperty("Rs", out var rs) && rs.ValueKind == JsonValueKind.Array)
            foreach (var range in rs.EnumerateArray())
                covered += range.TryGetProperty("Size", out var rsz) ? rsz.GetInt64() : 0;
        return new Meta(size, !dirty && covered >= size);
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _timer.Dispose();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);
}
