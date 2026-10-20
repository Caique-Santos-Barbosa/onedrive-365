using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace HdtDrive;

/// Instalação por usuário (sem admin), exceto o driver WinFsp que pede UAC uma única vez.
static class Installer
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\OneDrive365";
    const string LegacyUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\HDTDrive";
    const string OdopenKey = @"Software\Classes\odopen";
    const string AppName = Brand.Name;
    const string LegacyName = "HDT Drive";

    static string StartMenuShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

    static bool IsOurCommand(string? cmd) =>
        cmd != null && (cmd.Contains("OneDrive365.exe", StringComparison.OrdinalIgnoreCase) ||
                        cmd.Contains("HDTDrive.exe", StringComparison.OrdinalIgnoreCase));

    /// Instalação feita com o nome antigo ("HDT Drive"): move login, cache e configurações para a pasta nova.
    /// Chamado com a instância antiga já encerrada.
    public static void MigrateLegacy()
    {
        var legacyConfig = Path.Combine(Paths.LegacyAppDir, "rclone.conf");
        if (!File.Exists(legacyConfig) || File.Exists(Paths.RcloneConfig)) return;

        // Copia (não move): a versão antiga pode ainda estar soltando arquivos ao encerrar
        Directory.CreateDirectory(Paths.AppDir);
        File.Copy(legacyConfig, Paths.RcloneConfig);
        var legacySettings = Path.Combine(Paths.LegacyAppDir, "settings.json");
        if (File.Exists(legacySettings)) File.Copy(legacySettings, Paths.SettingsFile, overwrite: true);
        try
        {
            // Cache traz uploads pendentes, que o rclone retoma na próxima montagem
            var legacyCache = Path.Combine(Paths.LegacyAppDir, "cache");
            if (Directory.Exists(legacyCache) && !Directory.Exists(Paths.CacheDir)) Directory.Move(legacyCache, Paths.CacheDir);
        }
        catch (IOException ex) { Log.Error("Cache antigo não migrado", ex); }

        var settings = Settings.Load();
        settings.DriveLetter = null;   // a unidade passa a preferir M:
        settings.Save();
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            run?.DeleteValue(LegacyName, throwOnMissingValue: false);
        Registry.CurrentUser.DeleteSubKeyTree(LegacyUninstallKey, throwOnMissingSubKey: false);
        File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), LegacyName + ".lnk"));
        Log.Info($"Migrado de {Paths.LegacyAppDir}");
        RemoveLegacyDir();
    }

    /// Apaga a pasta da versão antiga depois da migração (tenta de novo a cada início se algo ainda a segura).
    public static void RemoveLegacyDir()
    {
        if (!Directory.Exists(Paths.LegacyAppDir) || !File.Exists(Paths.RcloneConfig)) return;
        // rclone da versão antiga que ficou pendurado segura a pasta
        foreach (var p in Process.GetProcessesByName("rclone").Where(p => IsUnder(p, Paths.LegacyAppDir)))
        {
            Log.Info($"Encerrando rclone da versão antiga (pid {p.Id})");
            p.Kill();
            p.WaitForExit(5000);   // Kill é assíncrono: o exe continua travado até o processo sair
        }
        try { Directory.Delete(Paths.LegacyAppDir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Info($"Pasta antiga ainda em uso: {ex.Message}"); }
    }

    public static void Install(Settings settings)
    {
        Directory.CreateDirectory(Paths.AppDir);
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.Rclone)!);

        var self = Environment.ProcessPath!;
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(Paths.InstalledExe), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(self, Paths.InstalledExe, overwrite: true);
            Log.Info($"Copiado para {Paths.InstalledExe}");
        }

        ExtractIfChanged("rclone.exe", Paths.Rclone);
        foreach (var name in Assembly.GetExecutingAssembly().GetManifestResourceNames())
            foreach (var folder in new[] { "menuicons", "foldercolors" })
                if (name.StartsWith(folder + "."))
                {
                    Directory.CreateDirectory(Path.Combine(Paths.AppDir, folder));
                    ExtractIfChanged(name, Path.Combine(Paths.AppDir, folder, name[(folder.Length + 1)..]));
                }

        var exe = $"\"{Paths.InstalledExe}\"";
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            run.SetValue(AppName, $"{exe} --autostart");

        using (var un = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            un.SetValue("DisplayName", AppName);
            un.SetValue("DisplayVersion", Application.ProductVersion.Split('+')[0]);
            un.SetValue("Publisher", "Caique Santos Barbosa");
            un.SetValue("DisplayIcon", ShellIntegration.CloudIconResource);
            un.SetValue("InstallLocation", Paths.AppDir);
            un.SetValue("UninstallString", $"{exe} --uninstall");
            un.SetValue("NoModify", 1, RegistryValueKind.DWord);
            un.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }

        CreateShortcut();
        ApplySyncInterception(settings);
        RemoveLegacyDir();
    }

    /// Faz o botão "Sincronizar" do SharePoint (link odopen://) abrir este app em vez do OneDrive.
    /// Reaplicado a cada início porque atualizações do OneDrive podem reescrever a chave.
    public static void ApplySyncInterception(Settings settings)
    {
        var current = ReadCommand(Registry.CurrentUser);
        var ours = IsOurCommand(current);

        if (!ours && current != null)
        {
            settings.OriginalOdopenCommand = current;   // OneDrive instalado por usuário registra em HKCU
            settings.Save();
        }
        settings.OriginalOdopenCommand ??= ReadCommand(Registry.LocalMachine);

        if (settings.InterceptSync)
        {
            using var k = Registry.CurrentUser.CreateSubKey(OdopenKey);
            k.SetValue("", "URL:OneDrive Protocol");
            k.SetValue("URL Protocol", "");
            using var cmd = k.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", $"\"{Paths.InstalledExe}\" \"%1\"");
        }
        else if (ours)
        {
            RestoreOdopen(settings);
        }
    }

    static void RestoreOdopen(Settings settings)
    {
        var hklm = ReadCommand(Registry.LocalMachine);
        if (settings.OriginalOdopenCommand != null && settings.OriginalOdopenCommand != hklm)
        {
            using var cmd = Registry.CurrentUser.CreateSubKey(OdopenKey + @"\shell\open\command");
            cmd.SetValue("", settings.OriginalOdopenCommand);
        }
        else
        {
            Registry.CurrentUser.DeleteSubKeyTree(OdopenKey, throwOnMissingSubKey: false);   // volta a valer o HKLM
        }
    }

    static string? ReadCommand(RegistryKey hive)
    {
        using var k = hive.OpenSubKey(OdopenKey + @"\shell\open\command");
        return k?.GetValue("") as string;
    }

    /// Repassa um link odopen:// que não é "sincronizar" para o OneDrive original.
    public static void ForwardToOneDrive(Settings settings, string url)
    {
        var template = settings.OriginalOdopenCommand ?? ReadCommand(Registry.LocalMachine);
        if (template == null || IsOurCommand(template))
        {
            Log.Error($"Sem handler original do OneDrive para repassar {url}");
            return;
        }
        var (exe, args) = SplitCommand(template.Replace("%1", url));
        Log.Info($"Repassando ao OneDrive: {url}");
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
    }

    static (string exe, string args) SplitCommand(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            return (cmd[1..end], cmd[(end + 1)..].Trim());
        }
        var sp = cmd.IndexOf(' ');
        return sp < 0 ? (cmd, "") : (cmd[..sp], cmd[(sp + 1)..]);
    }

    public static bool WinFspInstalled()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\WinFsp");
        return k?.GetValue("InstallDir") is string dir && File.Exists(Path.Combine(dir, "bin", "winfsp-x64.dll"));
    }

    public static bool NeedsElevatedSetup() => !WinFspInstalled() || !Overlays.MachineRegistered() || !MenuPackage.CertificateTrusted();

    /// Pede UAC uma vez e roda "--elevated-setup". Devolve false se o usuário recusar.
    public static bool RunElevatedSetup()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(Paths.InstalledExe, "--elevated-setup") { UseShellExecute = true, Verb = "runas" });
            p!.WaitForExit();
            Log.Info($"Configuração elevada terminou com {p.ExitCode}");
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)   // usuário cancelou o UAC
        {
            Log.Info("Usuário recusou a permissão de administrador");
            return false;
        }
    }

    /// Roda já elevado: instala o WinFsp (se faltar) e registra os selos de status.
    public static int ElevatedSetup()
    {
        try
        {
            if (!WinFspInstalled())
            {
                var msi = Path.Combine(Path.GetTempPath(), "onedrive365-winfsp.msi");
                ExtractIfChanged("winfsp.msi", msi);
                var p = Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{msi}\" /qb /norestart") { UseShellExecute = false });
                p!.WaitForExit();
                try { File.Delete(msi); } catch (IOException) { }
                if (!WinFspInstalled()) return 2;
            }
            Overlays.RegisterMachine();
            MenuPackage.TrustCertificate();   // menu "OneDrive 365" no menu compacto do Windows 11
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Configuração elevada falhou", ex);
            return 1;
        }
    }

    static void ExtractIfChanged(string resource, string target)
    {
        using var src = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Recurso {resource} ausente no executável");
        if (File.Exists(target) && new FileInfo(target).Length == src.Length) return;
        using var dst = File.Create(target);
        src.CopyTo(dst);
    }

    static void CreateShortcut()
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(shellType)!;
            var lnk = shell.CreateShortcut(StartMenuShortcut);
            lnk.TargetPath = Paths.InstalledExe;
            lnk.IconLocation = ShellIntegration.CloudIconResource;
            lnk.Description = "OneDrive e SharePoint sob demanda";
            lnk.Save();
        }
        catch (Exception ex) { Log.Error("Não criei o atalho no Menu Iniciar", ex); }
    }

    public static int Uninstall(Func<string, bool> sendToRunning)
    {
        if (MessageBox.Show("Remover o OneDrive 365 deste computador?\n\nA unidade sai do Explorer e o botão Sincronizar do SharePoint volta a abrir o OneDrive. Seus arquivos continuam na nuvem.",
                AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return 1;

        sendToRunning("quit");
        foreach (var p in Process.GetProcessesByName("OneDrive365").Where(p => p.Id != Environment.ProcessId))
            p.WaitForExit(30_000);
        foreach (var p in Process.GetProcessesByName("rclone").Where(IsOurs))
            p.Kill();

        var settings = Settings.Load();
        if (IsOurCommand(ReadCommand(Registry.CurrentUser)))
            RestoreOdopen(settings);
        ShellIntegration.RemoveAll(settings.DriveLetter);
        Overlays.RemovePerUser();
        TrayApp.RemoveContextMenu();
        MenuPackage.UnregisterAsync().Wait(TimeSpan.FromSeconds(60));
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--elevated-cleanup") { UseShellExecute = true, Verb = "runas" })?.WaitForExit(); }
        catch (System.ComponentModel.Win32Exception) { Log.Info("Selos não removidos do HKLM: UAC recusado"); }
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            run?.DeleteValue(AppName, throwOnMissingValue: false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        File.Delete(StartMenuShortcut);

        // O exe em execução não pode se apagar: um cmd destacado limpa a pasta depois que sairmos
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 /nobreak >nul & rmdir /s /q \"{Paths.AppDir}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        });
        MessageBox.Show("OneDrive 365 removido.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    static bool IsOurs(Process p) => IsUnder(p, Paths.AppDir);

    // Process.MainModule falha em processos em certos estados; essa API só precisa de acesso mínimo
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    static bool IsUnder(Process p, string dir)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, p.Id);
        if (h == IntPtr.Zero) return false;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) &&
                   sb.ToString().StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseHandle(h); }
    }
}
