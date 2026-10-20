using Microsoft.Win32;

namespace HdtDrive;

/// Menu de botão direito "OneDrive 365" (Compartilhar, Copiar Link, Gerenciar acesso, Exibir online, Cor da pasta)
/// nos itens do M: e das pastas com backup — no menu clássico (aqui) e no do Windows 11 (OneDrive365Menu.dll).
sealed partial class TrayApp
{
    const string MenuKey = "OneDrive365";
    // Menu clássico ("Mostrar mais opções"): arquivos, pastas e fundo de pastas. %V = pasta aberta no fundo
    static readonly (string Key, string Arg)[] MenuTargets =
    {
        (@"Software\Classes\*\shell", "%1"),
        (@"Software\Classes\Directory\shell", "%1"),
        (@"Software\Classes\Directory\Background\shell", "%V"),
    };

    /// Registra (ou atualiza) os verbos em HKCU. AppliesTo limita o menu às pastas que estão na nuvem.
    void RegisterContextMenu()
    {
        var roots = new List<string> { _settings.DriveLetter + "\\" };
        if (_backup != null)
            roots.AddRange(_backup.AllFolders().Where(k => _backup.State(k.Id).Enabled).Select(k => k.LocalPath.TrimEnd('\\') + "\\"));
        var appliesTo = string.Join(" OR ", roots.Select(r => $"System.ItemPathDisplay:~<\"{r}\""));
        var exe = $"\"{Paths.InstalledExe}\"";

        // Menu do Windows 11 (OneDrive365Menu.dll) lê as mesmas pastas daqui
        using (var state = Registry.CurrentUser.CreateSubKey(@"Software\OneDrive365"))
        {
            state.SetValue("MenuRoots", string.Join("|", roots));
            state.SetValue("Exe", Paths.InstalledExe);
        }

        foreach (var (target, arg) in MenuTargets)
        {
            using var k = Registry.CurrentUser.CreateSubKey($@"{target}\{MenuKey}");
            k.SetValue("MUIVerb", Brand.Name);
            k.SetValue("Icon", ShellIntegration.CloudIconResource);
            k.SetValue("SubCommands", "");
            k.SetValue("AppliesTo", appliesTo);
            Registry.CurrentUser.DeleteSubKeyTree($@"{target}\{MenuKey}\shell", throwOnMissingSubKey: false);   // versões antigas
            foreach (var (id, label, action) in new[]
            {
                ("1share", "Compartilhar", "share"),
                ("2link", "Copiar Link", "link"),
                ("3access", "Gerenciar acesso", "access"),
                ("4web", "Exibir online", "web"),
                ("5color", "Cor da pasta", "color"),
            })
            {
                if (action == "color" && !target.EndsWith(@"Directory\shell")) continue;   // só em pastas
                using var v = k.CreateSubKey($@"shell\{id}");
                v.SetValue("MUIVerb", label);
                v.SetValue("Icon", Path.Combine(Paths.AppDir, "menuicons", action + ".ico"));
                using var c = v.CreateSubKey("command");
                c.SetValue("", $"{exe} --action {action} \"{arg}\"");
            }
        }
    }

    public static void RemoveContextMenu()
    {
        foreach (var (target, _) in MenuTargets)
            Registry.CurrentUser.DeleteSubKeyTree($@"{target}\{MenuKey}", throwOnMissingSubKey: false);
    }

