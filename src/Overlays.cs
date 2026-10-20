using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace HdtDrive;

/// Selos de status no Explorer (nuvem / no PC / sincronizando) via extensão nativa OneDrive365Shell.dll.
static class Overlays
{
    const string StateKey = @"Software\OneDrive365";
    const string IdentifiersKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers";
    const string ApprovedKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved";

    // O Explorer só usa os ~15 primeiros identificadores em ordem alfabética: os tabs iniciais nos põem na frente
    static readonly (string Clsid, string Name)[] Handlers =
    {
        ("{B1E6F0A0-5C3D-4E7A-9B21-0D365A0E0001}", "\t\t\t\tOneDrive365 1 Sincronizando"),
        ("{B1E6F0A0-5C3D-4E7A-9B21-0D365A0E0002}", "\t\t\t\tOneDrive365 2 NoPC"),
        ("{B1E6F0A0-5C3D-4E7A-9B21-0D365A0E0003}", "\t\t\t\tOneDrive365 3 Nuvem"),
    };

    static readonly string[] Files = { "OneDrive365Shell.dll", "overlay-sync.ico", "overlay-local.ico", "overlay-cloud.ico" };

    public static string PendingFile => Path.Combine(Paths.AppDir, "status", "pending.txt");

    /// Copia a DLL para uma pasta por versão (o Explorer mantém a antiga carregada) e aponta os CLSIDs (HKCU) para ela.
    public static void InstallPerUser(string letter)
    {
        var dll = Resource("OneDrive365Shell.dll");
        var version = Convert.ToHexString(SHA256.HashData(dll))[..12];
        var dir = Path.Combine(Paths.AppDir, "shell", version);
        var repaired = false;
        Directory.CreateDirectory(dir);
        foreach (var f in Files.Where(f => !File.Exists(Path.Combine(dir, f))))
            File.WriteAllBytes(Path.Combine(dir, f), Resource(f));

        // Versões antigas: o Explorer pode estar com a DLL velha carregada, e ela entrega ao Explorer os ícones de selo
        // da PRÓPRIA pasta. Apagar só os .ico (a DLL travada não sai) fazia todo item do M: aparecer com o ícone de
        // arquivo genérico até o Explorer reiniciar. Regra: a pasta só sai se a DLL sair primeiro; senão fica inteira.
        foreach (var old in Directory.GetDirectories(Path.Combine(Paths.AppDir, "shell")).Where(d => d != dir))
        {
            var oldDll = Path.Combine(old, Files[0]);
            try
            {
                if (File.Exists(oldDll)) File.Delete(oldDll);
                Directory.Delete(old, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Em uso: garante os ícones ao lado dela (repara instalações que já perderam os .ico)
                foreach (var icon in Files.Skip(1).Where(i => !File.Exists(Path.Combine(old, i))))
                {
                    try { File.WriteAllBytes(Path.Combine(old, icon), Resource(icon)); repaired = true; }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error($"Selo {icon} em {old}", e); }
                }
            }
        }
        if (repaired)
        {
            // O Explorer guardou a falha de quando os ícones sumiram: manda recarregar a lista de ícones
            SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0x1000 /* SHCNF_IDLIST | SHCNF_FLUSH */, IntPtr.Zero, IntPtr.Zero);
            Log.Info("Selos de status reparados para o Explorer em execução");
        }

        foreach (var (clsid, name) in Handlers)
        {
            using var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{clsid}");
            k.SetValue("", name.Trim());
            using var s = k.CreateSubKey("InprocServer32");
            s.SetValue("", Path.Combine(dir, Files[0]));
            s.SetValue("ThreadingModel", "Apartment");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(PendingFile)!);
        using var state = Registry.CurrentUser.CreateSubKey(StateKey);
        state.SetValue("DriveLetter", letter);
        state.SetValue("CacheRoot", Path.Combine(Paths.CacheDir, "vfs", "m365"));
        state.SetValue("PendingFile", PendingFile);
    }

    public static bool MachineRegistered()
    {
        using var k = Registry.LocalMachine.OpenSubKey(IdentifiersKey);
        return Handlers.All(h => k?.OpenSubKey(h.Name) != null);
    }

    /// Parte que exige administrador (HKLM). Roda no processo elevado "--elevated-setup".
    public static void RegisterMachine()
    {
        using var ids = Registry.LocalMachine.CreateSubKey(IdentifiersKey);
        using var approved = Registry.LocalMachine.CreateSubKey(ApprovedKey);
        foreach (var (clsid, name) in Handlers)
        {
            using (var k = ids.CreateSubKey(name)) k.SetValue("", clsid);
            approved.SetValue(clsid, name.Trim());
        }
    }

    public static void UnregisterMachine()
    {
        using var ids = Registry.LocalMachine.OpenSubKey(IdentifiersKey, writable: true);
        using var approved = Registry.LocalMachine.OpenSubKey(ApprovedKey, writable: true);
        foreach (var (clsid, name) in Handlers)
        {
            ids?.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
            approved?.DeleteValue(clsid, throwOnMissingValue: false);
        }
    }

    public static void RemovePerUser()
    {
        foreach (var (clsid, _) in Handlers)
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\CLSID\{clsid}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(StateKey, throwOnMissingSubKey: false);
    }

    static HashSet<string> _published = new(StringComparer.OrdinalIgnoreCase);

    /// Publica a fila de upload para a extensão e avisa o Explorer dos arquivos que mudaram de estado.
    public static void PublishPending(IEnumerable<string> queue, string letter)
    {
        var now = queue.Select(q => q.Replace('/', '\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (now.SetEquals(_published)) return;

        var tmp = PendingFile + ".tmp";
        File.WriteAllText(tmp, string.Join("\n", now), new UTF8Encoding(false));
        File.Move(tmp, PendingFile, overwrite: true);

        var changed = now.Except(_published).Concat(_published.Except(now)).ToList();
        _published = now;
        foreach (var rel in changed)
        {
            // O item e as pastas acima dele (que mostram "sincronizando" enquanto algo dentro sobe)
            var path = Path.Combine(letter + "\\", rel);
            for (var p = path; p.Length > 3; p = Path.GetDirectoryName(p)!)
                SHChangeNotify(0x00002000 /* SHCNE_UPDATEITEM */, 0x0005 /* SHCNF_PATHW */, p, IntPtr.Zero);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    static byte[] Resource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Recurso {name} ausente");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
