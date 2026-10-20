using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HdtDrive;

/// Assistente da primeira execução (ui/wizard.html): e-mail → login no navegador → unidade M: → backup → pronto.
sealed class WizardWindow : Form
{
    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    readonly object _init;
    readonly Action<string, JsonElement> _command;
    bool _ready;
    readonly List<string> _pending = new();

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public WizardWindow(object init, Action<string, JsonElement> command)
    {
        _init = init;
        _command = command;
        Text = Brand.Name;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.White;
        using (var ico = ShellIntegration.OneDriveIcon(32)) if (ico != null) Icon = (Icon)ico.Clone();
        HandleCreated += (_, _) =>
        {
            ClientSize = new Size(DeviceDpi * 590 / 96, DeviceDpi * 528 / 96);   // mesmo tamanho do assistente do OneDrive
            ShellIntegration.CenterOnActiveScreen(this);
            TaskbarIdentity.ApplyToWindow(Handle);
        };
        DpiChanged += (_, e) => { e.Cancel = true; ClientSize = new Size(e.DeviceDpiNew * 590 / 96, e.DeviceDpiNew * 528 / 96); };
        Controls.Add(_web);
        Load += async (_, _) => await InitAsync();
    }

    async Task InitAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Paths.AppDir, "webview"));
        await _web.EnsureCoreWebView2Async(env);
        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.WebMessageReceived += (_, e) =>
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var cmd = doc.RootElement.GetProperty("cmd").GetString()!;
            if (cmd == "ready")
            {
                _ready = true;
                PostInit();
                foreach (var p in _pending) core.PostWebMessageAsJson(p);
                _pending.Clear();
                return;
            }
            _command(cmd, doc.RootElement.Clone());
        };
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("wizard.html")!;
        core.NavigateToString(new StreamReader(s).ReadToEnd());
    }

    /// Envia uma mensagem à página; se ela ainda não carregou, guarda para depois.
    public void Send(object message)
    {
        if (IsDisposed) return;
        var json = JsonSerializer.Serialize(message, Json);
        if (_ready) _web.CoreWebView2.PostWebMessageAsJson(json);
        else _pending.Add(json);
    }

    void PostInit()
    {
        // Objeto de inicialização + {type: "init"}
        var merged = JsonSerializer.SerializeToNode(_init, Json)!.AsObject();
        merged["type"] = "init";
        _web.CoreWebView2.PostWebMessageAsJson(merged.ToJsonString());
    }
}
