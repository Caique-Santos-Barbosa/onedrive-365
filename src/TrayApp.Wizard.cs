using System.Text.Json;

namespace HdtDrive;

/// Assistente da primeira execução, no modelo do "Configurar o OneDrive".
sealed partial class TrayApp
{
    WizardWindow? _wizard;
    TaskCompletionSource<bool>? _wizardLogin;
    CancellationTokenSource? _loginCancel;

    /// Mostra o assistente e espera o login. false = o usuário fechou antes de entrar.
    Task<bool> RunWizardLogin(string? email = null)
    {
        _wizardLogin = new TaskCompletionSource<bool>();
        _wizard = new WizardWindow(new
        {
            step = "welcome",
            email,
            letter = _settings.DriveLetter,
            cloudIcon = IconDataUri(ShellIntegration.OneDriveIcon(96)),
            sharePointIcon = IconDataUri(ShellIntegration.SharePointIcon32()),
            art = new
            {
                login = ShellIntegration.OneDriveArt("fre_email_hrd"),
                intro = ShellIntegration.OneDriveArt("FRE_Tutorial_Intro"),
                folders = ShellIntegration.OneDriveArt("fre_choose_folder"),
                done = ShellIntegration.OneDriveArt("fre_done"),
            },
        }, (cmd, args) => _ui.BeginInvoke(async () => await OnWizardCommand(cmd, args)));
        _wizard.FormClosed += (_, _) =>
        {
            _loginCancel?.Cancel();
            _wizardLogin.TrySetResult(false);
            _wizard = null;
        };
        _wizard.Show();
        _wizard.Activate();
        return _wizardLogin.Task;
    }

    async Task OnWizardCommand(string cmd, JsonElement a)
    {
        string Arg(string n) => a.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        switch (cmd)
        {
            case "login":
                _loginCancel = new CancellationTokenSource();
                var ok = await Rclone.Login(Arg("email"), _loginCancel.Token);
                if (_loginCancel.IsCancellationRequested) return;   // voltou para a tela anterior
                if (ok)
                {
                    _wizard?.Send(new { type = "loggedIn" });
                    _wizardLogin?.TrySetResult(true);
                }
                else
                    _wizard?.Send(new
                    {
                        type = "loginFailed",
                        error = "O login não foi concluído. Tente de novo. Se aparecer \"aprovação do administrador\", peça à TI para liberar o app no Microsoft Entra ID.",
                    });
                break;
            case "createAccount":
                MainWindow.OpenExternal("https://signup.live.com/");
                break;
            case "reopen":
                await Rclone.ReopenLoginPage(Arg("email"));
                break;
            case "retryDrive":
                if (_backup != null) await MountAfterWizardLogin();   // "Desconectar" → entrar de novo
                else { Program.Relaunch(); await Quit(confirm: false); }   // primeira execução: o início parou no erro, recomeça do zero
                break;
            case "cancelLogin":
                _loginCancel?.Cancel();
                break;
            case "folders":
                MeasureFoldersAsync();
                SendWizardFolders();
                break;
            case "listDirs":
                ListDirs(Arg("id"), Arg("rel"), (t, p) => _wizard?.Send(new { type = t, payload = p }));
                break;
            case "saveExclusions":
                _backup?.SetExclusions(Arg("id"), a.GetProperty("excluded").EnumerateArray().Select(x => x.GetString()!));
                break;
            case "saveFolders":
                var on = a.GetProperty("ids").EnumerateArray().Select(x => x.GetString()!).ToHashSet();
                if (_backup != null)
                    foreach (var k in _backup.AllFolders().Where(k => !k.Id.StartsWith("custom-")))
                        if (_backup.State(k.Id).Enabled != on.Contains(k.Id)) _backup.SetEnabled(k.Id, on.Contains(k.Id));
                RegisterContextMenu();
                PushState();
                break;
            case "openDrive":
                OpenFolder(Letter + "\\");
                _wizard?.Close();
                break;
            case "finish":
                _wizard?.Close();
                break;
        }
    }

    /// Entrar de novo (depois de "Desconectar conta"): assistente com o último e-mail; o app continua aberto.
    async Task SignInWithWizard()
    {
        if (_wizard is { IsDisposed: false }) { _wizard.Activate(); return; }
        if (!await RunWizardLogin(_settings.LastEmail)) return;   // fechou: continua desconectado, na bandeja
        await MountAfterWizardLogin();
    }

    /// Monta a unidade depois do login no assistente; em caso de erro o assistente mostra "Tentar de novo" (retryDrive).
    async Task MountAfterWizardLogin()
    {
        await _busy.WaitAsync();
        try
        {
            _signedOut = false;
            await WriteRemotesForAccount();
            await _rclone.Start(Letter);
            if (_backup != null) _backup.Paused = false;
            await RefreshStatus();
            _wizard?.Send(new { type = "driveReady" });
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao montar depois do login", ex);
            _wizard?.Send(new { type = "driveFailed", error = ex.Message });
        }
        finally { _busy.Release(); }
        await LoadAccountAsync(force: true);
        UpdateTrayIcon();
        PushState();
    }

    /// Lista de pastas para a etapa de backup (reenviada quando os tamanhos terminam de ser calculados).
    void SendWizardFolders()
    {
        if (_wizard is not { IsDisposed: false } || _backup == null) return;
        _wizard.Send(new
        {
            type = "folders",
            folders = _backup.AllFolders().Where(k => !k.Id.StartsWith("custom-")).Select(k =>
            {
                _folderSizes.TryGetValue(k.Id, out var size);
                if (!_folderIcons.TryGetValue(k.Id, out var icon)) _folderIcons[k.Id] = icon = IconDataUri(ShellIntegration.FolderIcon(k.LocalPath));
                return new { id = k.Id, name = BackupManager.DisplayName(k), path = k.LocalPath, size, icon, excluded = _backup.State(k.Id).Excluded, enabled = _backup.State(k.Id).Enabled };
            }),
        });
    }
}
