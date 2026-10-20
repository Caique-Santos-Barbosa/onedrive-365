using System.IO.Pipes;

namespace HdtDrive;

static class Program
{
    static readonly string PipeName = $"HDTDrive.{Environment.UserName}";

    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // Rede de segurança: erro inesperado vai para o log e o app segue na bandeja (nunca a caixa técnica do .NET)
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("Erro não tratado na interface", e.Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Erro não tratado em tarefa", e.Exception); e.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Erro fatal", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        TaskbarIdentity.ApplyToProcess();

        if (args.Contains("--uninstall"))
            return Installer.Uninstall(SendToRunning);

        // Processo elevado (UAC): driver WinFsp + registro dos selos em HKLM
        if (args.Contains("--elevated-setup")) return Installer.ElevatedSetup(ArgValue(args, "--letter"), ArgValue(args, "--old-letter"));
        if (args.Contains("--elevated-cleanup"))
        {
            Overlays.UnregisterMachine();
            MenuPackage.UntrustCertificate();
            if (ArgValue(args, "--letter") is { } letter) Installer.DefenderExclusion(letter, add: false);
            return 0;
        }

        if (args.Length == 2 && args[0] == "--write-auth-page")   // diagnóstico: gera a página de login num idioma
        {
            Directory.CreateDirectory(Paths.AppDir);
            File.WriteAllText(Path.Combine(Paths.AppDir, "auth-preview.txt"), AuthPage.Write(new System.Globalization.CultureInfo(args[1])));
            return 0;
        }

        var url = args.FirstOrDefault(a => a.StartsWith("odopen:", StringComparison.OrdinalIgnoreCase))
                  ?? (args.Contains("--add-all") ? "add-all" : null)
                  ?? (args.Length == 2 && args[0] == "--backup" ? "backup:" + args[1] : null)
                  ?? (args.Length == 3 && args[0] == "--action" ? $"action:{args[1]}|{args[2]}" : null);   // menu do botão direito

        using var mutex = new Mutex(true, $@"Local\HDTDrive.{Environment.UserName}", out var first);
        if (!first && args.Contains("--restart"))
        {
            // Relaunch(): a instância anterior está encerrando; espera ela soltar o mutex
            try { first = mutex.WaitOne(TimeSpan.FromSeconds(60)); }
            catch (AbandonedMutexException) { first = true; }
        }
        if (!first && url == null && !IsInstalledCopy())
        {
            // Exe baixado de novo (atualização): encerra a versão instalada e assume o lugar dela
            SendToRunning("quit");
            try { first = mutex.WaitOne(TimeSpan.FromSeconds(60)); }
            catch (AbandonedMutexException) { first = true; }
        }
        if (!first)
        {
            // Já tem uma instância rodando: entrega o link pra ela e sai. Fomos abertos pelo Explorer (que está
            // na frente), então repassamos o direito de ir para a frente: a janela que ela abrir não fica atrás
            AllowSetForegroundWindow(-1 /* ASFW_ANY */);
            SendToRunning(url ?? "show");
            return 0;
        }

        try { Installer.MigrateLegacy(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Migração da instalação antiga falhou", ex); }

        if (!IsInstalledCopy())
        {
            // Rodando do Downloads/pendrive: instala a cópia e entrega pra ela (não prende o arquivo baixado)
            try
            {
                Directory.CreateDirectory(Paths.AppDir);
                // A versão antiga solta o mutex um instante antes de o Windows soltar o arquivo
                for (var attempt = 1; ; attempt++)
                {
                    try { File.Copy(Environment.ProcessPath!, Paths.InstalledExe, overwrite: true); break; }
                    catch (IOException) when (attempt < 20) { Thread.Sleep(500); }
                }
                Log.Info($"Copiado para {Paths.InstalledExe}");
                mutex.ReleaseMutex();
                System.Diagnostics.Process.Start(Paths.InstalledExe, args);
                return 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("Não consegui instalar a cópia; seguindo a partir deste exe", ex);
            }
        }

        try
        {
            // Aberto pelo usuário (não pelo logon do Windows): mostra a unidade no Explorer quando montar
            var openWhenReady = url == null && !args.Contains("--autostart");
            Application.Run(new TrayApp(url, PipeName, openWhenReady));
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Falha fatal", ex);
            MessageBox.Show($"O OneDrive 365 parou por um erro:\n\n{ex.Message}\n\nDetalhes em {Paths.AppLog}",
                Brand.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    /// Abre uma instância nova que assume quando esta terminar de encerrar (chame Quit logo depois).
    public static void Relaunch() =>
        System.Diagnostics.Process.Start(IsInstalledCopy() ? Paths.InstalledExe : Environment.ProcessPath!, "--restart");

    /// Valor depois de um argumento ("--letter M:" -> "M:"); null se não houver.
    static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static bool IsInstalledCopy() =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath!), Path.GetFullPath(Paths.InstalledExe), StringComparison.OrdinalIgnoreCase);

    public static bool SendToRunning(string message)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            using var w = new StreamWriter(client);
            w.WriteLine(message);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Não consegui falar com a instância em execução", ex);
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);
}
