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
        // Trava de posição: o Windows reescala a janela ao trocar de tela (WM_DPICHANGED) e ela descia para trás da
        // barra de tarefas. Qualquer mudança de tamanho/posição enquanto aberto volta para o retângulo calculado.
        DpiChanged += (_, e) => { e.Cancel = true; Pin(); };
        Move += (_, _) => Pin();
        Resize += (_, _) => Pin();
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
        _target = PanelRect();
        Bounds = _target;
        Show();
        Pin();
        Activate();
        Push();
        if (_ready) _web.CoreWebView2.PostWebMessageAsJson("{\"type\":\"shown\"}");
    }

    Rectangle _target;
    (string Screen, int X)? _anchor;   // último clique no ícone: abre sempre no mesmo lugar, mesmo vindo de outro caminho

    void Pin()
    {
        if (Visible && !_target.IsEmpty && Bounds != _target) Bounds = _target;
    }

    /// Acima do ícone da bandeja, na tela da barra de tarefas e com a escala DESSA tela, sempre dentro da área útil.
    Rectangle PanelRect()
    {
        var p = Cursor.Position;
        var screen = Screen.FromPoint(p);
        if (!screen.WorkingArea.Contains(p)) _anchor = (screen.DeviceName, p.X);   // clicou na barra de tarefas
        else if (_anchor is { } a && Screen.AllScreens.FirstOrDefault(x => x.DeviceName == a.Screen) is { } s0) screen = s0;
        else screen = Screen.PrimaryScreen ?? screen;
        var area = screen.WorkingArea;
        var anchorX = _anchor is { } b && b.Screen == screen.DeviceName ? b.X : area.Right;

        var dpi = MonitorDpi(screen);
        int S(int v) => dpi * v / 96;
        var size = new Size(Math.Min(S(380), area.Width - S(24)), Math.Min(S(640), area.Height - S(24)));
        var x = Math.Clamp(anchorX - size.Width / 2, area.Left + S(12), area.Right - size.Width - S(12));
        var taskbarOnTop = area.Top > screen.Bounds.Top && area.Bottom == screen.Bounds.Bottom;
        var y = taskbarOnTop ? area.Top + S(12) : area.Bottom - size.Height - S(12);
        return new Rectangle(x, y, size.Width, size.Height);
    }

    static int MonitorDpi(Screen screen)
    {
        var c = screen.Bounds;
        var mon = MonitorFromPoint(new Point(c.Left + c.Width / 2, c.Top + c.Height / 2), 2 /* MONITOR_DEFAULTTONEAREST */);
        return GetDpiForMonitor(mon, 0 /* MDT_EFFECTIVE_DPI */, out var dx, out _) == 0 && dx > 0 ? (int)dx : 96;
    }

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(Point pt, uint flags);

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

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
