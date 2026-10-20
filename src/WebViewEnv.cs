using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace HdtDrive;

/// Ambiente WebView2 único para todas as janelas. Se o runtime não estiver instalado (Windows 10 antigo),
/// baixa o instalador oficial da Microsoft, confere a assinatura e instala antes de seguir.
static class WebViewEnv
{
    // Evergreen Bootstrapper oficial (link permanente da Microsoft)
    const string BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
    static Task<CoreWebView2Environment>? _env;

    public static Task<CoreWebView2Environment> Get() => _env ??= Create();

    static async Task<CoreWebView2Environment> Create()
    {
        if (!RuntimeInstalled())
        {
            Log.Info("WebView2 ausente: instalando o runtime da Microsoft");
            try { await InstallRuntime(); }
            catch (Exception e) { _env = null; throw new WebView2RuntimeNotFoundException("Não foi possível instalar o WebView2: " + e.Message, e); }
            if (!RuntimeInstalled()) { _env = null; throw new WebView2RuntimeNotFoundException("O WebView2 não ficou disponível depois da instalação."); }
        }
        return await CoreWebView2Environment.CreateAsync(null, Path.Combine(Paths.AppDir, "webview"));
    }

    /// Roda a inicialização de uma janela WebView2; sem runtime (nem depois de tentar instalar), avisa e devolve false.
    public static async Task<bool> Run(Form owner, Func<Task> init)
    {
        try { await init(); return true; }
        catch (WebView2RuntimeNotFoundException e)
        {
            Log.Error("WebView2", e);
            MessageBox.Show(owner, "Esta janela precisa do Microsoft Edge WebView2, e não foi possível instalá-lo agora.\n\n" +
                "Instale pelo site da Microsoft (procure por \"WebView2 Runtime\") e abra o OneDrive 365 de novo.",
                Brand.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    static bool RuntimeInstalled()
    {
        try { return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()); }
        catch (WebView2RuntimeNotFoundException) { return false; }
    }

    static async Task InstallRuntime()
    {
        var setup = Path.Combine(Path.GetTempPath(), $"MicrosoftEdgeWebview2Setup-{Guid.NewGuid():N}.exe");
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            await using (var file = File.Create(setup))
                await (await http.GetStreamAsync(BootstrapperUrl)).CopyToAsync(file);
            if (!SignedByMicrosoft(setup)) throw new InvalidOperationException("instalador do WebView2 sem assinatura válida da Microsoft");
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(setup, "/silent /install") { UseShellExecute = true })!;
            await p.WaitForExitAsync();
            Log.Info($"Instalador do WebView2 terminou com {p.ExitCode}");
        }
        finally
        {
            try { File.Delete(setup); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// Assinatura Authenticode válida (WinVerifyTrust) e emitida para a Microsoft Corporation.
    static bool SignedByMicrosoft(string path)
    {
        if (WinTrust.Verify(path) != 0) return false;
        try
        {
#pragma warning disable SYSLIB0057   // CreateFromSignedFile: o único jeito simples de ler o signatário Authenticode
            using var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return cert.Subject.Contains("O=Microsoft Corporation", StringComparison.Ordinal);
        }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
    }

    static class WinTrust
    {
        static readonly Guid ActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct FileInfo { public uint cbStruct; public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

        [StructLayout(LayoutKind.Sequential)]
        struct Data
        {
            public uint cbStruct; public IntPtr pPolicyCallbackData; public IntPtr pSIPClientData; public uint dwUIChoice;
            public uint fdwRevocationChecks; public uint dwUnionChoice; public IntPtr pFile; public uint dwStateAction;
            public IntPtr hWVTStateData; public IntPtr pwszURLReference; public uint dwProvFlags; public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref Data data);

        /// 0 = assinatura válida e confiável
        public static int Verify(string path)
        {
            var file = new FileInfo { cbStruct = (uint)Marshal.SizeOf<FileInfo>(), pcwszFilePath = path };
            var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
            try
            {
                Marshal.StructureToPtr(file, pFile, false);
                var data = new Data
                {
                    cbStruct = (uint)Marshal.SizeOf<Data>(), dwUIChoice = 2 /* WTD_UI_NONE */, fdwRevocationChecks = 0,
                    dwUnionChoice = 1 /* WTD_CHOICE_FILE */, pFile = pFile, dwStateAction = 0, dwProvFlags = 0x80 /* WTD_SAFER_FLAG */,
                };
                return WinVerifyTrust(new IntPtr(-1), ActionGenericVerifyV2, ref data);
            }
            finally
            {
                Marshal.DestroyStructure<FileInfo>(pFile);
                Marshal.FreeHGlobal(pFile);
            }
        }
    }
}
