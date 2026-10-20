using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace HdtDrive;

/// Aparência no Explorer: ícone/nome da unidade, ícones das pastas (desktop.ini) e atalhos na barra lateral.
static class ShellIntegration
{
    const string DrivesKey = @"Software\Classes\Applications\Explorer.exe\Drives";
    const string NameSpaceKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace";
    const string HideDesktopKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";

    // Itens da barra lateral do Explorer (GUIDs fixos deste app)
    static readonly (string Clsid, string Folder, bool SharePoint)[] NavItems =
    {
        ("{6E3F1C52-8B0A-4C61-9D2E-36A1F0B5C701}", Brand.OneDriveFolder, false),   // nuvem
        ("{6E3F1C52-8B0A-4C61-9D2E-36A1F0B5C702}", Brand.SharePointFolder, true),  // prédio
    };

    /// Pastas que o rclone marca como "somente leitura" para o Windows ler o desktop.ini delas.
    /// Pastas coloridas pelo usuário no M: (atualizado pelo app antes de montar).
    public static IReadOnlyCollection<string> ColoredFolders { get; set; } = Array.Empty<string>();

    public static string IconDirs => string.Join("|",
        new[] { Brand.OneDriveFolder, Brand.SharePointFolder, $"{Brand.SharePointFolder}/*" }.Concat(ColoredFolders));

    public static string ColorIcon(string colorId) => Path.Combine(Paths.AppDir, "foldercolors", colorId + ".ico");

    // Nuvem do OneDrive que vem no próprio Windows (imageres.dll), presente mesmo sem o OneDrive instalado.
    // Referenciada pelo ID do recurso, que não muda entre versões do Windows (o índice muda).
    static string ImageRes => Path.Combine(Environment.SystemDirectory, "imageres.dll");
    public static string CloudIconResource => $"{ImageRes},-1043";
    public static string SharePointIconFile => Path.Combine(Paths.AppDir, "sharepoint.ico");

    // Logo do SharePoint também vem no imageres.dll; Windows mais antigos sem ele usam o prédio do app
    static string SharePointIcon => HasImageResIcon(-8315) ? $"{ImageRes},-8315" : $"{SharePointIconFile},0";

    static string IconSource(bool sharePoint) => sharePoint ? SharePointIcon : CloudIconResource;

    static bool HasImageResIcon(int resourceId)
    {
        var handles = new IntPtr[1];
        if (PrivateExtractIcons(ImageRes, resourceId, 16, 16, handles, null, 1, 0) == 0 || handles[0] == IntPtr.Zero) return false;
        DestroyIcon(handles[0]);
        return true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, int[]? ids, uint count, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);

    /// Ilustração do assistente do OneDrive instalado no PC (images\lightTheme|darkTheme\<nome>.svg), em data URI.
    /// Lida da máquina na hora de mostrar — não vai dentro do nosso exe. Null se o OneDrive não estiver instalado.
    public static string? OneDriveArt(string name)
    {
        var exe = OneDriveExePath();
        if (exe == null) return null;
        var version = Directory.GetDirectories(Path.GetDirectoryName(exe)!)
            .Where(d => char.IsDigit(Path.GetFileName(d)[0]))
            .OrderByDescending(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version())
            .FirstOrDefault();
        if (version == null) return null;
        var theme = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0
            ? "darkTheme" : "lightTheme";
        var file = Path.Combine(version, "images", theme, name + ".svg");
        return File.Exists(file) ? "data:image/svg+xml;base64," + Convert.ToBase64String(File.ReadAllBytes(file)) : null;
    }

