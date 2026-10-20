# Gera src\assets\app.ico com o ícone do OneDrive.exe instalado (todas as resoluções, cópia byte a byte do recurso).
# Retorna $false se o OneDrive não estiver instalado (o build cai no make-icon.ps1).
param([string]$Out = (Join-Path $PSScriptRoot '..\src\assets\app.ico'))
$exe = @("$env:ProgramFiles\Microsoft OneDrive\OneDrive.exe", "$env:LOCALAPPDATA\Microsoft\OneDrive\OneDrive.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $exe) { return $false }

Add-Type -TypeDefinition @'
using System; using System.IO; using System.Runtime.InteropServices;
public static class ExeIcon {
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryEx(string f, IntPtr h, uint fl);
    [DllImport("kernel32")] static extern bool FreeLibrary(IntPtr h);
    [DllImport("kernel32")] static extern IntPtr FindResource(IntPtr h, IntPtr name, IntPtr type);
    [DllImport("kernel32")] static extern IntPtr LoadResource(IntPtr h, IntPtr r);
    [DllImport("kernel32")] static extern IntPtr LockResource(IntPtr r);
    [DllImport("kernel32")] static extern uint SizeofResource(IntPtr h, IntPtr r);
    delegate bool EnumNames(IntPtr h, IntPtr type, IntPtr name, IntPtr p);
    [DllImport("kernel32")] static extern bool EnumResourceNames(IntPtr h, IntPtr type, EnumNames cb, IntPtr p);

    static byte[] Res(IntPtr m, IntPtr name, int type) {
        var r = FindResource(m, name, (IntPtr)type);
        var b = new byte[SizeofResource(m, r)];
        Marshal.Copy(LockResource(LoadResource(m, r)), b, 0, b.Length);
        return b;
    }

    // Primeiro RT_GROUP_ICON (o ícone que o Explorer mostra) convertido para .ico
    public static void Save(string exe, string output) {
        var m = LoadLibraryEx(exe, IntPtr.Zero, 0x2 | 0x20);   // DATAFILE | IMAGE_RESOURCE
        if (m == IntPtr.Zero) throw new IOException("Não abriu " + exe);
        try {
            IntPtr first = IntPtr.Zero; bool found = false;
            EnumResourceNames(m, (IntPtr)14, (h, t, n, p) => { first = n; found = true; return false; }, IntPtr.Zero);
            if (!found) throw new IOException("Sem ícone em " + exe);
            var grp = Res(m, first, 14);
            int count = BitConverter.ToUInt16(grp, 4);
            var images = new byte[count][];
            for (int i = 0; i < count; i++) images[i] = Res(m, (IntPtr)BitConverter.ToUInt16(grp, 6 + i * 14 + 12), 3);
            using var w = new BinaryWriter(File.Create(output));
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)count);
            int offset = 6 + count * 16;
            for (int i = 0; i < count; i++) {
                w.Write(grp, 6 + i * 14, 12);   // largura, altura, cores, reservado, planos, bits, tamanho
                w.Write(offset); offset += images[i].Length;
            }
            foreach (var img in images) w.Write(img);
        } finally { FreeLibrary(m); }
    }
}
'@
[ExeIcon]::Save($exe, [IO.Path]::GetFullPath($Out))
return $true
