namespace HdtDrive;

/// Aviso do "Copiar Link": "✓ Link copiado / Qualquer pessoa com o link pode editar / ⚙ Configurações".
/// Também usado para "Excluído do OneDrive / Abrir lixeira".
sealed class LinkToast : Form
{
    static readonly Color Ink = Color.FromArgb(0x24, 0x24, 0x24);
    static readonly Color Soft = Color.FromArgb(0x61, 0x61, 0x61);
    static readonly Color Ok = Color.FromArgb(0x0E, 0x7A, 0x0D);
    static readonly Color Accent = Color.FromArgb(0x0F, 0x6C, 0xBD);

    readonly System.Windows.Forms.Timer _close = new() { Interval = 8000 };

    public LinkToast(string itemName, string detail, Action openSettings)
        : this($"Compartilhar \"{itemName}\"", "✓", "Link copiado", detail, "⚙ Configurações", openSettings, activate: true) { }

    /// activate=false: não rouba o foco (o aviso de exclusão aparece enquanto a pessoa ainda está no Explorer)
    readonly bool _activate;
    protected override bool ShowWithoutActivation => !_activate;

    /// dontShowAgain != null: mostra "Não mostrar novamente" e chama com true se a pessoa marcar
    public LinkToast(string caption, string glyph, string headline, string detail, string actionText, Action action, bool activate,
        Action<bool>? dontShowAgain = null)
    {
        _activate = activate;
        Text = caption;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.White;
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Segoe UI", 10f);
        using (var ico = ShellIntegration.OneDriveIcon(32)) if (ico != null) Icon = (Icon)ico.Clone();

        var check = new Label { Text = glyph, ForeColor = Ok, Font = new Font("Segoe UI", 13f, FontStyle.Bold), AutoSize = true };
        var title = new Label { Text = headline, ForeColor = Ink, Font = new Font("Segoe UI Semibold", 11.5f), AutoSize = true };
        var sub = new Label { Text = detail, ForeColor = Soft, AutoSize = true };
        var settings = new LinkLabel
        {
            Text = actionText, AutoSize = true, LinkColor = Accent, ActiveLinkColor = Ink,
            LinkBehavior = LinkBehavior.HoverUnderline,
        };
        settings.LinkClicked += (_, _) => { Close(); action(); };
        Controls.AddRange(new Control[] { check, title, sub, settings });
        var never = dontShowAgain == null ? null : new CheckBox { Text = "Não mostrar novamente", ForeColor = Soft, AutoSize = true };
        if (never != null)
        {
            Controls.Add(never);
            never.CheckedChanged += (_, _) => _close.Stop();   // mexeu no aviso: espera a pessoa fechar
            FormClosed += (_, _) => dontShowAgain!(never.Checked);
        }

        HandleCreated += (_, _) =>
        {
            int S(int v) => DeviceDpi * v / 96;
            ClientSize = new Size(S(560), S(never == null ? 84 : 116));
            if (never != null) never.Location = new Point(S(20), S(80));
            check.Location = new Point(S(20), S(16));
            title.Location = new Point(Math.Max(S(44), check.Right + S(6)), S(16));   // glifo largo (🗑) não cobre o título
            sub.Location = new Point(S(22), S(46));
            settings.Location = new Point(ClientSize.Width - S(150), S(46));
            // Topo e centro da tela ativa, como o aviso do OneDrive
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + S(90));
        };
        _close.Tick += (_, _) => Close();
        Shown += (_, _) => { _close.Start(); if (activate) Activate(); };
        FormClosed += (_, _) => _close.Dispose();
    }
}
