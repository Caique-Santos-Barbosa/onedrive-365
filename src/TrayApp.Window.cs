using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace HdtDrive;

/// Janela principal: estado mostrado na interface e comandos vindos dela.
sealed partial class TrayApp
{
    MainWindow? _window;
    Flyout? _flyout;
    DateTime? _pausedUntil;
    System.Windows.Forms.Timer? _pauseTimer;
    readonly Dictionary<string, string?> _fileIcons = new(StringComparer.OrdinalIgnoreCase);
    Graph.Account? _account;
    DateTime _accountLoaded;
    bool _signedOut;
    List<Library>? _libraryCatalog;   // última lista de "Adicionar bibliotecas que eu acesso"
    DateTime? _lastActivity;
    readonly List<ActivityItem> _activity = new();
    readonly List<Notice> _notices = new();
    readonly Dictionary<string, long?> _folderSizes = new();
    readonly Dictionary<string, string?> _folderIcons = new();
    Dictionary<string, long> _lastQueue = new(StringComparer.OrdinalIgnoreCase);
    string? _cloudIcon, _sharePointIcon;
    BackupManager.Progress? _backupProgress;
    DateTime _activitySaved;

    sealed record ActivityItem(DateTime Time, string Name, string Where, string? Path, long? Size, string Status);
    sealed record Notice(DateTime Time, string Level, string Title, string Detail);

    void ShowMain(string? page = null)
    {
        if (_window == null || _window.IsDisposed)
        {
            _window = new MainWindow(BuildState, (cmd, args) => _ui.BeginInvoke(async () => await OnCommand(cmd, args)));
            _window.FormClosed += (_, _) => _window = null;
            _ = LoadAccountAsync(force: false);
            MeasureFoldersAsync();
        }
        _window.BringToFront(page);
    }

    void PushState()
    {
        _window?.Push();
        _flyout?.Push();
    }

    void ToggleFlyout()
    {
        if (!_ready.Task.IsCompleted) return;
        _flyout ??= new Flyout(BuildState, (cmd, args) => _ui.BeginInvoke(async () => await OnCommand(cmd, args)));
        _flyout.Toggle();
    }

    /// Ícone do tipo de arquivo como o Explorer mostra (por extensão, sem tocar no arquivo).
    string? FileIcon(string ext)
    {
        if (_fileIcons.TryGetValue(ext, out var uri)) return uri;
        return _fileIcons[ext] = IconDataUri(ShellIntegration.FileTypeIcon(ext.Length == 0 ? ".txt" : "." + ext));
    }

    // ---------- estado ----------

    object BuildState()
    {
        _cloudIcon ??= IconDataUri(ShellIntegration.OneDriveIcon(64));
        _sharePointIcon ??= IconDataUri(ShellIntegration.SharePointIcon32());
        var folders = _backup == null ? new List<object>() : _backup.AllFolders().Select(k =>
        {
            var s = _backup.State(k.Id);
            _folderSizes.TryGetValue(k.Id, out var size);
            if (!_folderIcons.TryGetValue(k.Id, out var icon)) _folderIcons[k.Id] = icon = IconDataUri(ShellIntegration.FolderIcon(k.LocalPath));
            return (object)new
            {
                id = k.Id, name = BackupManager.DisplayName(k), path = k.LocalPath, remote = k.RemoteName.Replace('/', '\\'),
                enabled = s.Enabled, lastSync = s.LastSync, error = s.LastError, size, custom = k.Id.StartsWith("custom-"), icon,
                excluded = s.Excluded,
            };
        }).ToList();

