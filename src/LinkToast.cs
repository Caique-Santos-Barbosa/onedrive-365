namespace HdtDrive;

/// Aviso do "Copiar Link": "✓ Link copiado / Qualquer pessoa com o link pode editar / ⚙ Configurações".
sealed class LinkToast : Form
{
    static readonly Color Ink = Color.FromArgb(0x24, 0x24, 0x24);
    static readonly Color Soft = Color.FromArgb(0x61, 0x61, 0x61);
    static readonly Color Ok = Color.FromArgb(0x0E, 0x7A, 0x0D);
    static readonly Color Accent = Color.FromArgb(0x0F, 0x6C, 0xBD);

    readonly System.Windows.Forms.Timer _close = new() { Interval = 8000 };

    public LinkToast(string itemName, string detail, Action openSettings)
    {
        Text = $"Compartilhar \"{itemName}\"";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.White;
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Segoe UI", 10f);
        using (var ico = ShellIntegration.OneDriveIcon(32)) if (ico != null) Icon = (Icon)ico.Clone();

        var check = new Label { Text = "✓", ForeColor = Ok, Font = new Font("Segoe UI", 13f, FontStyle.Bold), AutoSize = true };
        var title = new Label { Text = "Link copiado", ForeColor = Ink, Font = new Font("Segoe UI Semibold", 11.5f), AutoSize = true };
        var sub = new Label { Text = detail, ForeColor = Soft, AutoSize = true };
        var settings = new LinkLabel
        {
            Text = "⚙ Configurações", AutoSize = true, LinkColor = Accent, ActiveLinkColor = Ink,
            LinkBehavior = LinkBehavior.HoverUnderline,
        };
        settings.LinkClicked += (_, _) => { Close(); openSettings(); };
        Controls.AddRange(new Control[] { check, title, sub, settings });

        HandleCreated += (_, _) =>
        {
            int S(int v) => DeviceDpi * v / 96;
            ClientSize = new Size(S(560), S(84));
            check.Location = new Point(S(20), S(16));
            title.Location = new Point(S(44), S(16));
            sub.Location = new Point(S(22), S(46));
            settings.Location = new Point(ClientSize.Width - S(150), S(46));
            // Topo e centro da tela ativa, como o aviso do OneDrive
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + S(90));
        };
        _close.Tick += (_, _) => Close();
        Shown += (_, _) => { _close.Start(); Activate(); };
        FormClosed += (_, _) => _close.Dispose();
    }
}
