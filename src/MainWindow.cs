using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HdtDrive;

/// Janela principal (estilo "Drive para computador"): interface em HTML (ui.html) num WebView2.
/// A página manda comandos com chrome.webview.postMessage({cmd, ...}); o app responde com o estado em JSON.
sealed class MainWindow : Form
{
    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    readonly Func<object> _state;
    readonly Action<string, JsonElement> _command;
    bool _ready;
    string? _pendingPage;

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public MainWindow(Func<object> state, Action<string, JsonElement> command)
    {
        _state = state;
        _command = command;
        Text = Brand.Name;
        // Tamanho fixo como o "Drive para computador" (escolhido pelo usuário): 1024x700 a 100%,
        // escalado pelo DPI do monitor onde a janela abre
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.None;   // o tamanho é calculado aqui; evita escala dupla
        HandleCreated += (_, _) =>
        {
            ClientSize = new Size(DeviceDpi * 1024 / 96, DeviceDpi * 700 / 96);
            ShellIntegration.CenterOnActiveScreen(this);
        };
        DpiChanged += (_, e) => { e.Cancel = true; ClientSize = new Size(e.DeviceDpiNew * 1024 / 96, e.DeviceDpiNew * 700 / 96); };
        StartPosition = FormStartPosition.Manual;   // centralizado depois de ter o tamanho certo
        BackColor = Color.FromArgb(0xF3, 0xF5, 0xF8);
        using (var ico = ShellIntegration.OneDriveIcon(32)) if (ico != null) Icon = (Icon)ico.Clone();
        Controls.Add(_web);
        Load += async (_, _) => await InitAsync();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        TaskbarIdentity.ApplyToWindow(Handle);   // nuvem do OneDrive no botão da barra de tarefas
    }

    async Task InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Paths.AppDir, "webview"));
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this, "Esta janela precisa do Microsoft Edge WebView2, que vem no Windows 10 e 11 atualizados.",
                Brand.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };   // links externos no navegador
        core.WebMessageReceived += (_, e) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var cmd = doc.RootElement.GetProperty("cmd").GetString()!;
                if (cmd == "ready") { _ready = true; Push(); if (_pendingPage != null) Navigate(_pendingPage); return; }
                _command(cmd, doc.RootElement.Clone());
            }
            catch (Exception ex) { Log.Error("Comando da interface", ex); }
        };

        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ui.html")!;
        core.NavigateToString(new StreamReader(s).ReadToEnd());
    }

    /// Envia o estado atual para a página (chamar sempre que algo mudar).
    public void Push()
    {
        if (!_ready || IsDisposed) return;
        try { _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "state", state = _state() }, Json)); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    public void Send(string type, object payload)
    {
        if (!_ready || IsDisposed) return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type, payload }, Json));
    }

    /// Abre numa página: home, activity, notifications, folders, sharepoint, preferences, about.
    public void Navigate(string page)
    {
        if (!_ready) { _pendingPage = page; return; }
        Send("navigate", page);
    }

    public void BringToFront(string? page)
    {
        if (page != null) Navigate(page);
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        Activate();
    }

    /// Diagnóstico: digita uma busca na página como se fosse o usuário.
    public Task TypeSearchAsync(string q) =>
        _web.CoreWebView2.ExecuteScriptAsync(
            $"document.querySelector('#q').value = {JsonSerializer.Serialize(q)}; document.querySelector('#searchForm').requestSubmit();");

    /// Diagnóstico: clica num elemento da página (seletor CSS).
    public Task ClickAsync(string selector) =>
        _web.CoreWebView2.ExecuteScriptAsync($"document.querySelector({JsonSerializer.Serialize(selector)})?.click();");

    /// Diagnóstico: captura a página num PNG.
    public async Task CaptureAsync(string png)
    {
        for (var i = 0; i < 100 && !_ready; i++) await Task.Delay(100);   // janela recém-criada: espera a página carregar
        if (!_ready) throw new InvalidOperationException("A janela ainda não carregou");
        using var fs = File.Create(png);
        await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
    }

    /// Abre o app de e-mail padrão (normalmente o Outlook) com o link no corpo.
    public static void OpenMail(string subject, string body) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            $"mailto:?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}") { UseShellExecute = true });

    public static void OpenExternal(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http"))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }
}