        return new
        {
            version = Application.ProductVersion.Split('+')[0],
            letter = _settings.DriveLetter ?? "M:",
            cloudIcon = _cloudIcon,
            sharePointIcon = _sharePointIcon,
            signedOut = _signedOut,
            account = _signedOut || _account == null ? null : new
            {
                name = _account.Name, email = _account.Email, photo = _account.PhotoDataUri,
                used = _account.Used, total = _account.Total, manageUrl = _account.ManageUrl,
            },
            status = StatusInfo(),
            paused = _backup?.Paused ?? false,
            backupProgress = _backupProgress == null ? null : new
            {
                folder = BackupManager.DisplayName(_backupProgress.Folder),
                bytes = _backupProgress.Bytes, totalBytes = _backupProgress.TotalBytes,
                files = _backupProgress.Files, totalFiles = _backupProgress.TotalFiles,
                checks = _backupProgress.Checks, speed = _backupProgress.Speed, eta = _backupProgress.Eta,
                transferring = _backupProgress.Transferring.Select(t => new
                {
                    name = Path.GetFileName(t.Name), where = BackupWhere(_backupProgress.Folder, t.Name), size = t.Size, percent = t.Percent,
                }),
            },
            lastActivity = _lastActivity,
            activity = _activity.Select(a => new { time = a.Time, name = a.Name, where = a.Where, path = a.Path, size = a.Size, status = a.Status }),
            notifications = Notices(),
            folders,
            libraries = _settings.Libraries.OrderBy(l => l.Name).Select(l => new { name = l.Name, webUrl = l.WebUrl, root = l.Root }),
            interceptSync = _settings.InterceptSync,
            org = OrgName(),
            pausedUntil = _pausedUntil,
            defenderExcluded = string.Equals(_settings.DefenderExcludedLetter, _settings.DriveLetter, StringComparison.OrdinalIgnoreCase),
            schedule = new { on = _settings.ScheduleEnabled, from = _settings.ScheduleFrom, to = _settings.ScheduleTo, inside = InSchedule() },
            fileIcons = _activity.Take(60).Select(a => Path.GetExtension(a.Name).TrimStart('.').ToLowerInvariant())
                .Concat((_backupProgress?.Transferring ?? new List<BackupManager.Transfer>()).Select(t => Path.GetExtension(t.Name).TrimStart('.').ToLowerInvariant()))
                .Append("").Distinct().ToDictionary(e => e, FileIcon),
        };
    }

    object StatusInfo()
    {
        if (_signedOut) return new { kind = "error", title = "Desconectado", detail = "Entre com a sua conta Microsoft para ver seus arquivos." };
        var uploading = _lastQueue.Count;
        var backupRunning = _backup?.Running;
        if (uploading > 0 || backupRunning != null)
            return new
            {
                kind = "sync",
                title = uploading > 0 ? $"Enviando {uploading} arquivo(s)" : $"Fazendo backup de {backupRunning}",
                detail = _backupProgress is { } bp && bp.TotalFiles > 0
                    ? $"{bp.Files:N0} de {bp.TotalFiles:N0} arquivos enviados ({SizeText(bp.Bytes)} de {SizeText(bp.TotalBytes)})."
                    : _backupProgress is { Checks: > 0 } bc ? $"Comparando com a nuvem: {bc.Checks:N0} arquivos verificados."
                    : "Pode continuar trabalhando: o envio segue em segundo plano.",
            };
        if (_status.StartsWith("Unidade presa") || _status.StartsWith("Desconectado") || _status.StartsWith("Erro"))
            return new { kind = "error", title = "Com problemas", detail = _status };
        if (_backup?.Paused == true)
            return new { kind = "paused", title = "Sincronização pausada", detail = "A unidade continua funcionando. O backup das pastas do PC volta quando você retomar." };
        if (!InSchedule())
            return new { kind = "paused", title = "Aguardando o horário", detail = $"O backup das pastas do PC volta às {_settings.ScheduleFrom}." };
        return new
        {
            kind = "ok",
            title = "Tudo atualizado",
            detail = _lastActivity == null ? $"Seus arquivos estão em {_settings.DriveLetter}\\." : $"Última sincronização {Ago(_lastActivity.Value)}.",
        };
    }

    IEnumerable<object> Notices()
    {
        var list = new List<Notice>(_notices);
        if (_backup != null)
            foreach (var k in _backup.AllFolders())
                if (_backup.State(k.Id) is { Enabled: true, LastError: { } err })
                    list.Add(new Notice(DateTime.Now, "error", $"O backup de {BackupManager.DisplayName(k)} falhou", err));
        return list.OrderByDescending(n => n.Time).Select(n => new { time = n.Time, level = n.Level, title = n.Title, detail = n.Detail });
    }

    void AddNotice(string level, string title, string detail)
    {
        _notices.RemoveAll(n => n.Title == title);
        _notices.Add(new Notice(DateTime.Now, level, title, detail));
        PushState();
    }

    static string ActivityFile => Path.Combine(Paths.AppDir, "activity.json");

    void AddActivity(ActivityItem item)
    {
        if (item.Path != null) _activity.RemoveAll(a => a.Path == item.Path);
        _activity.Insert(0, item);
        if (_activity.Count > 200) _activity.RemoveRange(200, _activity.Count - 200);
        _lastActivity = item.Time;
        if (DateTime.Now - _activitySaved > TimeSpan.FromSeconds(5)) { _activitySaved = DateTime.Now; SaveActivity(); }
    }

