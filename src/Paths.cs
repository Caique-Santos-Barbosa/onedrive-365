using System.Text.Json;

namespace HdtDrive;

static class Brand
{
    public const string Name = "OneDrive 365";
    public const string OneDriveFolder = "Meu OneDrive";
    public const string SharePointFolder = "SharePoint";
}

static class Paths
{
    static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static readonly string AppDir = Path.Combine(LocalAppData, Brand.Name);
    public static readonly string LegacyAppDir = Path.Combine(LocalAppData, "HDT Drive");

    public static string InstalledExe => Path.Combine(AppDir, "OneDrive365.exe");
    public static string Rclone => Path.Combine(AppDir, "bin", "rclone.exe");
    public static string RcloneConfig => Path.Combine(AppDir, "rclone.conf");
    public static string CacheDir => Path.Combine(AppDir, "cache");
    public static string LogsDir => Path.Combine(AppDir, "logs");
    public static string AppLog => Path.Combine(LogsDir, "app.log");
    public static string MountLog => Path.Combine(LogsDir, "mount.log");
    public static string SettingsFile => Path.Combine(AppDir, "settings.json");
    /// Lista de pastas com ícone (desktop.ini) que o rclone relê sem remontar (RCLONE_CMOUNT_ICON_DIRS_FILE).
    public static string IconDirsFile => Path.Combine(AppDir, "icon-dirs.txt");
    public static string OverlayDir => Path.Combine(AppDir, "overlay");
    public static string DriveIcon => Path.Combine(AppDir, "drive.ico");
    public static string BisyncDir => Path.Combine(AppDir, "bisync");
}

static class Log
{
    static readonly object Gate = new();

    public static void Info(string msg) => Write("INFO", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERRO", ex == null ? msg : $"{msg}: {ex}");

    static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.LogsDir);
                File.AppendAllText(Paths.AppLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {msg}{Environment.NewLine}");
            }
        }
        catch (IOException) { /* log é best-effort: não derruba o app por falta de disco */ }
    }
}

public sealed class Library
{
    public string Name { get; set; } = "";
    public string DriveId { get; set; } = "";
    public string Root { get; set; } = "";      // subpasta quando o usuário sincroniza só uma pasta
    public string WebUrl { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public string Site { get; set; } = "";
}

public sealed class Settings
{
    public bool InterceptSync { get; set; } = true;
    public string? OriginalOdopenCommand { get; set; }
    public string? DriveLetter { get; set; }
    public List<Library> Libraries { get; set; } = new();
    public List<BackupFolder> Backups { get; set; } = new();
    public bool ElevationDeclined { get; set; }
    /// Letra que está nas exclusões do Microsoft Defender (null = ainda não excluída).
    public string? DefenderExcludedLetter { get; set; }
    public bool BackupDialogShown { get; set; }
    public bool TrayPromoted { get; set; }
    public string? LastEmail { get; set; }
    /// Sincronizar só entre ScheduleFrom e ScheduleTo (hora local, "HH:mm"; pode virar a meia-noite).
    public bool ScheduleEnabled { get; set; }
    public string ScheduleFrom { get; set; } = "19:00";
    public string ScheduleTo { get; set; } = "07:00";
    public string LinkScope { get; set; } = "anonymous";   // padrão do "Copiar Link" (como o OneDrive da HDT)
    public string LinkRole { get; set; } = "edit";
    /// Cor das pastas no M: (caminho relativo à unidade, com '/') -> id da cor
    public Dictionary<string, string> FolderColors { get; set; } = new();

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile)) ?? new();
        }
        catch (Exception ex) { Log.Error("settings.json inválido, usando padrão", ex); }
        return new();
    }

    public void Save()
    {
        lock (this)   // o backup salva a partir de threads de timer
        {
        Directory.CreateDirectory(Paths.AppDir);
        var tmp = Paths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, Paths.SettingsFile, overwrite: true);
        }
    }
}
