using System.Drawing.Imaging;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;

namespace HdtDrive;

/// Página que o navegador mostra ao terminar o login (template Go do rclone, via config_template_file).
/// Gerada já no idioma do Windows, com o ícone do OneDrive instalado no PC.
static class AuthPage
{
    sealed record Strings(string SuccessTitle, string SuccessBody, string FailTitle, string FailBody, string Details);

    // Chave: idioma (ou idioma-região). Texto: sucesso, instrução, falha, instrução, rótulo dos detalhes.
    static readonly Dictionary<string, Strings> Texts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pt"] = new("Você entrou na sua conta", "Pode fechar esta aba. O OneDrive 365 já está montando seus arquivos.", "Não foi possível entrar", "Feche esta aba e tente de novo pelo ícone do OneDrive 365 perto do relógio.", "Detalhes técnicos"),
        ["en"] = new("You're signed in", "You can close this tab. OneDrive 365 is setting up your files.", "Sign-in didn't complete", "Close this tab and try again from the OneDrive 365 icon next to the clock.", "Technical details"),
        ["es"] = new("Has iniciado sesión", "Puedes cerrar esta pestaña. OneDrive 365 ya está preparando tus archivos.", "No se pudo iniciar sesión", "Cierra esta pestaña y vuelve a intentarlo desde el icono de OneDrive 365 junto al reloj.", "Detalles técnicos"),
        ["fr"] = new("Vous êtes connecté", "Vous pouvez fermer cet onglet. OneDrive 365 prépare vos fichiers.", "La connexion a échoué", "Fermez cet onglet et réessayez depuis l'icône OneDrive 365 près de l'horloge.", "Détails techniques"),
        ["de"] = new("Sie sind angemeldet", "Sie können diesen Tab schließen. OneDrive 365 richtet Ihre Dateien ein.", "Anmeldung nicht abgeschlossen", "Schließen Sie diesen Tab und versuchen Sie es erneut über das OneDrive 365-Symbol neben der Uhr.", "Technische Details"),
        ["it"] = new("Accesso eseguito", "Puoi chiudere questa scheda. OneDrive 365 sta preparando i tuoi file.", "Accesso non riuscito", "Chiudi questa scheda e riprova dall'icona di OneDrive 365 accanto all'orologio.", "Dettagli tecnici"),
        ["nl"] = new("Je bent aangemeld", "Je kunt dit tabblad sluiten. OneDrive 365 zet je bestanden klaar.", "Aanmelden is niet gelukt", "Sluit dit tabblad en probeer het opnieuw via het OneDrive 365-pictogram naast de klok.", "Technische details"),
        ["pl"] = new("Zalogowano", "Możesz zamknąć tę kartę. OneDrive 365 przygotowuje Twoje pliki.", "Nie udało się zalogować", "Zamknij tę kartę i spróbuj ponownie z ikony OneDrive 365 obok zegara.", "Szczegóły techniczne"),
        ["cs"] = new("Jste přihlášeni", "Tuto kartu můžete zavřít. OneDrive 365 připravuje vaše soubory.", "Přihlášení se nezdařilo", "Zavřete tuto kartu a zkuste to znovu přes ikonu OneDrive 365 vedle hodin.", "Technické podrobnosti"),
        ["ro"] = new("V-ați conectat", "Puteți închide această filă. OneDrive 365 vă pregătește fișierele.", "Conectarea nu a reușit", "Închideți această filă și încercați din nou din pictograma OneDrive 365 de lângă ceas.", "Detalii tehnice"),
        ["hu"] = new("Bejelentkezett", "Bezárhatja ezt a lapot. A OneDrive 365 előkészíti a fájljait.", "A bejelentkezés nem sikerült", "Zárja be ezt a lapot, és próbálja újra az óra melletti OneDrive 365 ikonnal.", "Technikai részletek"),
        ["sv"] = new("Du är inloggad", "Du kan stänga den här fliken. OneDrive 365 förbereder dina filer.", "Inloggningen slutfördes inte", "Stäng fliken och försök igen via OneDrive 365-ikonen bredvid klockan.", "Tekniska detaljer"),
        ["da"] = new("Du er logget ind", "Du kan lukke denne fane. OneDrive 365 gør dine filer klar.", "Login blev ikke gennemført", "Luk fanen, og prøv igen fra OneDrive 365-ikonet ved siden af uret.", "Tekniske detaljer"),
        ["nb"] = new("Du er logget på", "Du kan lukke denne fanen. OneDrive 365 gjør klar filene dine.", "Påloggingen ble ikke fullført", "Lukk fanen og prøv igjen fra OneDrive 365-ikonet ved siden av klokken.", "Tekniske detaljer"),
        ["fi"] = new("Olet kirjautunut", "Voit sulkea tämän välilehden. OneDrive 365 valmistelee tiedostojasi.", "Kirjautuminen ei onnistunut", "Sulje välilehti ja yritä uudelleen kellon vieressä olevasta OneDrive 365 -kuvakkeesta.", "Tekniset tiedot"),
        ["el"] = new("Συνδεθήκατε", "Μπορείτε να κλείσετε αυτή την καρτέλα. Το OneDrive 365 ετοιμάζει τα αρχεία σας.", "Η σύνδεση δεν ολοκληρώθηκε", "Κλείστε την καρτέλα και δοκιμάστε ξανά από το εικονίδιο του OneDrive 365 δίπλα στο ρολόι.", "Τεχνικές λεπτομέρειες"),
        ["tr"] = new("Oturum açtınız", "Bu sekmeyi kapatabilirsiniz. OneDrive 365 dosyalarınızı hazırlıyor.", "Oturum açılamadı", "Bu sekmeyi kapatın ve saatin yanındaki OneDrive 365 simgesinden yeniden deneyin.", "Teknik ayrıntılar"),
        ["ru"] = new("Вы вошли в аккаунт", "Эту вкладку можно закрыть. OneDrive 365 подготавливает ваши файлы.", "Не удалось войти", "Закройте вкладку и попробуйте снова через значок OneDrive 365 рядом с часами.", "Технические подробности"),
        ["uk"] = new("Ви увійшли в обліковий запис", "Цю вкладку можна закрити. OneDrive 365 готує ваші файли.", "Не вдалося увійти", "Закрийте вкладку й спробуйте ще раз через значок OneDrive 365 біля годинника.", "Технічні подробиці"),
        ["ar"] = new("تم تسجيل الدخول", "يمكنك إغلاق علامة التبويب هذه. يقوم OneDrive 365 بتجهيز ملفاتك.", "تعذّر تسجيل الدخول", "أغلق علامة التبويب وحاول مرة أخرى من أيقونة OneDrive 365 بجوار الساعة.", "تفاصيل تقنية"),
        ["he"] = new("נכנסת לחשבון", "אפשר לסגור את הכרטיסייה. OneDrive 365 מכין את הקבצים שלך.", "הכניסה לא הושלמה", "סגור את הכרטיסייה ונסה שוב דרך הסמל של OneDrive 365 ליד השעון.", "פרטים טכניים"),
        ["hi"] = new("आपने साइन इन कर लिया है", "आप यह टैब बंद कर सकते हैं। OneDrive 365 आपकी फ़ाइलें तैयार कर रहा है।", "साइन इन पूरा नहीं हुआ", "यह टैब बंद करें और घड़ी के पास OneDrive 365 आइकन से फिर से कोशिश करें।", "तकनीकी विवरण"),
        ["id"] = new("Anda sudah masuk", "Anda dapat menutup tab ini. OneDrive 365 sedang menyiapkan file Anda.", "Gagal masuk", "Tutup tab ini dan coba lagi dari ikon OneDrive 365 di dekat jam.", "Detail teknis"),
        ["vi"] = new("Bạn đã đăng nhập", "Bạn có thể đóng tab này. OneDrive 365 đang chuẩn bị tệp của bạn.", "Không đăng nhập được", "Đóng tab này và thử lại từ biểu tượng OneDrive 365 cạnh đồng hồ.", "Chi tiết kỹ thuật"),
        ["th"] = new("คุณลงชื่อเข้าใช้แล้ว", "คุณปิดแท็บนี้ได้เลย OneDrive 365 กำลังเตรียมไฟล์ของคุณ", "ลงชื่อเข้าใช้ไม่สำเร็จ", "ปิดแท็บนี้แล้วลองอีกครั้งจากไอคอน OneDrive 365 ข้างนาฬิกา", "รายละเอียดทางเทคนิค"),
        ["ja"] = new("サインインしました", "このタブは閉じてかまいません。OneDrive 365 がファイルを準備しています。", "サインインできませんでした", "このタブを閉じ、時計の横にある OneDrive 365 のアイコンからもう一度お試しください。", "技術的な詳細"),
        ["ko"] = new("로그인되었습니다", "이 탭을 닫아도 됩니다. OneDrive 365가 파일을 준비하고 있습니다.", "로그인하지 못했습니다", "이 탭을 닫고 시계 옆의 OneDrive 365 아이콘에서 다시 시도하세요.", "기술 세부 정보"),
        ["zh-Hans"] = new("你已登录", "可以关闭此标签页。OneDrive 365 正在准备你的文件。", "未能登录", "请关闭此标签页，然后通过时钟旁的 OneDrive 365 图标重试。", "技术详细信息"),
        ["zh-Hant"] = new("你已登入", "可以關閉此索引標籤。OneDrive 365 正在準備你的檔案。", "無法登入", "請關閉此索引標籤，然後從時鐘旁的 OneDrive 365 圖示再試一次。", "技術詳細資料"),
    };

    static readonly HashSet<string> RightToLeft = new(StringComparer.OrdinalIgnoreCase) { "ar", "he" };

    /// Grava o template e devolve o caminho. Idioma: o da interface do Windows.
    public static string Write() => Write(CultureInfo.CurrentUICulture);

    public static string Write(CultureInfo culture)
    {
        var (lang, t) = Pick(culture);
        var dir = RightToLeft.Contains(lang) ? "rtl" : "ltr";
        var icon = OneDriveIconDataUri();
        var iconHtml = icon != null
            ? $"<img class=\"cloud\" src=\"{icon}\" alt=\"\" width=\"96\" height=\"96\">"
            : "<svg class=\"cloud\" viewBox=\"0 0 96 96\" aria-hidden=\"true\"><path fill=\"#0F6CBD\" d=\"M28 74a18 18 0 0 1-2-36 24 24 0 0 1 46-4 20 20 0 0 1-2 40z\"/></svg>";

        string H(string s) => WebUtility.HtmlEncode(s);

        // .Code (código OAuth) nunca é exibido: só Name/Description na falha
        var html = $$"""
<!DOCTYPE html>
<html lang="{{lang}}" dir="{{dir}}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{{"{{"}} if .OK {{"}}"}}{{H(t.SuccessTitle)}}{{"{{"}} else {{"}}"}}{{H(t.FailTitle)}}{{"{{"}} end {{"}}"}} – {{Brand.Name}}</title>
<style>
  :root {
    --sky: #EEF5FC;
    --ink: #0E2A47;
    --ink-soft: #3D5673;
    --brand: #0F6CBD;
    --ok: #0E7A0D;
    --fail: #B3261E;
  }
  @media (prefers-color-scheme: dark) {
    :root { --sky: #0D1B2A; --ink: #E6EEF7; --ink-soft: #A9BCD0; --brand: #4CA3F0; --ok: #54B054; --fail: #F1707B; }
  }
  * { box-sizing: border-box; }
  html, body { height: 100%; margin: 0; }
  body {
    display: grid;
    place-items: center;
    padding: 24px 16px;
    background: var(--sky);
    color: var(--ink);
    font-family: "Segoe UI Variable Display", "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif;
  }
  main { width: min(30rem, 100%); }
  .mark { position: relative; width: 96px; height: 96px; margin-bottom: 28px; }
  .cloud { width: 96px; height: 96px; display: block; image-rendering: auto; }
  .seal {
    position: absolute;
    inset-inline-end: -10px;
    bottom: -6px;
    width: 40px;
    height: 40px;
    border-radius: 50%;
    border: 4px solid var(--sky);
    display: grid;
    place-items: center;
    color: #fff;
    animation: land 520ms cubic-bezier(.2, 1.4, .4, 1) 180ms both;
  }
  .seal svg { width: 18px; height: 18px; }
  .seal.ok { background: var(--ok); }
  .seal.fail { background: var(--fail); }
  @keyframes land { from { transform: translateY(-14px) scale(.4); opacity: 0; } to { transform: none; opacity: 1; } }
  h1 { font-size: 2rem; line-height: 1.15; font-weight: 600; letter-spacing: -0.01em; margin: 0 0 12px; }
  p { font-size: 1.0625rem; line-height: 1.55; color: var(--ink-soft); margin: 0; }
  details { margin-top: 28px; color: var(--ink-soft); font-size: .9rem; }
  summary { cursor: pointer; color: var(--brand); }
  summary:focus-visible { outline: 2px solid var(--brand); outline-offset: 3px; border-radius: 2px; }
  pre { white-space: pre-wrap; word-break: break-word; font: inherit; margin: 8px 0 0; }
  footer { margin-top: 48px; font-size: .8125rem; color: var(--ink-soft); opacity: .8; }
  @media (prefers-reduced-motion: reduce) { .seal { animation: none; } }
</style>
</head>
<body>
<main>
  <div class="mark">
    {{iconHtml}}
    {{"{{"}} if .OK {{"}}"}}
    <span class="seal ok"><svg viewBox="0 0 18 18" aria-hidden="true"><path d="M3.5 9.5l3.5 3.5 7.5-8" fill="none" stroke="currentColor" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round"/></svg></span>
    {{"{{"}} else {{"}}"}}
    <span class="seal fail"><svg viewBox="0 0 18 18" aria-hidden="true"><path d="M5 5l8 8M13 5l-8 8" fill="none" stroke="currentColor" stroke-width="2.6" stroke-linecap="round"/></svg></span>
    {{"{{"}} end {{"}}"}}
  </div>
  {{"{{"}} if .OK {{"}}"}}
  <h1>{{H(t.SuccessTitle)}}</h1>
  <p>{{H(t.SuccessBody)}}</p>
  {{"{{"}} else {{"}}"}}
  <h1>{{H(t.FailTitle)}}</h1>
  <p>{{H(t.FailBody)}}</p>
  <details><summary>{{H(t.Details)}}</summary><pre>{{"{{"}} .Name {{"}}"}}{{"{{"}} if .Description {{"}}"}}
{{"{{"}} .Description {{"}}"}}{{"{{"}} end {{"}}"}}</pre></details>
  {{"{{"}} end {{"}}"}}
  <footer>{{Brand.Name}}</footer>
</main>
</body>
</html>
""";
        var path = Path.Combine(Paths.AppDir, "auth.html");
        File.WriteAllText(path, html);
        return path;
    }

    static (string lang, Strings t) Pick(CultureInfo culture)
    {
        for (var c = culture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
        {
            if (Texts.TryGetValue(c.Name, out var t)) return (c.Name, t);
            if (c.Name is "zh-CN" or "zh-SG") return (c.Name, Texts["zh-Hans"]);
            if (c.Name is "zh-TW" or "zh-HK" or "zh-MO") return (c.Name, Texts["zh-Hant"]);
            if (c.Name is "no" or "nn") return (c.Name, Texts["nb"]);
        }
        return ("en", Texts["en"]);
    }

    /// Ícone da nuvem do OneDrive.exe instalado, em PNG base64. Null se o OneDrive não estiver instalado.
    static string? OneDriveIconDataUri()
    {
        using var icon = ShellIntegration.OneDriveIcon(256);
        if (icon == null) return null;
        using var bmp = icon.ToBitmap();
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
    }
}
