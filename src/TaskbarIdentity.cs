using System.Runtime.InteropServices;

namespace HdtDrive;

/// Identidade do app na barra de tarefas. O Windows 11 desenha o botão com o ícone do .exe, não o da janela;
/// com AppUserModelID + RelaunchIconResource ele usa a nuvem do OneDrive (imageres.dll) sem embuti-la no exe.
static class TaskbarIdentity
{
    public const string AppId = "HDTEnergy.OneDrive365";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    /// Chamar no início do processo, antes de criar janelas.
    public static void ApplyToProcess() => SetCurrentProcessExplicitAppUserModelID(AppId);

    public static void ApplyToWindow(IntPtr hwnd)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0) return;
        try
        {
            // As três Relaunch* precisam existir juntas para o ícone valer
            Set(store, PKEY_AppUserModel_ID, AppId);
            Set(store, PKEY_RelaunchCommand, $"\"{Paths.InstalledExe}\"");
            Set(store, PKEY_RelaunchDisplayNameResource, Brand.Name);
            Set(store, PKEY_RelaunchIconResource, ShellIntegration.CloudIconResource);
            store.Commit();
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    static void Set(IPropertyStore store, PropertyKey key, string value)
    {
        var pv = new PropVariant { vt = 31 /* VT_LPWSTR */, p = Marshal.StringToCoTaskMemUni(value) };
        try { store.SetValue(ref key, ref pv); }
        finally { Marshal.FreeCoTaskMem(pv.p); }
    }

    static readonly Guid AumidFmt = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    static readonly PropertyKey PKEY_AppUserModel_ID = new(AumidFmt, 5);
    static readonly PropertyKey PKEY_RelaunchCommand = new(AumidFmt, 2);
    static readonly PropertyKey PKEY_RelaunchIconResource = new(AumidFmt, 3);
    static readonly PropertyKey PKEY_RelaunchDisplayNameResource = new(AumidFmt, 4);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
        public PropertyKey(Guid f, uint p) { fmtid = f; pid = p; }
    }

    [StructLayout(LayoutKind.Explicit)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr p;
        [FieldOffset(16)] readonly IntPtr pad;   // PROPVARIANT tem 24 bytes em x64
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [DllImport("shell32.dll")]
    static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
}
