using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HdtDrive;

/// Painel da bandeja (ui/flyout.html), como o do OneDrive: status, arquivos recentes, Abrir pasta / Ver online / Lixeira.
/// Abre colado na bandeja e some ao perder o foco.
sealed class Flyout : Form
{
    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    readonly Func<object> _state;
    readonly Action<string, JsonElement> _command;
    bool _ready;
    DateTime _hiddenAt;

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public Flyout(Func<object> state, Action<string, JsonElement> command)
    {
        _state = state;
        _command = command;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(0xF9, 0xF9, 0xF9);
        Controls.Add(_web);
        Deactivate += (_, _) => HidePanel();
        Load += async (_, _) => { if (!await WebViewEnv.Run(this, InitAsync)) Close(); };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000;      // CS_DROPSHADOW
            cp.ExStyle |= 0x80;            // WS_EX_TOOLWINDOW: fora do Alt+Tab
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var round = 2;   // DWMWCP_ROUND: cantos arredondados do Windows 11
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    async Task InitAsync()
    {
        var env = await WebViewEnv.Get();
        await _web.EnsureCoreWebView2Async(env);
        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.WebMessageReceived += (_, e) =>
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var cmd = doc.RootElement.GetProperty("cmd").GetString()!;
            if (cmd == "ready") { _ready = true; Push(); return; }
            if (cmd == "hide") { HidePanel(); return; }
            // Ação que abre outra janela: some primeiro, como o painel do OneDrive
            if (cmd is not "pause") HidePanel();
            _command(cmd, doc.RootElement.Clone());
        };
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("flyout.html")!;
        core.NavigateToString(new StreamReader(s).ReadToEnd());
    }

    public void Push()
    {
        if (!_ready || IsDisposed || !Visible) return;
        try { _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "state", state = _state() }, Json)); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    /// Mostra/esconde a partir do clique no ícone da bandeja.
    public void Toggle()
    {
        // O clique no ícone tira o foco do painel antes de chegar aqui: sem isso ele fecharia e reabriria
        if (Visible || DateTime.Now - _hiddenAt < TimeSpan.FromMilliseconds(250)) { HidePanel(); return; }
        int S(int v) => DeviceDpi * v / 96;
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var size = new Size(S(380), Math.Min(S(640), area.Height - S(24)));
        // Encostado no canto da área de trabalho perto da bandeja (barra embaixo, em cima, ou dos lados)
        var x = Math.Clamp(Cursor.Position.X - size.Width / 2, area.Left + S(12), area.Right - size.Width - S(12));
        var y = Cursor.Position.Y > area.Bottom - S(4) || Cursor.Position.Y > area.Top + area.Height / 2
            ? area.Bottom - size.Height - S(12)
            : area.Top + S(12);
        Bounds = new Rectangle(x, y, size.Width, size.Height);
        Show();
        Activate();
        Push();
        if (_ready) _web.CoreWebView2.PostWebMessageAsJson("{\"type\":\"shown\"}");
    }

    /// Diagnóstico: captura o conteúdo do painel num PNG.
    public async Task CaptureAsync(string png)
    {
        using var fs = File.Create(png);
        await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
    }

    void HidePanel()
    {
        if (!Visible) return;
        Hide();
        _hiddenAt = DateTime.Now;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
