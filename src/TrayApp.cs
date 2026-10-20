using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;

namespace HdtDrive;

/// Ícone na bandeja: sobe a unidade, recebe cliques de "Sincronizar" do SharePoint e mostra o status.
sealed partial class TrayApp : ApplicationContext
{
    readonly NotifyIcon _tray;
    readonly Control _ui = new();
    readonly Settings _settings = Settings.Load();
    readonly Rclone _rclone = new();
    readonly SemaphoreSlim _busy = new(1, 1);
    readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 2000 };
    readonly string _pipeName;
    readonly TaskCompletionSource _ready = new();
    string _status = "Iniciando…";
    string _backupStatus = "";
    BackupManager? _backup;
    bool _quitting;
    int _restarts;

    string Letter => _settings.DriveLetter!;
    string SharedRoot => Path.Combine(Letter + "\\", Brand.SharePointFolder);

    readonly bool _openWhenReady;

    public TrayApp(string? initialUrl, string pipeName, bool openWhenReady)
    {
        _pipeName = pipeName;
        _openWhenReady = openWhenReady;
        _ui.CreateControl();   // instala o SynchronizationContext do WinForms para os awaits voltarem à UI

        // Ícone da bandeja = nuvem do OneDrive instalado; sem OneDrive no PC, o ícone próprio do app
        using var ico = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico")!;
        var trayIcon = ShellIntegration.OneDriveIcon(TrayIcons.TraySize) != null ? TrayIcons.Get(TrayIcons.Kind.Ok) : new Icon(ico);
        _tray = new NotifyIcon { Icon = trayIcon, Text = Brand.Name, Visible = true };
        _trayAnim.Tick += (_, _) => { _trayFrame = (_trayFrame + 1) % TrayIcons.SyncFrames; _tray.Icon = TrayIcons.Get(TrayIcons.Kind.Sync, _trayFrame); };
        // Fica visível na barra (fora da setinha ^) na primeira vez, como o OneDrive
        if (!_settings.TrayPromoted)
            _ui.BeginInvoke(async () =>
            {
                await Task.Delay(3000);   // o Windows cria a entrada do ícone alguns segundos depois de ele aparecer
                if (TrayIcons.TryPromote(Paths.InstalledExe)) { _settings.TrayPromoted = true; _settings.Save(); }
            });
        // Clique esquerdo ou direito abre o painel da bandeja, como o OneDrive
        _tray.MouseUp += (_, e) => { if (e.Button is MouseButtons.Left or MouseButtons.Right) ToggleFlyout(); };
        _rclone.MountDied += reason => _ui.BeginInvoke(() => OnMountDied(reason));
        LoadActivity();
        _statusTimer.Tick += async (_, _) => await RefreshStatus();

        new Thread(ListenPipe) { IsBackground = true, Name = "pipe" }.Start();
        _ui.BeginInvoke(async () => await Startup(initialUrl));
    }

    async Task Startup(string? initialUrl)
    {
        await _busy.WaitAsync();
        try
        {
            Installer.Install(_settings);

            // Link que não é "sincronizar" (ex.: abrir pasta no OneDrive) não precisa esperar a unidade subir
            if (initialUrl?.StartsWith("odopen:", StringComparison.OrdinalIgnoreCase) == true && !IsSyncLink(initialUrl))
            {
                Installer.ForwardToOneDrive(_settings, initialUrl);
                initialUrl = null;
            }

            var overlaysJustRegistered = false;
            if (Installer.NeedsElevatedSetup() && (!Installer.WinFspInstalled() || !_settings.ElevationDeclined))
            {
                var ok = await Task.Run(Installer.RunElevatedSetup);
                _settings.ElevationDeclined = !ok;
                overlaysJustRegistered = ok;
                if (!Installer.WinFspInstalled())
                    throw new InvalidOperationException("O WinFsp não foi instalado. Abra o OneDrive 365 de novo para tentar outra vez.");
            }

            _settings.DriveLetter ??= PickLetter();
            if (!Rclone.HasRemote("onedrive"))
            {
                // Primeira execução: assistente (e-mail -> login no navegador -> M: -> backup -> pronto)
                SetStatus("Aguardando login na Microsoft…");
                if (!await RunWizardLogin(_settings.LastEmail))
                {
                    Log.Info("Assistente fechado antes do login: encerrando");
                    _ui.BeginInvoke(async () => await Quit(confirm: false));   // depois do finally liberar a inicialização
                    return;
                }
            }

            _settings.Save();
            ShellIntegration.ApplyDrive(Letter);
            Overlays.InstallPerUser(Letter);
            ShellIntegration.ApplyNavigationPane(Letter);
            ShellIntegration.SetColors(_settings.FolderColors);
            await WriteRemotesForAccount();
            SetStatus("Montando unidade…");
            await _rclone.Start(Letter);
            _statusTimer.Start();
            await RefreshStatus();
            _backup = new BackupManager(_settings, s => _ui.BeginInvoke(() => { _backupStatus = s; PushState(); }))
            {
                Finished = (k, err) => _ui.BeginInvoke(() => OnBackupFinished(k, err)),
                ProgressChanged = p => _ui.BeginInvoke(() => OnBackupProgress(p)),
                FileUploaded = (k, rel, size) => _ui.BeginInvoke(() => OnBackupFile(k, rel, size)),
            };
            _backup.Allowed = InSchedule;
            _backup.Start();
            _backupStatus = _backup.Summary();
            StartScheduleTimer();
            RegisterContextMenu();
            _ = MenuPackage.EnsureRegisteredAsync().ContinueWith(t => { if (t.Exception != null) Log.Error("Menu do Windows 11", t.Exception); });
            _ready.TrySetResult();
            _ = LoadAccountAsync(force: false);   // nome da organização e foto no painel da bandeja desde o início
            if (_wizard is { IsDisposed: false })
            {
                _settings.BackupDialogShown = true;   // as pastas são escolhidas no próprio assistente
                _settings.Save();
                _wizard.Send(new { type = "driveReady" });
            }
            else if (!_settings.BackupDialogShown)
            {
                _settings.BackupDialogShown = true;   // primeira execução: escolher as pastas do PC
                _settings.Save();
                ShowMain("folders");
            }
            else if (_openWhenReady) ShowMain();
            if (overlaysJustRegistered)
                AddNotice("info", "Ícones de status chegam no próximo login", "Saia e entre no Windows para ver nuvem, no PC e sincronizando nos arquivos da unidade.");
            // Nunca reiniciamos o Explorer: os selos entram no próximo login (o Explorer só lê os handlers ao iniciar)
            Notify("OneDrive 365 pronto", overlaysJustRegistered
                ? $"Seus arquivos estão em {Letter}\\. Os ícones de status aparecem na próxima vez que você entrar no Windows."
                : $"Seus arquivos estão em {Letter}\\. Clique em \"Sincronizar\" no SharePoint para adicionar bibliotecas.");
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao iniciar", ex);
            _wizard?.Send(new { type = "driveFailed", error = ex.Message });
            SetStatus("Erro — veja o log");
            MessageBox.Show(ex.Message, Brand.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally { _busy.Release(); }

        if (initialUrl != null) await HandleMessage(initialUrl);   // odopen:// ou add-all recebido na abertura
    }

    static bool IsSyncLink(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.Equals("sync", StringComparison.OrdinalIgnoreCase);

    async Task HandleMessage(string msg)
    {
        Log.Info($"Mensagem: {msg}");
        if (msg == "show") { await _ready.Task; ShowMain(); return; }
        if (msg.StartsWith("flyout-shot:"))   // diagnóstico: abre o painel da bandeja e captura em PNG
        {
            await _ready.Task;
            ToggleFlyout();
            await Task.Delay(2500);
            await _flyout!.CaptureAsync(msg["flyout-shot:".Length..]);
            return;
        }
        if (msg.StartsWith("screenshot:"))   // diagnóstico: screenshot:<página>|<arquivo.png>
        {
            await _ready.Task;
            var parts = msg["screenshot:".Length..].Split('|', 2);
            var search = parts[0].StartsWith("search=") ? parts[0]["search=".Length..] : null;
            var click = parts[0].StartsWith("click=") ? parts[0]["click=".Length..] : null;
            ShowMain(search == null && click == null ? parts[0] : "home");
            await Task.Delay(3000);
            if (search != null) { await _window!.TypeSearchAsync(search); await Task.Delay(5000); }
            if (click != null) { await _window!.ClickAsync(click); await Task.Delay(800); }
            await _window!.CaptureAsync(parts[1]);
            return;
        }
        if (msg == "quit") { await Quit(confirm: false); return; }
        if (msg == "add-all") { await _ready.Task; await AddAll(); return; }
        if (msg.StartsWith("backup:")) { await _ready.Task; _backup!.SetEnabled(msg["backup:".Length..], true); return; }
        if (msg.StartsWith("action:"))
        {
            var parts = msg["action:".Length..].Split('|', 2);
            try { await HandleShellAction(parts[0], parts[1]); }
            catch (Exception ex)
            {
                Log.Error($"Ação {parts[0]} em {parts[1]}", ex);
                MessageBox.Show($"Não consegui concluir: {ex.Message}", Brand.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return;
        }
        if (!msg.StartsWith("odopen:", StringComparison.OrdinalIgnoreCase)) return;
        if (!IsSyncLink(msg)) { Installer.ForwardToOneDrive(_settings, msg); return; }
        await _ready.Task;   // clique em "Sincronizar" durante a inicialização espera a unidade subir

        Notify("Adicionando biblioteca…", "Buscando informações no SharePoint.");
        try
        {
            var lib = await Graph.ResolveSyncLink(msg);
            await AddLibraries(new[] { lib });
            OpenFolder(Path.Combine(SharedRoot, _settings.Libraries.First(l => l.DriveId == lib.DriveId && l.Root == lib.Root).Name));
        }
        catch (Exception ex)
        {
            Log.Error($"Falha ao sincronizar {msg}", ex);
            MessageBox.Show($"Não consegui adicionar essa biblioteca:\n\n{ex.Message}", Brand.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// Escreve os remotes só com as bibliotecas que a conta atual acessa. Uma biblioteca de outra conta/tenant
    /// (ou com acesso removido) derrubaria a unidade inteira ("itemNotFound"), então sai da lista.
    async Task WriteRemotesForAccount()
    {
        var gone = new List<Library>();
        foreach (var lib in _settings.Libraries.ToList())
            if (!await Graph.LibraryAccessible(lib)) gone.Add(lib);
        if (gone.Count > 0)
        {
            foreach (var lib in gone) _settings.Libraries.Remove(lib);
            _settings.Save();
            Log.Info($"Bibliotecas removidas (a conta não acessa): {string.Join(", ", gone.Select(l => l.Name))}");
            AddNotice("info", "Bibliotecas removidas da unidade",
                $"{string.Join(", ", gone.Select(l => l.Name))} não está disponível para esta conta. Clique em \"Sincronizar\" no SharePoint para adicionar de novo.");
        }
        await Rclone.WriteRemotes(_settings.Libraries);
    }

    async Task AddLibraries(IEnumerable<Library> libs)
    {
        var added = new List<string>();
        foreach (var lib in libs)
        {
            if (_settings.Libraries.Any(l => l.DriveId == lib.DriveId && l.Root == lib.Root)) continue;
            var name = lib.Name;
            for (var i = 2; _settings.Libraries.Any(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); i++)
                name = $"{lib.Name} ({i})";
            lib.Name = name;
            _settings.Libraries.Add(lib);
            added.Add(name);
        }
        if (added.Count == 0) return;   // já estava na unidade
        Log.Info($"Bibliotecas adicionadas: {string.Join(", ", added)}");
        await ApplyLibraries();
        Notify(added.Count == 1 ? $"\"{added[0]}\" adicionada" : $"{added.Count} bibliotecas adicionadas",
               $"Disponível em {SharedRoot}. Os arquivos baixam só quando você abre.");
    }

    /// Triagem de "Adicionar bibliotecas que eu acesso": marcadas ficam na unidade, desmarcadas saem.
    /// Bibliotecas sincronizadas só numa subpasta (botão Sincronizar numa pasta) não são mexidas.
    async Task SetLibraries(IReadOnlyCollection<string> driveIds, List<Library> catalog)
    {
        var removed = _settings.Libraries.RemoveAll(l => l.Root == "" && catalog.Any(c => c.DriveId == l.DriveId) && !driveIds.Contains(l.DriveId));
        var toAdd = catalog.Where(c => driveIds.Contains(c.DriveId) && !_settings.Libraries.Any(l => l.DriveId == c.DriveId && l.Root == "")).ToList();
        foreach (var lib in toAdd)
        {
            var name = lib.Name;
            for (var i = 2; _settings.Libraries.Any(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); i++)
                name = $"{lib.Name} ({i})";
            _settings.Libraries.Add(new Library { Name = name, DriveId = lib.DriveId, WebUrl = lib.WebUrl });
        }
        if (removed == 0 && toAdd.Count == 0) return;
        Log.Info($"Triagem de bibliotecas: +{toAdd.Count} -{removed}");
        await ApplyLibraries();
        if (toAdd.Count > 0)
            Notify(toAdd.Count == 1 ? $"\"{toAdd[0].Name}\" adicionada" : $"{toAdd.Count} bibliotecas adicionadas",
                   $"Disponível em {SharedRoot}. Os arquivos baixam só quando você abre.");
    }

    async Task RemoveLibrary(Library lib)
    {
        if (MessageBox.Show($"Tirar \"{lib.Name}\" da unidade {Letter}?\n\nNada é apagado no SharePoint.", Brand.Name,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _settings.Libraries.Remove(lib);
        Log.Info($"Biblioteca removida pelo usuário: {lib.Name}");
        await ApplyLibraries();
    }

    /// Grava os remotes e remonta (o combine do rclone não muda com a unidade montada; leva ~2s).
    async Task ApplyLibraries()
    {
        await _busy.WaitAsync();
        try
        {
            _settings.Save();
            SetStatus("Atualizando bibliotecas…");
            await _rclone.Stop(TimeSpan.FromSeconds(30));
            await Rclone.WriteRemotes(_settings.Libraries);
            await _rclone.Start(Letter);
            await RefreshStatus();
        }
        catch (MountStuckException ex)
        {
            SetStatus("Unidade presa: reinicie o computador");
            MessageBox.Show(ex.Message, Brand.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { _busy.Release(); }
    }

    async Task AddAll()
    {
        Notify("Procurando bibliotecas…", "Isso pode levar alguns segundos.");
        try { await AddLibraries((await Graph.AllLibraries()).OrderBy(l => l.Name)); }
        catch (Exception ex)
        {
            Log.Error("Falha ao listar bibliotecas", ex);
            MessageBox.Show(ex.Message, Brand.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    async void OnMountDied(string reason)
    {
        Log.Error($"Montagem caiu: {reason}");
        if (_quitting) return;
        if (++_restarts > 5)
        {
            SetStatus("Desconectado — veja o log");
            Notify("OneDrive 365 desconectado", "A unidade caiu várias vezes. Abra o log pelo menu do ícone.");
            return;
        }
        SetStatus("Reconectando…");
        await Task.Delay(TimeSpan.FromSeconds(5 * _restarts));
        await _busy.WaitAsync();
        try { await _rclone.Start(Letter); }
        catch (Exception ex) { Log.Error("Falha ao remontar", ex); }
        finally { _busy.Release(); }
    }

    async Task RefreshStatus()
    {
        var s = await _rclone.Stats();
        if (s == null) return;
        var queue = await _rclone.UploadQueue();
        if (queue != null)
        {
            try { Overlays.PublishPending(queue.Select(q => q.Name), Letter); }
            catch (IOException ex) { Log.Error("Não publiquei a fila para os selos", ex); }
            TrackQueue(queue);
        }
        _restarts = 0;
        UpdateTrayIcon();
        SetStatus(s.Uploading > 0 ? $"Enviando {s.Uploading} arquivo(s)…"
                : s.Errored > 0 ? $"{s.Errored} arquivo(s) com erro de envio"
                : "Tudo atualizado");
    }

    readonly System.Windows.Forms.Timer _trayAnim = new() { Interval = 110 };
    int _trayFrame;
    TrayIcons.Kind _trayKind = TrayIcons.Kind.Ok;

    /// Ícone da bandeja conforme o estado, como o do OneDrive (setas girando enquanto sincroniza).
    void UpdateTrayIcon()
    {
        var hasError = _backup != null && _backup.AllFolders().Any(k => _backup.State(k.Id) is { Enabled: true, LastError: not null });
        var kind = _signedOut ? TrayIcons.Kind.Offline
            : _status.StartsWith("Unidade presa") || _status.StartsWith("Desconectado") || _status.StartsWith("Erro") || hasError ? TrayIcons.Kind.Error
            : _lastQueue.Count > 0 || _backup?.Running != null ? TrayIcons.Kind.Sync
            : _backup?.Paused == true || !InSchedule() ? TrayIcons.Kind.Paused
            : TrayIcons.Kind.Ok;
        if (kind == _trayKind) return;
        _trayKind = kind;
        if (kind == TrayIcons.Kind.Sync) _trayAnim.Start();
        else { _trayAnim.Stop(); _tray.Icon = TrayIcons.Get(kind); }
    }

    void SetStatus(string s)
    {
        if (_status != s) { _status = s; PushState(); }
        var text = $"OneDrive 365 — {s}";
        _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    async Task Quit(bool confirm)
    {
        if (confirm)
        {
            // Sempre pergunta, como o OneDrive ("Encerrar o OneDrive?")
            var s = await _rclone.Stats();
            using var dlg = new QuitDialog((s?.Uploading ?? 0) + _lastQueue.Count);
            if (dlg.ShowDialog() != DialogResult.OK) return;
        }
        Log.Info(confirm ? "Encerrado pelo usuário" : "Encerrado");
        _quitting = true;
        _statusTimer.Stop();
        _backup?.Dispose();
        try { await _rclone.Stop(TimeSpan.FromSeconds(confirm ? 0 : 20)); }
        catch (MountStuckException) { Log.Error("Saindo com a montagem presa; o reinício do Windows libera"); }
        _tray.Visible = false;
        ExitThread();
    }

    void ListenPipe()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                server.WaitForConnection();
                var msg = new StreamReader(server).ReadLine();
                if (!string.IsNullOrWhiteSpace(msg))
                    _ui.BeginInvoke(async () =>
                    {
                        try { await HandleMessage(msg.Trim()); }
                        catch (Exception ex) { Log.Error($"Mensagem da pipe: {msg}", ex); }   // nunca derruba o app nem abre a caixa de erro do .NET
                    });
            }
            catch (IOException ex) { Log.Error("Pipe", ex); }
        }
    }

    void Notify(string title, string text) => _tray.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);

    static void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) { Log.Info($"Pasta não existe para abrir: {path}"); return; }
        // explorer.exe direto: sempre abre uma janela nova na frente (ShellExecute da pasta às vezes só
        // reaproveitava uma janela já aberta, sem trazê-la para a frente, e parecia que nada acontecia)
        try
        {
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            psi.ArgumentList.Add(path.Length == 3 ? path : path.TrimEnd('\\'));
            Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex) { Log.Error($"Não abri {path}", ex); }
    }

    static string PickLetter()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        foreach (var c in "MOPQRSTUVWXYZHIJKLN")
            if (!used.Contains(c)) return $"{c}:";
        throw new InvalidOperationException("Nenhuma letra de unidade livre.");
    }
}