    static string? OneDriveExePath() => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft OneDrive", "OneDrive.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "OneDrive", "OneDrive.exe"),
    }.FirstOrDefault(File.Exists);

    /// Logo do SharePoint (32 px) para a interface.
    public static Icon? SharePointIcon32() =>
        HasImageResIcon(-8315) ? Extract(ImageRes, -8315, 32) : File.Exists(SharePointIconFile) ? new Icon(SharePointIconFile, 32, 32) : null;

    static Icon? Extract(string file, int id, int size)
    {
        var handles = new IntPtr[1];
        if (PrivateExtractIcons(file, id, size, size, handles, null, 1, 0) == 0 || handles[0] == IntPtr.Zero) return null;
        try { return (Icon)Icon.FromHandle(handles[0]).Clone(); }
        finally { DestroyIcon(handles[0]); }
    }

    /// Ícone grande do tipo de arquivo (".pdf", ".xlsx"...) sem precisar que o arquivo exista.
    public static Icon? FileTypeIcon(string extension)
    {
        var info = new SHFILEINFO();
        if (SHGetFileInfo(extension, 0x80 /* FILE_ATTRIBUTE_NORMAL */, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf(info),
                0x100 /* SHGFI_ICON */ | 0x10 /* SHGFI_USEFILEATTRIBUTES */) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;
        try { return (Icon)Icon.FromHandle(info.hIcon).Clone(); }
        finally { DestroyIcon(info.hIcon); }
    }

    /// Ícone que o Windows mostra para a pasta (Área de Trabalho, Documentos...).
    public static Icon? FolderIcon(string path)
    {
        var info = new SHFILEINFO();
        if (SHGetFileInfo(path, 0, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf(info), 0x100 /* SHGFI_ICON | LARGE */) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;
        try { return (Icon)Icon.FromHandle(info.hIcon).Clone(); }
        finally { DestroyIcon(info.hIcon); }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    /// Ícone da nuvem do OneDrive no tamanho pedido (do Windows); null se o recurso não existir.
    public static Icon? OneDriveIcon(int size)
    {
        var handles = new IntPtr[1];
        if (PrivateExtractIcons(ImageRes, -1043, size, size, handles, null, 1, 0) == 0 || handles[0] == IntPtr.Zero) return null;
        try { return (Icon)Icon.FromHandle(handles[0]).Clone(); }
        finally { DestroyIcon(handles[0]); }
    }

    /// Centraliza a janela no monitor onde está o mouse (chamar depois de definir o tamanho).
    public static void CenterOnActiveScreen(Form f)
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        f.Location = new Point(area.Left + Math.Max(0, (area.Width - f.Width) / 2), area.Top + Math.Max(0, (area.Height - f.Height) / 2));
    }

    public static void ApplyDrive(string letter)
    {
        using (var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("drive.ico")!)
        using (var dst = File.Create(Paths.DriveIcon))
            src.CopyTo(dst);

        var key = $@"{DrivesKey}\{letter.TrimEnd(':')}";
        using (var icon = Registry.CurrentUser.CreateSubKey(key + @"\DefaultIcon")) icon.SetValue("", Paths.DriveIcon);
        using (var label = Registry.CurrentUser.CreateSubKey(key + @"\DefaultLabel")) label.SetValue("", Brand.Name);
    }

    public static void RemoveDrive(string letter) =>
        Registry.CurrentUser.DeleteSubKeyTree($@"{DrivesKey}\{letter.TrimEnd(':')}", throwOnMissingSubKey: false);

    /// Pasta local montada por cima da unidade (somente leitura) só com os desktop.ini dos ícones.
    static Func<string, string?> ColorOf = _ => null;
    public static void SetColors(IReadOnlyDictionary<string, string> colors)
    {
        ColoredFolders = colors.Keys.ToList();
        ColorOf = k => colors.TryGetValue(k, out var c) ? c : null;
    }

    public static void WriteOverlay(IEnumerable<Library> libraries)
    {
        if (Directory.Exists(Paths.OverlayDir)) Directory.Delete(Paths.OverlayDir, recursive: true);
        using (var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("sharepoint.ico")!)
        using (var dst = File.Create(SharePointIconFile))
            src.CopyTo(dst);
        WriteDesktopIni(Brand.OneDriveFolder, IconSource(false));
        WriteDesktopIni(Brand.SharePointFolder, IconSource(true));
        foreach (var lib in libraries)
            WriteDesktopIni(Path.Combine(Brand.SharePointFolder, lib.Name), IconSource(true));
        foreach (var folder in ColoredFolders)
            if (ColorOf(folder) is { } color) WriteDesktopIni(folder.Replace('/', '\\'), $"{ColorIcon(color)},0");
    }

    static void WriteDesktopIni(string folder, string icon)
    {
        var dir = Path.Combine(Paths.OverlayDir, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "desktop.ini"),
            $"[.ShellClassInfo]\r\nIconResource={icon}\r\nConfirmFileOp=0\r\n", Encoding.Unicode);
    }

    public static void ApplyNavigationPane(string letter)
    {
        foreach (var (clsid, folder, sharePoint) in NavItems)
        {
            var name = folder;
            using (var c = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{clsid}"))
            {
                c.SetValue("", name);
                c.SetValue("System.IsPinnedToNameSpaceTree", 1, RegistryValueKind.DWord);
                c.SetValue("SortOrderIndex", 0x41, RegistryValueKind.DWord);
                using (var i = c.CreateSubKey("DefaultIcon")) i.SetValue("", IconSource(sharePoint));
                using (var s = c.CreateSubKey("InProcServer32")) s.SetValue("", @"%SystemRoot%\system32\shell32.dll", RegistryValueKind.ExpandString);
                using (var inst = c.CreateSubKey("Instance"))
                {
                    inst.SetValue("CLSID", "{0E5AAE11-A475-4c5b-AB00-C66DE400274E}");   // pasta do sistema de arquivos
                    using var bag = inst.CreateSubKey("InitPropertyBag");
                    bag.SetValue("Attributes", 0x11, RegistryValueKind.DWord);
                    bag.SetValue("TargetFolderPath", Path.Combine(letter + "\\", folder));
                }
                using (var sf = c.CreateSubKey("ShellFolder"))
                {
                    sf.SetValue("FolderValueFlags", 0x28, RegistryValueKind.DWord);
                    sf.SetValue("Attributes", unchecked((int)0xF080004D), RegistryValueKind.DWord);
                }
            }
            using (var ns = Registry.CurrentUser.CreateSubKey($@"{NameSpaceKey}\{clsid}")) ns.SetValue("", name);
            using (var hide = Registry.CurrentUser.CreateSubKey(HideDesktopKey)) hide.SetValue(clsid, 1, RegistryValueKind.DWord);
        }
    }

    public static void RemoveAll(string? letter)
    {
        foreach (var (clsid, _, _) in NavItems)
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\CLSID\{clsid}", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"{NameSpaceKey}\{clsid}", throwOnMissingSubKey: false);
            using var hide = Registry.CurrentUser.OpenSubKey(HideDesktopKey, writable: true);
            hide?.DeleteValue(clsid, throwOnMissingValue: false);
        }
        if (letter != null) RemoveDrive(letter);
    }
}
