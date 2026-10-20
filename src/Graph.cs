using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

namespace HdtDrive;

/// Microsoft Graph usando o mesmo token OAuth do rclone (um login só para tudo).
static class Graph
{
    static readonly HttpClient Http = new() { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };

    // Sites técnicos que não fazem sentido como "drive compartilhado"
    static readonly string[] SystemSites = { "/sites/appcatalog", "/sites/pwa", "/sites/contentTypeHub", "/contentstorage/" };

    static readonly string[] DefaultLibraryNames = { "Documentos", "Documents", "Shared Documents", "Documentos Compartilhados" };

    static async Task<string> AccessToken()
    {
        var tok = ReadToken();
        if (tok == null || tok.Value.expiry < DateTimeOffset.UtcNow.AddMinutes(2))
        {
            await Rclone.Run(TimeSpan.FromSeconds(60), "about", "onedrive:");   // força o refresh do token
            tok = ReadToken();
        }
        return tok?.access ?? throw new InvalidOperationException("Sem token do OneDrive. Entre na conta de novo.");
    }

    static (string access, DateTimeOffset expiry)? ReadToken()
    {
        using var fs = new FileStream(Paths.RcloneConfig, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var conf = new StreamReader(fs).ReadToEnd();
        var m = Regex.Match(conf, @"^\[onedrive\][^\[]*?^token\s*=\s*(.+)$", RegexOptions.Multiline);
        if (!m.Success) return null;
        using var doc = JsonDocument.Parse(m.Groups[1].Value);
        return (doc.RootElement.GetProperty("access_token").GetString()!,
                DateTimeOffset.Parse(doc.RootElement.GetProperty("expiry").GetString()!));
    }

    static async Task<JsonElement> Get(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessToken());
        using var resp = await Http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Graph {(int)resp.StatusCode} em {url}: {body[..Math.Min(body.Length, 300)]}", null, resp.StatusCode);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// false só quando o Graph responde que a biblioteca não existe ou não é acessível para a conta atual
    /// (outra conta/tenant, acesso removido). Sem internet ou erro temporário: true, para não apagar à toa.
    public static async Task<bool> LibraryAccessible(Library lib)
    {
        var path = string.IsNullOrEmpty(lib.Root) ? "root" : "root:/" + Uri.EscapeDataString(lib.Root).Replace("%2F", "/") + ":";
        try { await Get($"drives/{lib.DriveId}/{path}?$select=id"); return true; }
        catch (HttpRequestException e) when (e.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden
                                                 or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.BadRequest) { return false; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return true; }
    }

    static async Task<List<JsonElement>> GetAll(string url)
    {
        var all = new List<JsonElement>();
        string? next = url;
        while (next != null)
        {
            var page = await Get(next);
            all.AddRange(page.GetProperty("value").EnumerateArray());
            next = page.TryGetProperty("@odata.nextLink", out var n) ? n.GetString() : null;
        }
        return all;
    }

    /// Converte o link do botão "Sincronizar" (odopen://sync/?siteId=..&listId=..&webUrl=..) numa biblioteca.
    public static async Task<Library> ResolveSyncLink(string odopenUrl)
    {
        var q = HttpUtility.ParseQueryString(new Uri(odopenUrl).Query);
        var webUrl = q["webUrl"] ?? q["siteUrl"] ?? throw new ArgumentException("Link sem webUrl");
        var listId = (q["listId"] ?? throw new ArgumentException("Link sem listId")).Trim('{', '}');

        var web = new Uri(webUrl);
        var path = web.AbsolutePath.TrimEnd('/');
        var site = await Get($"sites/{web.Host}{(path.Length > 0 ? ":" + path : "")}?$select=id,displayName");
        var siteId = site.GetProperty("id").GetString()!;
        var siteName = site.GetProperty("displayName").GetString() ?? q["webTitle"] ?? web.Host;

        var drive = await Get($"sites/{siteId}/lists/{listId}/drive?$select=id,name,webUrl");
        var defaultDrive = await Get($"sites/{siteId}/drive?$select=id");
        var driveId = drive.GetProperty("id").GetString()!;
        var isDefault = driveId == defaultDrive.GetProperty("id").GetString();
        var lib = new Library
        {
            DriveId = driveId,
            WebUrl = drive.GetProperty("webUrl").GetString() ?? webUrl,
            Name = LibraryName(siteName, drive.GetProperty("name").GetString()!, isDefault),
        };

        // Sincronizar uma pasta específica: monta só a subpasta
        var folderUrl = q["folderUrl"];
        if (!string.IsNullOrEmpty(folderUrl))
        {
            var drivePath = Uri.UnescapeDataString(new Uri(lib.WebUrl).AbsolutePath).TrimEnd('/');
            var folderPath = Uri.UnescapeDataString(new Uri(new Uri(webUrl), folderUrl).AbsolutePath).TrimEnd('/');
            if (folderPath.StartsWith(drivePath + "/", StringComparison.OrdinalIgnoreCase))
            {
                lib.Root = folderPath[(drivePath.Length + 1)..];
                lib.Name = Sanitize($"{lib.Name} - {q["folderName"] ?? lib.Root.Split('/').Last()}");
            }
        }
        return lib;
    }

    /// Todas as bibliotecas de documentos que o usuário enxerga (para "Adicionar todas").
    public static async Task<List<Library>> AllLibraries()
    {
        var result = new List<Library>();
        foreach (var site in await GetAll("sites?search=*&$select=id,displayName,webUrl"))
        {
            var url = site.GetProperty("webUrl").GetString() ?? "";
            if (SystemSites.Any(s => url.Contains(s, StringComparison.OrdinalIgnoreCase)) || url.Contains("vivaengage", StringComparison.OrdinalIgnoreCase))
                continue;
            var siteId = site.GetProperty("id").GetString()!;
            try
            {
                var defaultId = (await Get($"sites/{siteId}/drive?$select=id")).GetProperty("id").GetString();
                foreach (var d in await GetAll($"sites/{siteId}/drives?$select=id,name,webUrl,driveType"))
                {
                    if (d.GetProperty("driveType").GetString() != "documentLibrary") continue;
                    var id = d.GetProperty("id").GetString()!;
                    result.Add(new Library
                    {
                        DriveId = id,
                        WebUrl = d.GetProperty("webUrl").GetString() ?? url,
                        Name = LibraryName(site.GetProperty("displayName").GetString() ?? url, d.GetProperty("name").GetString()!, id == defaultId),
                    });
                }
            }
            catch (HttpRequestException ex) { Log.Info($"Sem acesso a {url}: {ex.StatusCode}"); }
        }
        return result;
    }

    public sealed record Account(string Name, string Email, string? PhotoDataUri, long Used, long Total, string? ManageUrl, string? WebUrl);

    /// Conta conectada: nome, e-mail, foto e uso do armazenamento do OneDrive.
    public static async Task<Account> Me()
    {
        var drive = await Get("me/drive?$select=quota,webUrl,owner");
        var quota = drive.GetProperty("quota");
        var webUrl = drive.TryGetProperty("webUrl", out var w) ? w.GetString() : null;

        // Dono do OneDrive: funciona com as permissões de arquivos (logins antigos não pediram User.Read)
        var owner = drive.TryGetProperty("owner", out var o) && o.TryGetProperty("user", out var u0) ? u0 : default;
        var name = owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "";
        var email = owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty("email", out var em) ? em.GetString() ?? "" : "";
        string? photo = null;
        try
        {
            var me = await Get("me?$select=displayName,mail,userPrincipalName");   // precisa de User.Read
            name = me.GetProperty("displayName").GetString() ?? name;
            email = me.TryGetProperty("mail", out var m) && m.GetString() is { } mail ? mail : me.GetProperty("userPrincipalName").GetString() ?? email;
            photo = await PhotoDataUri();
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Unauthorized)
        {
            // Sem User.Read (logins antigos): a foto sai da cópia que o SharePoint guarda no site pessoal
            var ownerId = owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty("id", out var oid) ? oid.GetString() : null;
            if (ownerId != null && webUrl != null)
            {
                try { photo = await SharePointProfilePhoto(new Uri(webUrl).Host, ownerId); }
                catch (HttpRequestException) { }   // sem foto: a interface mostra as iniciais
            }
        }
        return new Account(
            name,
            email,
            photo,
            quota.TryGetProperty("used", out var u) ? u.GetInt64() : 0,
            quota.TryGetProperty("total", out var t) ? t.GetInt64() : 0,
            webUrl == null ? null : new Uri(new Uri(webUrl), "/_layouts/15/storman.aspx").ToString(),
            webUrl);
    }

    static async Task<string> PhotoDataUri()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "me/photos/96x96/$value");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessToken());
        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException("sem foto", null, resp.StatusCode);
        var type = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
        return $"data:{type};base64,{Convert.ToBase64String(await resp.Content.ReadAsByteArrayAsync())}";
    }

    /// Foto de perfil sincronizada pelo SharePoint em {tenant}-my: biblioteca "User Photos"/"Fotos do Usuário",
    /// pasta "Profile Pictures"/"Imagens de Perfil" (nomes variam com o idioma), arquivo {id do usuário}_LThumb.jpg.
    static async Task<string?> SharePointProfilePhoto(string myHost, string userId)
    {
        var site = await Get($"sites/{myHost}:/?$select=id");
        foreach (var drive in await GetAll($"sites/{site.GetProperty("id").GetString()}/drives?$select=id"))
        {
            var driveId = drive.GetProperty("id").GetString();
            foreach (var folder in await GetAll($"drives/{driveId}/root/children?$select=name,folder"))
            {
                if (!folder.TryGetProperty("folder", out _)) continue;
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"drives/{driveId}/root:/{Uri.EscapeDataString(folder.GetProperty("name").GetString()!)}/{userId}_LThumb.jpg:/content");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessToken());
                using var resp = await Http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                    return "data:image/jpeg;base64," + Convert.ToBase64String(await resp.Content.ReadAsByteArrayAsync());
            }
        }
        return null;
    }

    /// Endereço web de uma pasta do OneDrive (caminho relativo à raiz); null se ela ainda não existe na nuvem.
    public static async Task<string?> FolderWebUrl(string relativePath)
    {
        var path = string.Join('/', relativePath.Split('/', '\\').Select(Uri.EscapeDataString));
        try { return (await Get($"me/drive/root:/{path}?$select=webUrl")).GetProperty("webUrl").GetString(); }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public sealed record DriveItem(string DriveId, string Id, string Name, string WebUrl, bool IsFolder);

    /// Item da nuvem por caminho Graph (ex.: "me/drive/root:/Documentos/a.docx"); null se não existe.
    public static async Task<DriveItem?> Item(string graphPath)
    {
        try
        {
            var i = await Get($"{graphPath}?$select=id,name,webUrl,folder,parentReference");
            return new DriveItem(i.GetProperty("parentReference").GetProperty("driveId").GetString()!, i.GetProperty("id").GetString()!,
                i.GetProperty("name").GetString() ?? "", i.GetProperty("webUrl").GetString() ?? "", i.TryGetProperty("folder", out _));
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    /// Link de compartilhamento. type: view|edit; scope: organization|anonymous|users.
    public static async Task<string> CreateLink(DriveItem item, string type, string scope, DateTime? expires = null, string? password = null)
    {
        var body = JsonSerializer.Serialize(new
        {
            type,
            scope,
            expirationDateTime = expires?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            password = string.IsNullOrEmpty(password) ? null : password,
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        var r = await Send(HttpMethod.Post, $"drives/{item.DriveId}/items/{item.Id}/createLink", body);
        return r.GetProperty("link").GetProperty("webUrl").GetString()!;
    }

    public sealed record Permission(string Id, string Kind, string Name, string Detail, string Role, string? LinkUrl, bool Inherited);

    /// Quem tem acesso ao item: Kind = owner | person | group | link.
    public static async Task<List<Permission>> Permissions(DriveItem item)
    {
        var list = new List<Permission>();
        foreach (var p in await GetAll($"drives/{item.DriveId}/items/{item.Id}/permissions"))
        {
            var roles = p.TryGetProperty("roles", out var r) ? r.EnumerateArray().Select(x => x.GetString()).ToList() : new();
            var role = roles.Contains("owner") ? "Proprietário" : roles.Contains("write") ? "Pode editar" : "Pode exibir";
            var inherited = p.TryGetProperty("inheritedFrom", out var inh) && inh.ValueKind == JsonValueKind.Object;
            var id = p.GetProperty("id").GetString()!;
            if (p.TryGetProperty("link", out var link))
            {
                var scope = link.TryGetProperty("scope", out var sc) ? sc.GetString() : "";
                var who = scope switch { "anonymous" => "Qualquer pessoa com o link", "organization" => "Pessoas na sua organização com o link", _ => "Pessoas específicas com o link" };
                list.Add(new Permission(id, "link", who, link.TryGetProperty("webUrl", out var wu) ? wu.GetString() ?? "" : "", role, link.TryGetProperty("webUrl", out var u) ? u.GetString() : null, inherited));
                continue;
            }
            var grant = p.TryGetProperty("grantedToV2", out var g2) ? g2 : p.TryGetProperty("grantedTo", out var g1) ? g1 : default;
            if (grant.ValueKind != JsonValueKind.Object) continue;
            var isGroup = grant.TryGetProperty("group", out var grp) || grant.TryGetProperty("siteGroup", out grp);
            var who2 = isGroup ? grp : grant.TryGetProperty("user", out var usr) ? usr : grant.TryGetProperty("siteUser", out usr) ? usr : default;
            if (who2.ValueKind != JsonValueKind.Object) continue;
            var name = who2.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "";
            var email = who2.TryGetProperty("email", out var em) ? em.GetString() ?? "" : who2.TryGetProperty("loginName", out var ln) ? ln.GetString() ?? "" : "";
            list.Add(new Permission(id, roles.Contains("owner") ? "owner" : isGroup ? "group" : "person", name, email, role, null, inherited));
        }
        return list;
    }

    public static Task DeletePermission(DriveItem item, string permissionId) =>
        Send(HttpMethod.Delete, $"drives/{item.DriveId}/items/{item.Id}/permissions/{permissionId}", "");

    /// Sugestões de pessoas (precisa de People.Read; logins antigos não têm: devolve vazio).
    public static async Task<List<(string Name, string Email)>> People(string query)
    {
        try
        {
            var page = await Get($"me/people?$search=\"{Uri.EscapeDataString(query)}\"&$top=6&$select=displayName,scoredEmailAddresses");
            return page.GetProperty("value").EnumerateArray()
                .Select(p => (p.GetProperty("displayName").GetString() ?? "",
                              p.TryGetProperty("scoredEmailAddresses", out var e) && e.GetArrayLength() > 0 ? e[0].GetProperty("address").GetString() ?? "" : ""))
                .Where(p => p.Item2.Length > 0).ToList();
        }
        catch (HttpRequestException) { return new(); }
    }

    /// Convida pessoas por e-mail (a Microsoft envia o convite). role: read|write.
    public static Task Invite(DriveItem item, IEnumerable<string> emails, string role, string? message)
    {
        var body = JsonSerializer.Serialize(new
        {
            recipients = emails.Select(e => new { email = e }),
            message = string.IsNullOrWhiteSpace(message) ? null : message,
            requireSignIn = true,
            sendInvitation = true,
            roles = new[] { role },
        });
        return Send(HttpMethod.Post, $"drives/{item.DriveId}/items/{item.Id}/invite", body);
    }

    static async Task<JsonElement> Send(HttpMethod method, string url, string json)
    {
        using var req = new HttpRequestMessage(method, url) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessToken());
        using var resp = await Http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            // Mensagem do Graph é a explicação útil (ex.: links anônimos desativados pela empresa)
            var msg = body;
            try { msg = JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("message").GetString() ?? body; }
            catch (JsonException) { }
            throw new HttpRequestException(msg, null, resp.StatusCode);
        }
        return string.IsNullOrWhiteSpace(body) ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    public sealed record SearchHit(string Name, string Path, bool IsFolder, long Size, DateTimeOffset Modified);

    /// Busca no OneDrive do usuário. Path = caminho relativo à raiz do OneDrive (com '/').
    public static async Task<List<SearchHit>> Search(string query)
    {
        var q = Uri.EscapeDataString(query.Replace("'", "''"));
        var page = await Get($"me/drive/root/search(q='{q}')?$select=id,name,folder,size,lastModifiedDateTime&$top=20");
        // A busca não traz o caminho da pasta (parentReference.path vem vazio): resolve item a item, em paralelo
        var tasks = page.GetProperty("value").EnumerateArray().Select(async item =>
        {
            var name = item.GetProperty("name").GetString() ?? "";
            var rel = name;
            try
            {
                var full = await Get($"me/drive/items/{item.GetProperty("id").GetString()}?$select=parentReference");
                var parent = full.GetProperty("parentReference").TryGetProperty("path", out var pp) ? pp.GetString() ?? "" : "";
                var dir = Uri.UnescapeDataString(parent.Contains("root:") ? parent[(parent.IndexOf("root:", StringComparison.Ordinal) + 5)..] : "").Trim('/');
                if (dir.Length > 0) rel = $"{dir}/{name}";
            }
            catch (HttpRequestException) { }   // sem caminho: abre na raiz do Meu OneDrive
            return new SearchHit(name, rel, item.TryGetProperty("folder", out _),
                item.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                item.TryGetProperty("lastModifiedDateTime", out var d) ? d.GetDateTimeOffset() : default);
        });
        return (await Task.WhenAll(tasks)).ToList();
    }

    static string LibraryName(string site, string library, bool isDefault) =>
        Sanitize(isDefault || DefaultLibraryNames.Contains(library, StringComparer.OrdinalIgnoreCase) ? site : $"{site} - {library}");

    public static string Sanitize(string name) =>
        Regex.Replace(Regex.Replace(name, "[\\\\/:*?\"<>|=]", "-"), @"\s+", " ").Trim().TrimEnd('.');
}