    /// Caminho Graph do item correspondente a um caminho local (M:\... ou pasta com backup); null se não está na nuvem.
    string? CloudPath(string path)
    {
        static string Seg(string rel) => string.Join('/', rel.Split('\\', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        static string Join(string root, string rel) => rel.Length == 0 ? root.TrimEnd(':') : $"{root}/{Seg(rel)}";

        path = path.TrimEnd('\\');
        var drive = _settings.DriveLetter + "\\";
        if (path.StartsWith(drive, StringComparison.OrdinalIgnoreCase))
        {
            var rel = path[drive.Length..];
            var parts = rel.Split('\\', 3);
            if (parts[0].Equals(Brand.OneDriveFolder, StringComparison.OrdinalIgnoreCase))
                return parts.Length == 1 ? "me/drive/root" : $"me/drive/root:/{Seg(rel[(parts[0].Length + 1)..])}";
            if (parts[0].Equals(Brand.SharePointFolder, StringComparison.OrdinalIgnoreCase) && parts.Length >= 2)
            {
                var lib = _settings.Libraries.FirstOrDefault(l => l.Name.Equals(parts[1], StringComparison.OrdinalIgnoreCase));
                if (lib == null) return null;
                var inner = string.Join('\\', new[] { lib.Root.Replace('/', '\\'), parts.Length == 3 ? parts[2] : "" }.Where(x => x.Length > 0));
                return inner.Length == 0 ? $"drives/{lib.DriveId}/root" : $"drives/{lib.DriveId}/root:/{Seg(inner)}";
            }
            return null;
        }
        if (_backup != null)
            foreach (var k in _backup.AllFolders())
            {
                var root = k.LocalPath.TrimEnd('\\');
                if (path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                    return Join($"me/drive/root:/{Seg(k.RemoteName.Replace('/', '\\'))}", path[root.Length..].TrimStart('\\'));
            }
        return null;
    }

    /// "Pessoas em HDT ENERGY": nome da organização tirado do domínio do e-mail da conta.
    string OrgName()
    {
        var domain = _account?.Email.Split('@').LastOrDefault();
        if (string.IsNullOrEmpty(domain)) return "sua organização";
        // hdt.energy -> HDT ENERGY; acme.com.br -> ACME
        var generic = new[] { "com", "net", "org", "br", "io", "co", "gov", "edu", "biz", "info" };
        var parts = domain.Split('.').Where(p => !generic.Contains(p, StringComparer.OrdinalIgnoreCase));
        return string.Join(' ', parts).ToUpperInvariant();
    }

    async Task HandleShellAction(string action, string path)
    {
        await _ready.Task;
        if (action == "color") { await SetFolderColor(path); return; }
        await LoadAccountAsync(force: false);
        var cloud = CloudPath(path);
        var item = cloud == null ? null : await Graph.Item(cloud);
        if (item == null && action == "resolve") { Log.Info($"resolve {path} -> {cloud ?? "(fora da nuvem)"} -> não existe"); return; }
        if (item == null)
        {
            MessageBox.Show($"\"{Path.GetFileName(path)}\" ainda não está no OneDrive.\n\nSe for de uma pasta com backup, ele aparece na nuvem assim que o envio terminar.",
                Brand.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        switch (action)
        {
            case "resolve":   // diagnóstico: só registra o que encontrou
                Log.Info($"resolve {path} -> {cloud} -> {item.WebUrl}");
                break;
            case "web":
                MainWindow.OpenExternal(item.WebUrl);
                break;
            case "link":
                var (url, scope) = await DefaultLink(item);
                Clipboard.SetText(url);
                var who = scope == "anonymous" ? "Qualquer pessoa com o link" : $"Pessoas em {OrgName()} com o link";
                new LinkToast(item.Name, $"{who} {(_settings.LinkRole == "view" ? "pode exibir" : "pode editar")}",
                    () => new ShareDialog(item, _settings, OrgName(), "settings").Show()).Show();
                break;
            case "share":
                new ShareDialog(item, _settings, OrgName()).Show();
                break;
            case "access":
                new ShareDialog(item, _settings, OrgName(), "access").Show();
                break;
        }
    }

    /// Link com o padrão das Configurações de link; se a empresa bloqueia "Alguém", usa a organização.
    async Task<(string Url, string Scope)> DefaultLink(Graph.DriveItem item)
    {
        var type = _settings.LinkRole == "view" ? "view" : "edit";
        var scope = _settings.LinkScope is "anonymous" or "organization" ? _settings.LinkScope : "organization";
        try { return (await Graph.CreateLink(item, type, scope), scope); }
        catch (HttpRequestException) when (scope == "anonymous") { return (await Graph.CreateLink(item, type, "organization"), "organization"); }
    }

    /// "Cor da pasta": no PC, desktop.ini direto na pasta; no M:, desktop.ini no overlay (exige remontar a unidade).
    async Task SetFolderColor(string path)
    {
        path = path.TrimEnd('\\');
        var drive = _settings.DriveLetter + "\\";
        var inDrive = path.StartsWith(drive, StringComparison.OrdinalIgnoreCase);
        var key = inDrive ? path[drive.Length..].Replace('\\', '/') : null;
        var current = inDrive
            ? _settings.FolderColors.GetValueOrDefault(key!, "yellow")
            : LocalColor(path);

        using var dlg = new FolderColorDialog(Path.GetFileName(path), current);
        if (dlg.ShowDialog() != DialogResult.OK || dlg.Selected == current) return;
        Log.Info($"Cor da pasta {path}: {dlg.Selected}");

        if (inDrive)
        {
            if (dlg.Selected == "yellow") _settings.FolderColors.Remove(key!);
            else _settings.FolderColors[key!] = dlg.Selected;
            _settings.Save();
            ShellIntegration.SetColors(_settings.FolderColors);   // icon-dirs.txt: vale também para as próximas montagens
            ShellIntegration.WriteColorIni(key!, dlg.Selected);
            var parent = key!.Contains('/') ? key[..key.LastIndexOf('/')] : "";
            // Só a pasta e a mãe: remontar faria o Explorer aberto no M: perder os ícones de tudo
            if (!await _rclone.SetIconDirs(ShellIntegration.IconDirs) || !await _rclone.RefreshDirs(key, parent)) { await ApplyLibraries(); return; }
            SHChangeNotify(0x00002000 /* SHCNE_UPDATEITEM */, 0x0005 /* SHCNF_PATHW */, path, IntPtr.Zero);
            SHChangeNotify(0x00001000 /* SHCNE_UPDATEDIR */, 0x0005 /* SHCNF_PATHW */, Path.GetDirectoryName(path)!, IntPtr.Zero);
            return;
        }

        var ini = Path.Combine(path, "desktop.ini");
        if (File.Exists(ini)) File.SetAttributes(ini, FileAttributes.Normal);
        WritePrivateProfileString(".ShellClassInfo", "IconResource",
            dlg.Selected == "yellow" ? null : $"{ShellIntegration.ColorIcon(dlg.Selected)},0", ini);
        if (File.Exists(ini)) File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
        // O Windows só lê o desktop.ini de pastas marcadas como somente leitura
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        SHChangeNotify(0x00002000 /* SHCNE_UPDATEITEM */, 0x0005 /* SHCNF_PATHW */, path, IntPtr.Zero);
    }

    static string LocalColor(string folder)
    {
        var sb = new System.Text.StringBuilder(1024);
        GetPrivateProfileString(".ShellClassInfo", "IconResource", "", sb, sb.Capacity, Path.Combine(folder, "desktop.ini"));
        var icon = sb.ToString();
        var id = Path.GetFileNameWithoutExtension(icon.Split(',')[0]);
        return icon.Contains(@"\foldercolors\", StringComparison.OrdinalIgnoreCase) && FolderColorDialog.Palette.Any(p => p.Id == id) ? id : "yellow";
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool WritePrivateProfileString(string section, string key, string? value, string file);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int GetPrivateProfileString(string section, string key, string def, System.Text.StringBuilder value, int size, string file);

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);
}
