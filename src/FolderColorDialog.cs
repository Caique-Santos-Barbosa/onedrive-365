namespace HdtDrive;

/// "Cor da pasta": 16 cores (mesma paleta do OneDrive), Aplicar / Cancelar.
sealed class FolderColorDialog : Form
{
    public static readonly (string Id, string Name, Color Color)[] Palette =
    {
        ("yellow", "amarelo", C("#FFC83D")), ("red", "vermelho", C("#E5432F")), ("orange", "laranja", C("#F07A16")), ("green", "verde", C("#3C9A44")),
        ("teal", "verde-azulado", C("#25958F")), ("blue", "azul", C("#1C83D4")), ("purple", "roxo", C("#8B52C8")), ("pink", "rosa", C("#C9479F")),
        ("gray", "cinza", C("#9FA5AB")), ("lightred", "vermelho-claro", C("#F7A99E")), ("lightorange", "laranja-claro", C("#FBBE85")), ("lightgreen", "verde-claro", C("#8FD192")),
        ("lightteal", "verde-azulado-claro", C("#7CD1CB")), ("lightblue", "azul-claro", C("#83C3F5")), ("lightpurple", "roxo-claro", C("#C7A6EE")), ("lightpink", "rosa-claro", C("#F2A6D8")),
    };

    static Color C(string hex) => ColorTranslator.FromHtml(hex);

    public string Selected { get; private set; }
    readonly string _initial;
    readonly Label _info;
    readonly Button _apply;
    readonly List<Button> _swatches = new();

    public FolderColorDialog(string folderName, string current)
    {
        Selected = _initial = current;
        Text = folderName;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.White;
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Segoe UI", 10f);
        using (var ico = ShellIntegration.OneDriveIcon(32)) if (ico != null) Icon = (Icon)ico.Clone();

        var title = new Label { Text = "Cor da pasta", Font = new Font("Segoe UI Semibold", 15f), AutoSize = true, ForeColor = Color.FromArgb(0x24, 0x24, 0x24) };
        _info = new Label { AutoSize = true, ForeColor = Color.FromArgb(0x61, 0x61, 0x61) };
        _apply = new Button { Text = "Aplicar", FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0x0F, 0x6C, 0xBD), ForeColor = Color.White, DialogResult = DialogResult.OK };
        _apply.FlatAppearance.BorderSize = 0;
        var cancel = new Button { Text = "Cancelar", FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.Cancel };
        cancel.FlatAppearance.BorderColor = Color.FromArgb(0xD1, 0xD1, 0xD1);
        Controls.AddRange(new Control[] { title, _info, _apply, cancel });
        AcceptButton = _apply;
        CancelButton = cancel;

        foreach (var (id, name, color) in Palette)
        {
            var b = new Button { FlatStyle = FlatStyle.Flat, Tag = id, BackColor = Color.White, TabStop = true, AccessibleName = name };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.White;
            b.FlatAppearance.MouseDownBackColor = Color.White;
            b.Paint += (_, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var r = b.ClientRectangle;
                var d = (int)(r.Width * 0.62);
                var dot = new Rectangle((r.Width - d) / 2, (r.Height - d) / 2, d, d);
                using var brush = new SolidBrush(color);
                e.Graphics.FillEllipse(brush, dot);
                if ((string)b.Tag == Selected)
                {
                    using var pen = new Pen(Color.FromArgb(0x0F, 0x6C, 0xBD), Math.Max(2, r.Width / 16f));
                    e.Graphics.DrawEllipse(pen, Rectangle.Inflate(dot, r.Width / 9, r.Width / 9));
                }
            };
            b.Click += (_, _) => { Selected = id; Refresh(); UpdateState(); };
            _swatches.Add(b);
            Controls.Add(b);
        }

        HandleCreated += (_, _) =>
        {
            int S(int v) => DeviceDpi * v / 96;
            ClientSize = new Size(S(540), S(330));
            title.Location = new Point(S(28), S(20));
            for (var i = 0; i < _swatches.Count; i++)
            {
                _swatches[i].Size = new Size(S(56), S(56));
                _swatches[i].Location = new Point(S(22 + (i % 8) * 62), S(86 + (i / 8) * 64));
            }
            _info.Location = new Point(S(28), S(224));
            _apply.Size = cancel.Size = new Size(S(232), S(36));
            _apply.Location = new Point(S(28), S(272));
            cancel.Location = new Point(S(280), S(272));
            ShellIntegration.CenterOnActiveScreen(this);
        };
        UpdateState();
    }

    void UpdateState()
    {
        var name = Palette.First(p => p.Id == Selected).Name;
        _info.Text = $"ⓘ  Você selecionou a cor—{name}.";
        _apply.Enabled = Selected != _initial;
        _apply.BackColor = _apply.Enabled ? Color.FromArgb(0x0F, 0x6C, 0xBD) : Color.FromArgb(0xC8, 0xC8, 0xC8);
    }
}
