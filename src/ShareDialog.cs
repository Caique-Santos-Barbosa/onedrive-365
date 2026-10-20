using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HdtDrive;

/// Janela "Compartilhar" (ui/share.html): pessoas + mensagem, Copiar link com Configurações de link e Gerenciar acesso.
sealed class ShareDialog : Form
{
    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    readonly Graph.DriveItem _item;
    readonly Settings _settings;
    readonly string _org;
    readonly string _view;

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// view: main | settings | access
    public ShareDialog(Graph.DriveItem item, Settings settings, string org, string view = "main")
    {
        _item = item;
        _settings = settings;
        _org = org;
        _view = view;
        Text = $"Compartilhar \"{item.Name}\"";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.White;
        TopMost = true;   // aberto pelo menu do Explorer: precisa aparecer na frente
        using (var ico = ShellIntegration.OneDriveIcon(32)) if (ico != null) Icon = (Icon)ico.Clone();
        HandleCreated += (_, _) =>
        {
            ClientSize = new Size(Scale(620), Scale(view == "main" ? 360 : 640));
            ShellIntegration.CenterOnActiveScreen(this);
        };
        Controls.Add(_web);
        Load += async (_, _) => await InitAsync();
        Shown += (_, _) => { TopMost = false; Activate(); };
    }

    int Scale(int dip) => DeviceDpi * dip / 96;

    async Task InitAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Paths.AppDir, "webview"));
        await _web.EnsureCoreWebView2Async(env);
        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.WebMessageReceived += async (_, e) => await OnMessage(e.WebMessageAsJson);
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("share.html")!;
        core.NavigateToString(new StreamReader(s).ReadToEnd());
    }

    object LinkDefaults => new { scope = _settings.LinkScope, role = _settings.LinkRole, expires = "", password = "" };

    async Task OnMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var m = doc.RootElement;
        var cmd = m.GetProperty("cmd").GetString();
        string S(string n) => m.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        try
        {
            switch (cmd)
            {
                case "ready":
                    Post(new { type = "init", name = _item.Name, isFolder = _item.IsFolder, org = _org, link = LinkDefaults, view = _view });
                    break;
                case "resize":
                    var h = Math.Clamp(m.GetProperty("height").GetInt32(), 240, 760);
                    ClientSize = new Size(ClientSize.Width, Scale(h));
                    break;
                case "people":
                    var people = await Graph.People(S("q"));
                    Post(new { type = "people", list = people.Select(p => new { name = p.Name, email = p.Email }) });
                    break;
                case "invite":
                    var emails = m.GetProperty("emails").EnumerateArray().Select(e => e.GetString()!).ToList();
                    await Graph.Invite(_item, emails, S("role"), S("message"));
                    Post(new { type = "invite", ok = true, count = emails.Count });
                    break;
                case "link":
                case "outlook":
                    var url = await LinkFor(S("scope"), S("role"), S("expires"), S("password"));
                    Clipboard.SetText(url);
                    if (cmd == "outlook") MainWindow.OpenMail(_item.Name, url);
                    Post(new { type = "link", ok = true, url });
                    break;
                case "saveLinkDefaults":
                    _settings.LinkScope = S("scope");
                    _settings.LinkRole = S("role");
                    _settings.Save();
                    break;
                case "permissions":
                    var perms = await Graph.Permissions(_item);
                    Post(new
                    {
                        type = "permissions", ok = true,
                        list = perms.Select(p => new { id = p.Id, kind = p.Kind, name = p.Name, detail = p.Detail, role = p.Role, inherited = p.Inherited }),
                    });
                    break;
                case "deletePermission":
                    await Graph.DeletePermission(_item, S("id"));
                    Post(new { type = "changed", ok = true });
                    break;
                case "stopSharing":
                    foreach (var p in (await Graph.Permissions(_item)).Where(p => p.Kind != "owner" && !p.Inherited))
                        await Graph.DeletePermission(_item, p.Id);
                    Post(new { type = "changed", ok = true });
                    break;
                case "close":
                    Close();
                    break;
            }
        }
        catch (HttpRequestException ex)
        {
            Log.Error($"Compartilhar {cmd} {_item.Name}", ex);
            Post(new { type = cmd is "deletePermission" or "stopSharing" ? "changed" : cmd == "outlook" ? "link" : cmd, ok = false, error = ex.Message });
        }
    }

    /// Link conforme as Configurações de link. "Acesso existente" não cria permissão: é o endereço do item.
    async Task<string> LinkFor(string scope, string role, string expires, string password)
    {
        if (scope == "existing") return _item.WebUrl;
        DateTime? exp = DateTime.TryParse(expires, out var d) ? d.Date.AddDays(1).AddSeconds(-1) : null;
        var type = role == "view" ? "view" : "edit";
        try { return await Graph.CreateLink(_item, type, scope, exp, password); }
        catch (HttpRequestException) when (scope == "anonymous" && exp == null && string.IsNullOrEmpty(password))
        {
            // Empresa bloqueou links "Alguém": cai para pessoas da organização em vez de falhar
            return await Graph.CreateLink(_item, type, "organization");
        }
    }

    void Post(object o) => _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(o, Json));
}
