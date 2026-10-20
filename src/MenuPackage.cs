using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;

namespace HdtDrive;

/// Menu "OneDrive 365" no menu compacto do Windows 11: pacote MSIX sparse (identidade) + OneDrive365Menu.dll.
/// O certificado do pacote vai para LocalMachine\TrustedPeople na etapa de administrador.
static class MenuPackage
{
    const string PackageName = "HDTEnergy.OneDrive365";
    static string MenuDll => Path.Combine(Paths.AppDir, "OneDrive365Menu.dll");
    static string Msix => Path.Combine(Paths.AppDir, "package", "OneDrive365.msix");

    static byte[] Resource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Recurso {name} ausente");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    static X509Certificate2 Certificate => new(Resource("menu.cer"));

    public static bool CertificateTrusted()
    {
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, Certificate.Thumbprint, false).Count > 0;
    }

    /// Roda elevado (junto com WinFsp e selos).
    public static void TrustCertificate()
    {
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        store.Add(Certificate);
    }

    public static void UntrustCertificate()
    {
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        foreach (var c in store.Certificates.Find(X509FindType.FindByThumbprint, Certificate.Thumbprint, false)) store.Remove(c);
    }

    /// Copia a DLL e registra o pacote para o usuário se a versão embutida mudou. Não bloqueia a inicialização.
    public static async Task EnsureRegisteredAsync()
    {
        if (!CertificateTrusted()) { Log.Info("Menu do Windows 11: certificado ainda não confiável (falta a etapa de administrador)"); return; }

        var dll = Resource("OneDrive365Menu.dll");
        if (!File.Exists(MenuDll) || !File.ReadAllBytes(MenuDll).AsSpan().SequenceEqual(dll))
        {
            // O Explorer mantém a DLL carregada: renomear um arquivo em uso é permitido, sobrescrever não
            if (File.Exists(MenuDll))
                try { File.Delete(MenuDll); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { File.Move(MenuDll, MenuDll + $".old-{DateTime.Now.Ticks}"); }
            File.WriteAllBytes(MenuDll, dll);
        }
        foreach (var old in Directory.GetFiles(Paths.AppDir, "OneDrive365Menu.dll.old-*"))
            try { File.Delete(old); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        var version = System.Text.Encoding.UTF8.GetString(Resource("menu-version.txt")).Trim();
        var installed = (await PowerShell($"(Get-AppxPackage -Name {PackageName}).Version")).Trim();
        if (installed == version) return;

        Directory.CreateDirectory(Path.GetDirectoryName(Msix)!);
        File.WriteAllBytes(Msix, Resource("OneDrive365.msix"));
        var output = await PowerShell($"Add-AppxPackage -Path '{Msix}' -ExternalLocation '{Paths.AppDir}' -ForceUpdateFromAnyVersion; if ($?) {{ 'ok' }}");
        Log.Info($"Menu do Windows 11: pacote {version} (antes: {(installed == "" ? "nenhum" : installed)}) -> {output.Trim()}");
    }

    public static Task UnregisterAsync() =>
        PowerShell($"Get-AppxPackage -Name {PackageName} | Remove-AppxPackage");

    static async Task<string> PowerShell(string script)
    {
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return output;
    }
}
