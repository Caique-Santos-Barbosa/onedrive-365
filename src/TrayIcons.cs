using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HdtDrive;

/// Ícones da bandeja no estilo do OneDrive: nuvem + selo de estado; setas girando enquanto sincroniza.
static class TrayIcons
{
    public enum Kind { Ok, Sync, Paused, Error, Offline }

    public const int SyncFrames = 12;
    static readonly Dictionary<(Kind, int, int), Icon> Cache = new();

    /// Tamanho do ícone da bandeja no DPI atual (16 px a 100%, 20 a 125%...).
    public static int TraySize => GetSystemMetricsForDpi(49 /* SM_CXSMICON */, GetDpiForSystem());

    public static Icon Get(Kind kind, int frame = 0)
    {
        var size = TraySize;
        if (kind != Kind.Sync) frame = 0;
        if (Cache.TryGetValue((kind, frame, size), out var icon)) return icon;

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var cloud = ShellIntegration.OneDriveIcon(size);
            if (cloud != null)
            {
                if (kind == Kind.Offline)
                {
                    // Nuvem acinzentada, como o OneDrive desconectado
                    using var gray = new Bitmap(cloud.ToBitmap());
                    var cm = new System.Drawing.Imaging.ColorMatrix(new[]
                    {
                        new[] { .3f, .3f, .3f, 0f, 0f }, new[] { .59f, .59f, .59f, 0f, 0f }, new[] { .11f, .11f, .11f, 0f, 0f },
                        new[] { 0f, 0f, 0f, .7f, 0f }, new[] { 0f, 0f, 0f, 0f, 1f },
                    });
                    using var ia = new System.Drawing.Imaging.ImageAttributes();
                    ia.SetColorMatrix(cm);
                    g.DrawImage(gray, new Rectangle(0, 0, size, size), 0, 0, size, size, GraphicsUnit.Pixel, ia);
                }
                else g.DrawIcon(cloud, new Rectangle(0, 0, size, size));
            }
            DrawBadge(g, kind, frame, size);
        }
        var h = bmp.GetHicon();
        icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        Cache[(kind, frame, size)] = icon;
        return icon;
    }

    static void DrawBadge(Graphics g, Kind kind, int frame, int size)
    {
        if (kind is Kind.Ok or Kind.Offline) return;
        var d = size * 0.62f;
        var r = new RectangleF(size - d, size - d, d - 0.5f, d - 0.5f);
        var color = kind switch
        {
            Kind.Sync => Color.FromArgb(0x0F, 0x6C, 0xBD),
            Kind.Paused => Color.FromArgb(0x61, 0x61, 0x61),
            _ => Color.FromArgb(0xC4, 0x2B, 0x1C),
        };
        using (var white = new SolidBrush(Color.White)) g.FillEllipse(white, RectangleF.Inflate(r, size * 0.06f, size * 0.06f));
        using (var fill = new SolidBrush(color)) g.FillEllipse(fill, r);
        using var pen = new Pen(Color.White, Math.Max(1.2f, size / 12f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var c = new PointF(r.X + r.Width / 2, r.Y + r.Height / 2);
        var k = r.Width / 10f;
        switch (kind)
        {
            case Kind.Sync:
                // Duas setas em arco, giradas conforme o quadro
                var state = g.Save();
                g.TranslateTransform(c.X, c.Y);
                g.RotateTransform(frame * 360f / SyncFrames);
                var arc = new RectangleF(-3.2f * k, -3.2f * k, 6.4f * k, 6.4f * k);
                g.DrawArc(pen, arc, 200, 120);
                g.DrawArc(pen, arc, 20, 120);
                g.DrawLines(pen, new[] { new PointF(2.2f * k, -3.6f * k), new PointF(3.3f * k, -1.6f * k), new PointF(1.2f * k, -1.4f * k) });
                g.DrawLines(pen, new[] { new PointF(-2.2f * k, 3.6f * k), new PointF(-3.3f * k, 1.6f * k), new PointF(-1.2f * k, 1.4f * k) });
                g.Restore(state);
                break;
            case Kind.Paused:
                g.DrawLine(pen, c.X - 1.4f * k, c.Y - 2.4f * k, c.X - 1.4f * k, c.Y + 2.4f * k);
                g.DrawLine(pen, c.X + 1.4f * k, c.Y - 2.4f * k, c.X + 1.4f * k, c.Y + 2.4f * k);
                break;
            case Kind.Error:
                g.DrawLine(pen, c.X - 2.2f * k, c.Y - 2.2f * k, c.X + 2.2f * k, c.Y + 2.2f * k);
                g.DrawLine(pen, c.X + 2.2f * k, c.Y - 2.2f * k, c.X - 2.2f * k, c.Y + 2.2f * k);
                break;
        }
    }

    /// Mostra o ícone direto na barra (fora da setinha ^), como o OneDrive na primeira execução.
    /// O Windows 11 guarda isso em NotifyIconSettings; só promovemos uma vez para respeitar se o usuário esconder depois.
    public static bool TryPromote(string exePath)
    {
        using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
        if (root == null) return false;
        foreach (var name in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(name, writable: true);
            if (k?.GetValue("ExecutablePath") is string path && string.Equals(path, exePath, StringComparison.OrdinalIgnoreCase))
            {
                k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                return true;
            }
        }
        return false;
    }

    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
}
