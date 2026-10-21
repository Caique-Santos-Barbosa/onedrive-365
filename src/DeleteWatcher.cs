namespace HdtDrive;

/// Exclusões feitas pelo usuário no M: (Explorer, apps). A nuvem joga o item na Lixeira do OneDrive (primeiro estágio);
/// aqui só juntamos as exclusões de alguns segundos para avisar uma vez, como o OneDrive faz.
/// Avisos de mudança vindos da nuvem não passam por aqui: o WinFsp só avisa o que foi feito através da unidade.
sealed class DeleteWatcher : IDisposable
{
    readonly FileSystemWatcher _watcher;
    readonly System.Threading.Timer _timer;
    readonly Action<IReadOnlyList<string>> _deleted;
    readonly object _lock = new();
    readonly HashSet<string> _due = new(StringComparer.OrdinalIgnoreCase);

    public DeleteWatcher(string root, Action<IReadOnlyList<string>> deleted)
    {
        _deleted = deleted;
        _timer = new System.Threading.Timer(_ => Flush());
        _watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
        };
        _watcher.Deleted += (_, e) => Add(e.FullPath);
        _watcher.Error += (_, e) => Log.Error("Vigia de exclusões", e.GetException());
        _watcher.EnableRaisingEvents = true;
    }

    void Add(string path)
    {
        if (IsTemporary(Path.GetFileName(path))) return;
        lock (_lock) _due.Add(path);
        _timer.Change(2500, Timeout.Infinite);   // junta a exclusão de vários itens num aviso só
    }

    void Flush()
    {
        string[] due;
        lock (_lock) { due = _due.ToArray(); _due.Clear(); }
        // Salvar no Word/Excel apaga e recria o arquivo: se ele voltou, não foi exclusão
        var gone = due.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList();
        // Pasta excluída: os itens dentro dela não contam separado
        gone = gone.Where(p => !gone.Any(d => p.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase))).ToList();
        if (gone.Count > 0) _deleted(gone);
    }

    /// Arquivos de trabalho de apps (Office, navegadores, editores) e os do próprio app
    static bool IsTemporary(string name) =>
        name.StartsWith('~') || name.StartsWith(".~") || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(name).ToLowerInvariant() is ".tmp" or ".temp" or ".crdownload" or ".part" or ".partial" or ".swp";

    public void Dispose()
    {
        _watcher.Dispose();
        _timer.Dispose();
    }
}
