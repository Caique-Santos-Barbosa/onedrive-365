using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HdtDrive;

/// A montagem anterior não terminou (processo preso no driver). Só um reinício do Windows libera.
sealed class MountStuckException : Exception
{
    public MountStuckException() : base(
        "A unidade não foi desmontada corretamente e ficou presa no Windows.\n\n" +
        "Reinicie o computador para liberá-la. Seus arquivos estão seguros: envios pendentes continuam no cache e sobem quando a unidade voltar.") { }
}

/// Controla o rclone: login, remotes e o processo de montagem (o "GoogleDriveFS.exe" daqui).
sealed class Rclone
{
    Process? _mount;
    bool _stopping;
    int _rcPort;
    readonly HttpClient _rc = new() { Timeout = TimeSpan.FromSeconds(5) };

    public event Action<string>? MountDied;
    public bool Running => _mount is { HasExited: false };

    public static Task<(int code, string output)> Run(TimeSpan timeout, params string[] args) =>
        Run(timeout, CancellationToken.None, args);

    public static async Task<(int code, string output)> Run(TimeSpan timeout, CancellationToken cancel, params string[] args)
    {
        var psi = new ProcessStartInfo(Paths.Rclone)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(Paths.RcloneConfig);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true);
            return (-1, "tempo esgotado");
        }
        return (p.ExitCode, await stdout + await stderr);
    }

    /// Roda o rclone entregando cada linha de log (stderr) enquanto ele trabalha. Devolve o código de saída.
    public static async Task<int> RunStreaming(TimeSpan timeout, CancellationToken cancel, Action<string> onLine, params string[] args)
    {
        var psi = new ProcessStartInfo(Paths.Rclone)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(Paths.RcloneConfig);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var reader = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardError.ReadLineAsync()) != null)
            {
                try { onLine(line); }
                catch (Exception ex) { Log.Error("Linha do rclone", ex); }
            }
        });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true);
            return -1;
        }
        await reader;
        await stdout;
        return p.ExitCode;
    }

    public static bool HasRemote(string name) =>
        File.Exists(Paths.RcloneConfig) && File.ReadAllText(Paths.RcloneConfig).Contains($"[{name}]");

    static string? _lastAuthUrl;

    /// Abre o navegador no login da Microsoft (com o e-mail já preenchido, se informado) e grava o token
    /// no rclone.conf do app.
    public static async Task<bool> Login(string? email = null, CancellationToken cancel = default)
    {
        Log.Info("Iniciando login OneDrive");
        var errors = new List<string>();
        var code = await RunStreaming(TimeSpan.FromMinutes(15), cancel, line =>
        {
            // A saída traz o token no fim: só olhamos o link de login e as linhas de erro
            var m = System.Text.RegularExpressions.Regex.Match(line, @"http://127\.0\.0\.1:\d+/auth\?state=[\w-]+");
            if (m.Success) _ = OpenLoginPage(m.Value, email);
            else if (line.Contains("ERROR") || line.Contains("Failed")) errors.Add(line);
        },
            "config", "create", "onedrive", "onedrive", "region=global",
            // Escopos padrão do rclone + User.Read (nome e foto) + People.Read (sugestões ao compartilhar)
            "access_scopes=Files.Read Files.ReadWrite Files.Read.All Files.ReadWrite.All Sites.Read.All offline_access User.Read People.Read",
            "config_template_file=" + AuthPage.Write(),   // página "login concluído" no idioma do Windows
            "config_auth_no_browser=true");               // o app abre o navegador, com login_hint (--auth-no-open-browser só existe no "authorize")
        Log.Info($"Login terminou com {code} {string.Join(" | ", errors)}");
        return code == 0 && HasRemote("onedrive");
    }

    /// Reabre a página de login (botão "Abrir o navegador de novo" do assistente).
    public static Task ReopenLoginPage(string? email) => _lastAuthUrl == null ? Task.CompletedTask : OpenLoginPage(_lastAuthUrl, email);

    /// O endereço local do rclone redireciona para o login da Microsoft: pegamos o destino e acrescentamos
    /// login_hint (e-mail pré-preenchido), como o instalador do OneDrive faz.
    static async Task OpenLoginPage(string localAuthUrl, string? email)
    {
        _lastAuthUrl = localAuthUrl;
        var target = localAuthUrl;
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
            using var resp = await http.GetAsync(localAuthUrl);
            if (resp.Headers.Location is { } loc)
            {
                target = loc.ToString();
                if (!string.IsNullOrWhiteSpace(email))
                    target += (target.Contains('?') ? "&" : "?") + "login_hint=" + Uri.EscapeDataString(email.Trim());
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { Log.Error("Não li o redirecionamento do login", ex); }
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    /// "hdt:" = Meu OneDrive + SharePoint (uma pasta por biblioteca).
    public static async Task WriteRemotes(IReadOnlyList<Library> libraries)
    {
        var top = new List<string> { $"\"{Brand.OneDriveFolder}=onedrive:\"" };
        if (libraries.Count > 0)
        {
            // Override no connection string reaproveita o token do remote "onedrive" (um login só)
            var ups = libraries.Select(l =>
                $"\"{l.Name}=onedrive,drive_id={l.DriveId},drive_type=documentLibrary:{l.Root}\"");
            await Check("config", "create", "sharepoint", "combine", "upstreams=" + string.Join(" ", ups), "--non-interactive");
            top.Add($"\"{Brand.SharePointFolder}=sharepoint:\"");
        }
        await Check("config", "create", "hdt", "combine", "upstreams=" + string.Join(" ", top), "--non-interactive");

        // "m365:" = hdt: + pasta local só-leitura com os desktop.ini dos ícones das pastas
        ShellIntegration.WriteOverlay(libraries);
        await Check("config", "create", "m365", "union", $"upstreams=hdt: \"{Paths.OverlayDir}:ro\"",
            "create_policy=epff", "action_policy=epall", "search_policy=ff", "--non-interactive");
    }

    static async Task Check(params string[] args)
    {
        var (code, output) = await Run(TimeSpan.FromSeconds(30), args);
        if (code != 0) throw new InvalidOperationException($"rclone {args[0]} {args[1]} falhou: {output.Trim()}");
    }

    public async Task Start(string letter)
    {
        // Letra ainda ocupada = montagem anterior não saiu; montar por cima falharia ou pioraria
        for (var i = 0; i < 20 && Directory.Exists(letter + "\\"); i++) await Task.Delay(500);
        if (Directory.Exists(letter + "\\")) throw new MountStuckException();

        _rcPort = FreePort();
        var user = "hdt";
        var pass = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _rc.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{user}:{pass}")));

        Directory.CreateDirectory(Paths.LogsDir);
        var psi = new ProcessStartInfo(Paths.Rclone) { UseShellExecute = false, CreateNoWindow = true };
        psi.Environment["RCLONE_CMOUNT_ICON_DIRS"] = ShellIntegration.IconDirs;   // patch nosso no rclone (ver tools/rclone-patch)
        psi.Environment["RCLONE_CMOUNT_ICON_DIRS_FILE"] = Paths.IconDirsFile;     // relido quando muda: cor de pasta sem remontar
        foreach (var a in new[]
        {
            "mount", "m365:", letter,
            "--config", Paths.RcloneConfig,
            "--volname", Brand.Name,
            "--vfs-cache-mode", "full",          // baixa só o que é aberto, guarda em cache
            "--vfs-cache-max-size", "20G",
            "--vfs-cache-max-age", "168h",
            "--cache-dir", Paths.CacheDir,
            "--dir-cache-time", "5m",            // listagens em memória; 5m cobre exclusões que o delta do Business não informa
            "--poll-interval", "1m",             // arquivos novos/alterados na nuvem chegam via delta a cada minuto
            "--rc", "--rc-addr", $"127.0.0.1:{_rcPort}", "--rc-user", user, "--rc-pass", pass,
            "--log-level", "NOTICE", "--log-file", Paths.MountLog,
        }) psi.ArgumentList.Add(a);

        _stopping = false;
        _mount = Process.Start(psi)!;
        _mount.EnableRaisingEvents = true;
        _mount.Exited += (_, _) =>
        {
            if (!_stopping) MountDied?.Invoke($"rclone saiu com código {_mount.ExitCode}");
        };
        Log.Info($"rclone mount iniciado (pid {_mount.Id}) em {letter}");

        for (var i = 0; i < 60; i++)
        {
            if (Directory.Exists(letter + "\\"))
            {
                // Janelas do Explorer abertas na unidade durante a remontagem ficam com o ícone genérico em tudo
                ShellIntegration.RefreshExplorerWindows(letter);
                return;
            }
            if (_mount.HasExited) throw new InvalidOperationException($"Montagem falhou. Veja {Paths.MountLog}");
            await Task.Delay(500);
        }
        throw new TimeoutException($"A unidade {letter} não apareceu em 30s. Veja {Paths.MountLog}");
    }

    /// Para a montagem. Uploads pendentes ficam no cache e o rclone retoma na próxima montagem.
    public async Task Stop(TimeSpan waitForUploads)
    {
        if (!Running) return;
        var deadline = DateTime.UtcNow + waitForUploads;
        while (DateTime.UtcNow < deadline && (await Stats())?.Uploading > 0)
            await Task.Delay(1000);

        _stopping = true;
        try { await _rc.PostAsync($"http://127.0.0.1:{_rcPort}/core/quit", null); }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }

        // Desmontagem limpa pode demorar com janelas abertas na unidade: forçar cedo demais já deixou
        // o rclone preso dentro do driver WinFsp, e aí qualquer Explorer novo trava ao listar as unidades.
        if (!await WaitExit(_mount!, TimeSpan.FromSeconds(30)))
        {
            Log.Error($"rclone (pid {_mount!.Id}) não saiu em 30s; forçando");
            _mount.Kill(entireProcessTree: true);
            if (!await WaitExit(_mount, TimeSpan.FromSeconds(10)))
            {
                Log.Error($"rclone (pid {_mount.Id}) ficou preso no driver após Kill");
                throw new MountStuckException();
            }
        }
        Log.Info("rclone mount parado");
    }

    static async Task<bool> WaitExit(Process p, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(cts.Token); return true; }
        catch (OperationCanceledException) { return false; }
    }

    public sealed record VfsStats(int Uploading, int Errored);

    public async Task<VfsStats?> Stats()
    {
        if (!Running) return null;
        try
        {
            var r = await _rc.PostAsync($"http://127.0.0.1:{_rcPort}/vfs/stats", null);
            if (r.StatusCode != HttpStatusCode.OK) return null;
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("diskCache", out var dc)) return new(0, 0);
            return new(dc.GetProperty("uploadsInProgress").GetInt32() + dc.GetProperty("uploadsQueued").GetInt32(),
                       dc.GetProperty("erroredFiles").GetInt32());
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
    }

    /// Troca na hora a lista de pastas com ícone (mount/icondirs, patch nosso), sem remontar.
    public async Task<bool> SetIconDirs(string list)
    {
        if (!Running) return false;
        try
        {
            var r = await _rc.PostAsync($"http://127.0.0.1:{_rcPort}/mount/icondirs",
                new StringContent(JsonSerializer.Serialize(new { dirs = list }), System.Text.Encoding.UTF8, "application/json"));
            return r.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { Log.Error("mount/icondirs", e); return false; }
    }

    /// Relê do remoto só estas pastas (caminhos relativos à unidade), sem remontar.
    public async Task<bool> RefreshDirs(params string[] dirs)
    {
        if (!Running || dirs.Length == 0) return false;
        var args = new Dictionary<string, string> { ["recursive"] = "false" };
        for (var i = 0; i < dirs.Length; i++) args[i == 0 ? "dir" : $"dir{i + 1}"] = dirs[i].Replace('\\', '/').Trim('/');
        try
        {
            var r = await _rc.PostAsync($"http://127.0.0.1:{_rcPort}/vfs/refresh", new StringContent(JsonSerializer.Serialize(args), System.Text.Encoding.UTF8, "application/json"));
            if (r.StatusCode == HttpStatusCode.OK) return true;
            Log.Info($"vfs/refresh {string.Join(", ", dirs)}: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
            return false;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { Log.Error("vfs/refresh", e); return false; }
    }

    public sealed record QueueItem(string Name, long Size);

    /// Arquivos (caminho relativo à unidade, tamanho) na fila de upload do cache.
    public async Task<IReadOnlyCollection<QueueItem>?> UploadQueue()
    {
        if (!Running) return null;
        try
        {
            var r = await _rc.PostAsync($"http://127.0.0.1:{_rcPort}/vfs/queue", null);
            if (r.StatusCode != HttpStatusCode.OK) return null;
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("queue").EnumerateArray()
                .Select(e => new QueueItem(e.GetProperty("name").GetString()!, e.TryGetProperty("size", out var s) ? s.GetInt64() : 0)).ToList();
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