    /// Histórico sobrevive a reinícios ("sincronizado há 38 min" depois de reabrir o app).
    void LoadActivity()
    {
        try
        {
            if (!File.Exists(ActivityFile)) return;
            var items = JsonSerializer.Deserialize<List<ActivityItem>>(File.ReadAllText(ActivityFile));
            if (items == null) return;
            // Quem estava "enviando" quando o app fechou não tem status confiável: o cache retoma o envio
            _activity.AddRange(items.Where(i => i.Status != "uploading"));
            _lastActivity = _activity.FirstOrDefault()?.Time;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { Log.Error("activity.json ilegível", ex); }
    }

    void SaveActivity()
    {
        try
        {
            var tmp = ActivityFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_activity));
            File.Move(tmp, ActivityFile, overwrite: true);
        }
        catch (IOException ex) { Log.Error("Não salvei activity.json", ex); }
    }

    /// Diferença da fila de upload: o que entrou está "Enviando", o que saiu foi "Enviado".
    void TrackQueue(IReadOnlyCollection<Rclone.QueueItem> queue)
    {
        var now = queue.ToDictionary(q => q.Name.Replace('/', '\\'), q => q.Size, StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var (rel, size) in now)
            if (!_lastQueue.ContainsKey(rel)) { AddActivity(Item(rel, size, "uploading")); changed = true; }
        foreach (var (rel, size) in _lastQueue)
            if (!now.ContainsKey(rel)) { AddActivity(Item(rel, size, "done")); changed = true; }
        _lastQueue = now;
        if (changed) PushState();

        ActivityItem Item(string rel, long size, string status) => new(DateTime.Now, Path.GetFileName(rel),
            Path.GetDirectoryName(Path.Combine(_settings.DriveLetter + "\\", rel)) ?? "", Path.Combine(_settings.DriveLetter + "\\", rel), size, status);
    }

    void OnBackupProgress(BackupManager.Progress? p)
    {
        _backupProgress = p;
        UpdateTrayIcon();
        PushState();
    }

    void OnBackupFile(BackupManager.Known k, string rel, long size)
    {
        AddActivity(new ActivityItem(DateTime.Now, Path.GetFileName(rel), BackupWhere(k, rel), Path.Combine(k.LocalPath, rel.Replace('/', '\\')), size, "done"));
        // A tela é atualizada pelo próximo andamento (a cada 2 s), não a cada arquivo
    }

    static string BackupWhere(BackupManager.Known k, string rel)
    {
        var dir = Path.GetDirectoryName(rel.Replace('/', '\\'));
        return $"{BackupManager.DisplayName(k)}{(string.IsNullOrEmpty(dir) ? "" : "\\" + dir)} → {Brand.OneDriveFolder}";
    }

    static string SizeText(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : b >= 1L << 20 ? $"{b >> 20} MB" : $"{Math.Max(1, b >> 10)} KB";

    void OnBackupFinished(BackupManager.Known k, string? error)
    {
        SaveActivity();
        var name = BackupManager.DisplayName(k);
        AddActivity(new ActivityItem(DateTime.Now, $"Backup de {name}", error ?? $"{k.LocalPath} → {Brand.OneDriveFolder}\\{k.RemoteName.Replace('/', '\\')}",
            Path.Combine(_settings.DriveLetter + "\\", Brand.OneDriveFolder, k.RemoteName.Replace('/', '\\')), null, error == null ? "backup" : "error"));
        PushState();
    }

    async Task LoadAccountAsync(bool force)
    {
        if (_signedOut || (!force && _account != null && DateTime.Now - _accountLoaded < TimeSpan.FromMinutes(10))) return;
        try
        {
            _account = await Graph.Me();
            _accountLoaded = DateTime.Now;
            PushState();
        }
        catch (Exception ex) { Log.Error("Não carreguei os dados da conta", ex); }
    }

    void MeasureFoldersAsync()
    {
        if (_backup == null) return;
        foreach (var k in _backup.AllFolders())
        {
            if (_folderSizes.ContainsKey(k.Id)) continue;
            _folderSizes[k.Id] = null;
            var path = k.LocalPath;
            var id = k.Id;
            Task.Run(() =>
            {
                long total = 0;
                var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                try { foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", opts)) total += f.Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                return total;
            }).ContinueWith(t => _ui.BeginInvoke(() => { _folderSizes[id] = t.Result; PushState(); }));
        }
    }

    // ---------- comandos ----------

    async Task OnCommand(string cmd, JsonElement a)
    {
        string Arg(string name) => a.TryGetProperty(name, out var v) ? v.ToString() : "";
        try
        {
            switch (cmd)
            {
                case "openDrive": OpenFolder(_settings.DriveLetter + "\\"); break;
                case "openWeb": if (_account?.WebUrl != null) MainWindow.OpenExternal(_account.WebUrl); break;
                case "openUrl": MainWindow.OpenExternal(Arg("url")); break;
                case "settings": ShowMain(); break;
                case "help": ShowMain("about"); break;
                case "recycle": if (RecycleBinUrl() is { } bin) MainWindow.OpenExternal(bin); break;
                case "openFile":
                    if (File.Exists(Arg("path"))) Process.Start(new ProcessStartInfo(Arg("path")) { UseShellExecute = true });
                    break;
                case "openWhere":
                    var where = Arg("where").Split(" → ")[0];
                    if (Path.IsPathRooted(where)) OpenFolder(where);
                    break;
                case "openLogs": OpenFolder(Paths.LogsDir); break;
                case "defender":
                    if (await Task.Run(() => Installer.RunElevatedSetup(_settings)))
                    {
                        _settings.ElevationDeclined = false;
                        _settings.DefenderExcludedLetter = _settings.DriveLetter;
                        _settings.Save();
                    }
                    PushState();
                    break;
                case "quit": _window?.Close(); await Quit(confirm: true); break;
                case "reveal": Reveal(Arg("path")); break;
                case "openHit": Reveal(Path.Combine(_settings.DriveLetter + "\\", Brand.OneDriveFolder, Arg("path").Replace('/', '\\'))); break;
                case "openLibrary": OpenFolder(Path.Combine(SharedRoot, Arg("name"))); break;
                case "removeLibrary":
                    var lib = _settings.Libraries.FirstOrDefault(l => l.Name == Arg("name"));
                    if (lib != null) { await RemoveLibrary(lib); PushState(); }
                    break;
                case "addAll": await AddAll(); PushState(); break;
                case "listLibraries":
                    try
                    {
                        _libraryCatalog = await Graph.AllLibraries();
                        _window?.Send("libraryCatalog", new
                        {
                            libraries = _libraryCatalog.OrderBy(l => l.Site, StringComparer.CurrentCultureIgnoreCase).ThenBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
                                .Select(l => new { id = l.DriveId, name = l.Name, site = l.Site, webUrl = l.WebUrl,
                                                   added = _settings.Libraries.Any(s => s.DriveId == l.DriveId && s.Root == "") }),
                        });
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Falha ao listar bibliotecas", ex);
                        _window?.Send("libraryCatalog", new { error = "Não foi possível buscar as bibliotecas agora. Verifique a internet e tente de novo." });
                    }
                    break;
                case "saveLibraries":
                    if (_libraryCatalog != null)
                        await SetLibraries(a.GetProperty("ids").EnumerateArray().Select(x => x.GetString()!).ToHashSet(), _libraryCatalog);
                    PushState();
                    break;
                case "toggleFolder":
                    _backup!.SetEnabled(Arg("id"), a.GetProperty("on").GetBoolean());
                    RegisterContextMenu();
                    PushState();
                    break;
                case "addFolder": AddFolder(); break;
                case "openLocalFolder":
                    var local = _backup!.AllFolders().FirstOrDefault(k => k.Id == Arg("id"));
                    if (local != null) OpenFolder(local.LocalPath);
                    break;
                case "openFolderWeb":
                    var f = _backup!.AllFolders().FirstOrDefault(k => k.Id == Arg("id"));
                    if (f == null) break;
                    var url = await Graph.FolderWebUrl(f.RemoteName);
                    if (url != null) MainWindow.OpenExternal(url);
                    else MessageBox.Show(_window, $"A pasta \"{BackupManager.DisplayName(f)}\" ainda não existe no OneDrive. Ligue o backup dela para que ela seja criada na nuvem.",
                        Brand.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    break;
                case "listDirs":
                    ListDirs(Arg("id"), Arg("rel"), (t, p) => _window?.Send(t, p));
                    break;
                case "saveExclusions":
                    _backup!.SetExclusions(Arg("id"), a.GetProperty("excluded").EnumerateArray().Select(x => x.GetString()!));
                    PushState();
                    break;
                case "setSchedule":
                    _settings.ScheduleEnabled = a.GetProperty("on").GetBoolean();
                    if (TimeSpan.TryParse(Arg("from"), out _)) _settings.ScheduleFrom = Arg("from");
                    if (TimeSpan.TryParse(Arg("to"), out _)) _settings.ScheduleTo = Arg("to");
                    _settings.Save();
                    _lastInSchedule = !InSchedule();   // força aplicar o estado novo agora
                    CheckSchedule();
                    break;
                case "removeFolder":
                    _backup!.RemoveCustom(Arg("id"));
                    _folderSizes.Remove(Arg("id"));
                    PushState();
                    break;
                case "setIntercept":
                    _settings.InterceptSync = a.GetProperty("on").GetBoolean();
                    _settings.Save();
                    Installer.ApplySyncInterception(_settings);
                    PushState();
                    break;
                case "pause" when a.TryGetProperty("hours", out var hrs):
                    PauseFor(hrs.GetInt32());
                    break;
                case "pause":
                    _backup!.Paused = a.GetProperty("on").GetBoolean();
                    _pausedUntil = null;
                    UpdateTrayIcon();
                    PushState();
                    break;
                case "search": await Search(Arg("q")); break;
                case "signOut": await SignOut(); break;
                case "signIn": await SignInWithWizard(); break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Comando {cmd}", ex);
            MessageBox.Show(_window, ex.Message, Brand.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void AddFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Escolha a pasta para fazer backup", UseDescriptionForTitle = true };
        if (dlg.ShowDialog(_window) != DialogResult.OK) return;
        var path = dlg.SelectedPath;
        if (path.StartsWith(_settings.DriveLetter!, StringComparison.OrdinalIgnoreCase) || new DriveInfo(path).DriveType == DriveType.Network)
        {
            MessageBox.Show(_window, "Escolha uma pasta deste computador, não da nuvem nem de uma unidade de rede.", Brand.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var k = _backup!.AddCustom(path);
        _backup.SetEnabled(k.Id, true);
        RegisterContextMenu();
        MeasureFoldersAsync();
        _window?.Navigate("folders");
        PushState();
    }

    System.Windows.Forms.Timer? _scheduleTimer;
    bool _lastInSchedule = true;

    /// Dentro do horário de sincronização configurado (sempre, se não houver horário).
    bool InSchedule()
    {
        if (!_settings.ScheduleEnabled) return true;
        if (!TimeSpan.TryParse(_settings.ScheduleFrom, out var from) || !TimeSpan.TryParse(_settings.ScheduleTo, out var to)) return true;
        var now = DateTime.Now.TimeOfDay;
        return from <= to ? now >= from && now < to : now >= from || now < to;   // ex.: 19:00-07:00 vira a meia-noite
    }

    void StartScheduleTimer()
    {
        _lastInSchedule = InSchedule();
        _scheduleTimer ??= new System.Windows.Forms.Timer { Interval = 30_000 };
        _scheduleTimer.Tick += (_, _) => CheckSchedule();
        _scheduleTimer.Start();
    }

    /// Ao sair do horário, para o envio; ao entrar, começa na hora.
    void CheckSchedule()
    {
        var inside = InSchedule();
        if (inside == _lastInSchedule) return;
        _lastInSchedule = inside;
        Log.Info(inside ? "Horário de sincronização começou" : "Fora do horário de sincronização: envio pausado");
        if (inside) _backup?.StartNow(); else _backup?.CancelAll();
        UpdateTrayIcon();
        PushState();
    }

    /// Pausar sincronização por X horas (0 = retomar), como no menu do OneDrive.
    void PauseFor(int hours)
    {
        if (_backup == null) return;
        _pauseTimer?.Dispose();
        _pauseTimer = null;
        if (hours <= 0)
        {
            _pausedUntil = null;
            _backup.Paused = false;
        }
        else
        {
            _pausedUntil = DateTime.Now.AddHours(hours);
            _backup.Paused = true;
            _pauseTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
            _pauseTimer.Tick += (_, _) => { if (DateTime.Now >= _pausedUntil) PauseFor(0); };
            _pauseTimer.Start();
        }
        Log.Info(hours <= 0 ? "Sincronização retomada" : $"Sincronização pausada por {hours} h");
        UpdateTrayIcon();
        PushState();
    }

    /// Lixeira do OneDrive na web: https://<tenant>-my.sharepoint.com/personal/<usuário>/_layouts/15/RecycleBin.aspx
    string? RecycleBinUrl()
    {
        if (_account?.WebUrl is not { } web) return null;
        var u = new Uri(web);
        var segs = u.AbsolutePath.Trim('/').Split('/');
        var site = segs.Length >= 2 && segs[0] == "personal" ? $"/personal/{segs[1]}" : "";
        return $"{u.Scheme}://{u.Host}{site}/_layouts/15/RecycleBin.aspx";
    }

    /// Subpastas de uma pasta com backup, para a árvore do "Escolher pastas" (um nível por vez).
    /// Subpastas diretas de rel, para "Escolher pastas" (janela principal e assistente).
    void ListDirs(string id, string rel, Action<string, object> send)
    {
        var k = _backup?.AllFolders().FirstOrDefault(x => x.Id == id);
        if (k == null) return;
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        static bool HasChildren(string dir, EnumerationOptions o)
        {
            try { return Directory.EnumerateDirectories(dir, "*", o).Any(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        }
        var baseDir = Path.Combine(k.LocalPath, rel.Replace('/', '\\'));
        List<object> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(baseDir, "*", opts)
                .OrderBy(d => Path.GetFileName(d), StringComparer.CurrentCultureIgnoreCase)
                .Select(d => (object)new
                {
                    name = Path.GetFileName(d),
                    rel = (rel.Length == 0 ? "" : rel + "/") + Path.GetFileName(d),
                    hasChildren = HasChildren(d, opts),
                })
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { dirs = new(); }
        send("dirs", new { id, rel, dirs });
    }

    async Task Search(string q)
    {
        try
        {
            var hits = await Graph.Search(q);
            _window?.Send("results", new { q, hits = hits.Select(h => new { name = h.Name, path = h.Path, isFolder = h.IsFolder }) });
        }
        catch (Exception ex)
        {
            Log.Error("Pesquisa", ex);
            _window?.Send("results", new { q, hits = Array.Empty<object>(), error = "Não foi possível pesquisar agora. Verifique a internet e tente de novo." });
        }
    }

    async Task SignOut()
    {
        if (MessageBox.Show(_window, "Desvincular este PC da conta?\n\nA unidade sai do Explorer, o backup para e o histórico de atividade deste PC é apagado. Nada é apagado na nuvem.",
                Brand.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        await _busy.WaitAsync();
        try
        {
            if (_backup != null) _backup.Paused = true;
            await _rclone.Stop(TimeSpan.FromSeconds(30));
            foreach (var r in new[] { "m365", "hdt", "sharepoint", "onedrive" })
                await Rclone.Run(TimeSpan.FromSeconds(30), "config", "delete", r);
            _signedOut = true;
            if (_account?.Email is { Length: > 0 } last) { _settings.LastEmail = last; _settings.Save(); }
            _account = null;
            // A próxima conta pode ser de outra pessoa ou tenant: não mostra a atividade nem os avisos desta
            _activity.Clear();
            _lastActivity = null;
            _notices.Clear();
            SaveActivity();
            SetStatus("Desconectado");
            Log.Info("Conta desconectada");
        }
        finally { _busy.Release(); }
        UpdateTrayIcon();
        PushState();
        _window?.Close();
        await SignInWithWizard();   // como o OneDrive: abre "Entrar no OneDrive 365"
    }

    async Task SignIn()
    {
        await _busy.WaitAsync();
        try
        {
            SetStatus("Aguardando login na Microsoft…");
            if (!await Rclone.Login())
            {
                SetStatus("Desconectado");
                MessageBox.Show(_window, "O login não foi concluído.", Brand.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _signedOut = false;
            await WriteRemotesForAccount();
            await _rclone.Start(_settings.DriveLetter!);
            if (_backup != null) _backup.Paused = false;
            await RefreshStatus();
        }
        finally { _busy.Release(); }
        await LoadAccountAsync(force: true);
    }

    static void Reveal(string path)
    {
        if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(path)) OpenFolder(path);
        else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir)) OpenFolder(dir);
    }

    static string Ago(DateTime t)
    {
        var m = (int)(DateTime.Now - t).TotalMinutes;
        return m < 1 ? "agora" : m < 60 ? $"há {m} min" : m < 1440 ? $"há {m / 60} h" : t.ToString("dd/MM HH:mm");
    }

    static string? IconDataUri(Icon? icon)
    {
        if (icon == null) return null;
        using (icon)
        using (var bmp = icon.ToBitmap())
        using (var ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
    }
}
