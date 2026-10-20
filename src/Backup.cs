using System.Globalization;
using System.Runtime.InteropServices;

namespace HdtDrive;

public sealed class BackupFolder
{
    public string Id { get; set; } = "";          // desktop, documents, ... ou custom-xxxx
    public string? LocalPath { get; set; }        // só pastas escolhidas pelo usuário
    public string? RemoteName { get; set; }       // idem: caminho dentro de Meu OneDrive
    public bool Enabled { get; set; }
    public bool Initialized { get; set; }         // primeiro sync (--resync) já feito
    /// Subpastas fora do backup (relativas à pasta, com '/'), escolhidas na ⚙ "Escolher pastas".
    public List<string> Excluded { get; set; } = new();
    public DateTime? LastSync { get; set; }
    public string? LastError { get; set; }
}

/// Backup das pastas do PC para o OneDrive (mesmos nomes do "backup de pastas" da Microsoft).
/// Só envia: nunca baixa nada para o PC e nunca apaga nada na nuvem (apagar no PC não apaga lá).
/// Mudanças detectadas na pasta sobem em ~1 min (só os arquivos alterados); a cada 6 h uma passada completa.
sealed class BackupManager : IDisposable
{
    public sealed record Known(string Id, string LocalPath, string RemoteName);

    // Cópia fiel de tudo (inclusive node_modules, .git...). Fora só o que não dá para copiar de forma confiável:
    // trava de documento aberto do Office (~$...) e downloads ainda em andamento (incompletos).
    static readonly string[] Excludes = { "~$*", "*.crdownload", "*.part", "*.partial" };

    readonly Settings _settings;
    readonly Action<string> _onStatus;

    /// Chamado ao fim de cada envio: pasta e erro (null = deu certo).
    public Action<Known, string?>? Finished;

    /// Andamento do envio em curso (a cada ~2 s); null quando termina.
    public Action<Progress?>? ProgressChanged;

    /// Cada arquivo que terminou de subir: pasta, caminho relativo, tamanho.
    public Action<Known, string, long>? FileUploaded;

    public sealed record Transfer(string Name, long Size, int Percent);

    public sealed record Progress(Known Folder, long Bytes, long TotalBytes, long Files, long TotalFiles,
        long Checks, double Speed, double? Eta, IReadOnlyList<Transfer> Transferring);

    bool _paused;

    /// Pausado: nenhum envio novo começa; ao retomar, uma passada completa põe tudo em dia.
    public bool Paused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value;
            if (value) CancelAll();          // pausar para o envio em curso na hora
            else QueueAll(full: true);       // retomar recomeça já (o que subiu é pulado)
            _onStatus(Summary());
        }
    }

    /// Nome como o Windows mostra (Área de Trabalho, Documentos...) ou o nome da pasta escolhida.
    public static string DisplayName(Known k)
    {
        if (k.Id.StartsWith("custom-")) return new DirectoryInfo(k.LocalPath).Name;
        var info = new SHFILEINFO();
        SHGetFileInfo(k.LocalPath, 0, ref info, (uint)Marshal.SizeOf(info), 0x200 /* SHGFI_DISPLAYNAME */);
        return string.IsNullOrEmpty(info.szDisplayName) ? k.RemoteName : info.szDisplayName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly Dictionary<string, FileSystemWatcher> _watchers = new();
    readonly Dictionary<string, System.Threading.Timer> _debounce = new();
    readonly System.Threading.Timer _periodic;
    readonly CancellationTokenSource _cancel = new();
    readonly Dictionary<string, CancellationTokenSource> _runCancel = new();   // envio em curso, por pasta
    readonly Dictionary<string, HashSet<string>> _changed = new();   // por pasta: arquivos alterados desde o último envio
    readonly HashSet<string> _needsFullScan = new();
    string? _running;

    public BackupManager(Settings settings, Action<string> onStatus)
    {
        _settings = settings;
        _onStatus = onStatus;
        _periodic = new System.Threading.Timer(_ => QueueAll(full: true), null, TimeSpan.FromMinutes(2), TimeSpan.FromHours(6));
    }

    public static IReadOnlyList<Known> KnownFolders()
    {
        var names = RemoteNames(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        return new[]
        {
            new Known("desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), names[0]),
            new Known("documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), names[1]),
            new Known("pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), names[2]),
            new Known("downloads", DownloadsPath(), names[3]),
            new Known("music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), names[4]),
            new Known("videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), names[5]),
        };
    }

    // Mesmos nomes que o OneDrive usa ao fazer backup das pastas no idioma do Windows
    static string[] RemoteNames(string lang) => lang switch
    {
        "pt" => new[] { "Área de Trabalho", "Documentos", "Imagens", "Downloads", "Músicas", "Vídeos" },
        "es" => new[] { "Escritorio", "Documentos", "Imágenes", "Descargas", "Música", "Vídeos" },
        _ => new[] { "Desktop", "Documents", "Pictures", "Downloads", "Music", "Videos" },
    };

    /// Pastas do Windows + pastas que o usuário escolheu.
    public IReadOnlyList<Known> AllFolders()
    {
        lock (_settings)
            return KnownFolders().Concat(_settings.Backups
                    .Where(b => b.LocalPath != null && b.RemoteName != null)
                    .Select(b => new Known(b.Id, b.LocalPath!, b.RemoteName!)))
                .ToList();
    }

    public BackupFolder State(string id)
    {
        lock (_settings)
        {
            var f = _settings.Backups.FirstOrDefault(b => b.Id == id);
            if (f == null) { f = new BackupFolder { Id = id }; _settings.Backups.Add(f); }
            return f;
        }
    }

    /// Pasta qualquer do PC: vai para Meu OneDrive\Computadores\<PC>\<pasta>, como o "backup de computador" do Google.
    public Known AddCustom(string localPath)
    {
        var existing = AllFolders().FirstOrDefault(k => string.Equals(k.LocalPath.TrimEnd('\\'), localPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;
        var computers = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch { "pt" => "Computadores", "es" => "Equipos", _ => "Computers" };
        var name = Graph.Sanitize(new DirectoryInfo(localPath).Name);
        var f = new BackupFolder
        {
            Id = "custom-" + Guid.NewGuid().ToString("N")[..8],
            LocalPath = localPath,
            RemoteName = $"{computers}/{Graph.Sanitize(Environment.MachineName)}/{name}",
        };
        lock (_settings) _settings.Backups.Add(f);
        _settings.Save();
        return new Known(f.Id, f.LocalPath, f.RemoteName);
    }

    public void RemoveCustom(string id)
    {
        SetEnabled(id, false);
        lock (_settings) _settings.Backups.RemoveAll(b => b.Id == id && b.LocalPath != null);
        _settings.Save();
    }

    public string? Running => _running;

    public void Start()
    {
        foreach (var k in AllFolders())
            if (State(k.Id).Enabled) Watch(k);
    }

    public void SetEnabled(string id, bool enabled)
    {
        var k = AllFolders().First(x => x.Id == id);
        var s = State(id);
        if (s.Enabled != enabled) Log.Info($"Backup {id} {(enabled ? "ligado" : "desligado")} pelo usuário");
        s.Enabled = enabled;
        s.LastError = null;
        _settings.Save();
        if (enabled) { Watch(k); MarkFull(k.Id); Queue(k, TimeSpan.Zero); }
        else { Unwatch(id); CancelRun(id); }   // desligar para o envio em andamento na hora
    }

    /// Define as subpastas fora do backup; reinicia o envio da pasta com o filtro novo.
    public void SetExclusions(string id, IEnumerable<string> excluded)
    {
        var s = State(id);
        s.Excluded = excluded.Select(e => e.Replace('\\', '/').Trim('/')).Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _settings.Save();
        Log.Info($"Backup {id}: {s.Excluded.Count} subpasta(s) fora do backup");
        CancelRun(id);
        if (s.Enabled) { MarkFull(id); Queue(AllFolders().First(x => x.Id == id), TimeSpan.FromSeconds(2)); }
    }

    /// Horário permitido para enviar (null = sempre). Fora dele nenhum envio começa.
    public Func<bool>? Allowed;

    /// Interrompe todos os envios em curso (pausa, fim do horário).
    public void CancelAll()
    {
        lock (_runCancel) foreach (var c in _runCancel.Values) c.Cancel();
    }

    /// Começa já a sincronizar todas as pastas ligadas (início do horário, botão Retomar).
    public void StartNow() => QueueAll(full: true);

    void CancelRun(string id)
    {
        lock (_runCancel)
            if (_runCancel.TryGetValue(id, out var cts)) cts.Cancel();
    }

    bool InExcludedFolder(string id, string relative)
    {
        var rel = relative.Replace('\\', '/');
        return State(id).Excluded.Any(x => rel.Equals(x, StringComparison.OrdinalIgnoreCase) || rel.StartsWith(x + "/", StringComparison.OrdinalIgnoreCase));
    }

    void Watch(Known k)
    {
        if (_watchers.ContainsKey(k.Id) || !Directory.Exists(k.LocalPath)) return;
        var w = new FileSystemWatcher(k.LocalPath)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        FileSystemEventHandler changed = (_, e) =>
        {
            if (!IsExcluded(e.FullPath) && !InExcludedFolder(k.Id, Path.GetRelativePath(k.LocalPath, e.FullPath))) Changed(k, e.FullPath);
        };
        w.Created += changed; w.Changed += changed;               // apagar no PC não apaga na nuvem: Deleted é ignorado
        w.Renamed += (_, e) => { if (!IsExcluded(e.FullPath)) Changed(k, e.FullPath); };
        w.Error += (_, _) => { MarkFull(k.Id); Queue(k, TimeSpan.FromSeconds(60)); };   // buffer estourou: passada completa
        w.EnableRaisingEvents = true;
        _watchers[k.Id] = w;
    }

    void Unwatch(string id)
    {
        if (_watchers.Remove(id, out var w)) w.Dispose();
    }

    static bool IsExcluded(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("~$")
            || new[] { ".crdownload", ".part", ".partial" }.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    void Changed(Known k, string fullPath)
    {
        // Pasta criada/movida traz arquivos que o watcher não lista um a um: passada completa
        if (Directory.Exists(fullPath)) MarkFull(k.Id);
        else lock (_changed)
        {
            if (!_changed.TryGetValue(k.Id, out var set)) _changed[k.Id] = set = new(StringComparer.OrdinalIgnoreCase);
            set.Add(Path.GetRelativePath(k.LocalPath, fullPath));
        }
        Queue(k, TimeSpan.FromSeconds(60));
    }

    void MarkFull(string id)
    {
        lock (_changed) _needsFullScan.Add(id);
    }

    void QueueAll(bool full)
    {
        foreach (var k in AllFolders())
            if (State(k.Id).Enabled)
            {
                if (full) MarkFull(k.Id);
                Queue(k, TimeSpan.Zero);
            }
    }

    /// Agenda um sync; mudanças em sequência dentro da janela viram um só.
    void Queue(Known k, TimeSpan delay)
    {
        lock (_debounce)
        {
            if (_debounce.TryGetValue(k.Id, out var t)) t.Change(delay, Timeout.InfiniteTimeSpan);
            else _debounce[k.Id] = new System.Threading.Timer(_ => _ = Run(k), null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    async Task Run(Known k)
    {
        await _gate.WaitAsync();
        try
        {
            var s = State(k.Id);
            if (!s.Enabled || _paused || Allowed?.Invoke() == false) return;
            _running = k.RemoteName;
            _onStatus($"Backup: enviando {k.RemoteName}" + (s.Initialized ? "…" : " (primeira vez pode demorar)…"));

            // Pega o que mudou até aqui; o que mudar durante o envio entra na próxima rodada
            HashSet<string>? files;
            bool full;
            lock (_changed)
            {
                full = !s.Initialized || _needsFullScan.Remove(k.Id);
                _changed.Remove(k.Id, out files);
            }
            if (!full && (files == null || files.Count == 0)) return;
            if (!full && files!.Count > 5000) full = true;

            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
            lock (_runCancel) _runCancel[k.Id] = runCts;
            var (code, output) = await Upload(k, full ? null : files, runCts.Token);
            lock (_runCancel) _runCancel.Remove(k.Id);
            ProgressChanged?.Invoke(null);
            if (runCts.IsCancellationRequested && !_cancel.IsCancellationRequested)
            {
                Log.Info($"Backup {k.Id}: envio interrompido (pasta desligada ou filtro mudou)");
                return;
            }
            if (code == 0)
            {
                s.Initialized = true;
                s.LastSync = DateTime.Now;
                s.LastError = null;
            }
            else if (!_cancel.IsCancellationRequested)
            {
                s.LastError = string.IsNullOrEmpty(output) ? $"rclone saiu com código {code}" : output;
                Log.Error($"Backup {k.Id} falhou ({code}): {s.LastError}");
                if (files != null) lock (_changed) _needsFullScan.Add(k.Id);   // tenta de novo na próxima, completo
            }
            _settings.Save();
            if (!_cancel.IsCancellationRequested) Finished?.Invoke(k, code == 0 ? null : s.LastError);
        }
        catch (Exception ex)
        {
            Log.Error($"Backup {k.Id}", ex);
            State(k.Id).LastError = ex.Message;
        }
        finally
        {
            _running = null;
            _gate.Release();
            _onStatus(Summary());
        }
    }

    /// Envia PC → nuvem. files = só esses arquivos (relativos); null = pasta inteira.
    async Task<(int, string)> Upload(Known k, HashSet<string>? files, CancellationToken cancel)
    {
        Directory.CreateDirectory(Paths.BisyncDir);
        var filters = Path.Combine(Paths.BisyncDir, $"filters-{k.Id}.txt");
        // Subpastas desmarcadas na ⚙ ("/" no início = a partir da raiz da pasta) + arquivos temporários
        var content = string.Join("\n", State(k.Id).Excluded.Select(x => $"- /{EscapeFilter(x)}/**").Concat(Excludes.Select(e => "- " + e))) + "\n";
        if (!File.Exists(filters) || File.ReadAllText(filters) != content) File.WriteAllText(filters, content);

        var remote = $"onedrive:{k.RemoteName}";
        var args = new List<string>
        {
            "copy", k.LocalPath, remote,      // copy (não sync): nunca apaga na nuvem; origem é sempre o PC
            "--update",                       // não sobrescreve na nuvem uma versão mais nova que a do PC
            // Muitos arquivos pequenos: com 4 envios paralelos a Área de Trabalho levava horas
            "--transfers", "16", "--checkers", "16",
            // Log em JSON lido ao vivo: andamento a cada 2 s e uma linha por arquivo enviado
            "--use-json-log", "--log-level", "INFO", "--stats", "2s", "--stats-log-level", "NOTICE",
        };
        if (files != null)
        {
            var existing = files.Where(f => !IsExcluded(f) && !InExcludedFolder(k.Id, f) && File.Exists(Path.Combine(k.LocalPath, f))).ToList();
            if (existing.Count == 0) return (0, "");
            var list = Path.Combine(Paths.BisyncDir, $"changed-{k.Id}.txt");
            File.WriteAllLines(list, existing.Select(f => f.Replace('\\', '/')));
            args.AddRange(new[] { "--files-from-raw", list, "--no-traverse" });
        }
        else args.AddRange(new[] { "--filter-from", filters });
        Log.Info($"Backup {k.Id}: envio {(files == null ? "completo" : $"de {files.Count} arquivo(s)")} {k.LocalPath} -> {remote}");
        string? lastError = null;
        var code = await Rclone.RunStreaming(TimeSpan.FromHours(24), cancel, line =>
        {
            if (OnLogLine(k, line) is { } err) lastError = err;
        }, args.ToArray());
        return (code, lastError ?? "");
    }

    /// Escapa curingas do filtro do rclone em nomes de pasta (*, ?, [, ], {, }).
    static string EscapeFilter(string path) =>
        System.Text.RegularExpressions.Regex.Replace(path, @"[\*\?\[\]\{\}\\]", m => "\\" + m.Value);

    static string BackupLog => Path.Combine(Paths.LogsDir, "backup.log");

    /// Interpreta uma linha do log JSON do rclone. Devolve a mensagem se for erro.
    string? OnLogLine(Known k, string line)
    {
        if (!line.StartsWith('{')) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(line);
        var root = doc.RootElement;
        var msg = root.TryGetProperty("msg", out var m) ? m.GetString() ?? "" : "";
        var level = root.TryGetProperty("level", out var l) ? l.GetString() : "";

        if (root.TryGetProperty("stats", out var st))
        {
            long L(string n) => st.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetInt64() : 0;
            var transferring = new List<Transfer>();
            if (st.TryGetProperty("transferring", out var tr) && tr.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var t in tr.EnumerateArray())
                    transferring.Add(new Transfer(t.GetProperty("name").GetString() ?? "",
                        t.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0,
                        t.TryGetProperty("percentage", out var pc) ? pc.GetInt32() : 0));
            var eta = st.TryGetProperty("eta", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.Number ? e.GetDouble() : (double?)null;
            var speed = st.TryGetProperty("speed", out var sp) && sp.ValueKind == System.Text.Json.JsonValueKind.Number ? sp.GetDouble() : 0;
            ProgressChanged?.Invoke(new Progress(k, L("bytes"), L("totalBytes"), L("transfers"), L("totalTransfers"), L("checks"), speed, eta, transferring));
            return null;
        }

        if (msg.StartsWith("Copied (") && root.TryGetProperty("object", out var obj) && obj.GetString() is { } rel)
        {
            var local = Path.Combine(k.LocalPath, rel.Replace('/', '\\'));
            FileUploaded?.Invoke(k, rel, File.Exists(local) ? new FileInfo(local).Length : 0);
            return null;
        }

        if (level is "error" or "critical")
        {
            var obj2 = root.TryGetProperty("object", out var o) ? o.GetString() + ": " : "";
            var text = obj2 + msg;
            try { File.AppendAllText(BackupLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} ERRO {k.Id}: {text}{Environment.NewLine}"); }
            catch (IOException) { }
            return text;
        }
        return null;
    }

    public string Summary()
    {
        var enabled = _settings.Backups.Where(b => b.Enabled).ToList();
        if (enabled.Count == 0) return "";
        if (_paused) return "Backup: pausado";
        if (_running != null) return $"Backup: enviando {_running}…";
        var failed = enabled.Count(b => b.LastError != null);
        if (failed > 0) return $"Backup: {failed} pasta(s) com erro";
        var last = enabled.Where(b => b.LastSync != null).Select(b => b.LastSync!.Value).DefaultIfEmpty().Min();
        return last == default ? "Backup: aguardando" : $"Backup: atualizado às {last:HH:mm}";
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    static string DownloadsPath()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) != 0)
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        try { return Marshal.PtrToStringUni(p)!; }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    public void Dispose()
    {
        _cancel.Cancel();   // mata o rclone do envio em andamento: nada de processo órfão; retoma na próxima abertura
        _periodic.Dispose();
        foreach (var w in _watchers.Values) w.Dispose();
        lock (_debounce) foreach (var t in _debounce.Values) t.Dispose();
    }
}
