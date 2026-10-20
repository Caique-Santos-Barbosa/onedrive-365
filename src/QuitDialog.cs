namespace HdtDrive;

/// "Encerrar o OneDrive 365?" — mesma pergunta do OneDrive antes de fechar.
sealed class QuitDialog : Form
{
    public QuitDialog(int uploading)
    {
        Text = $"Encerrar o {Brand.Name}";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.White;
        Font = new Font("Segoe UI", 11f);
        using (var ico = ShellIntegration.OneDriveIcon(32)) if (ico != null) Icon = (Icon)ico.Clone();

        var title = new Label { Text = $"Encerrar o {Brand.Name}?", Font = new Font("Segoe UI Semibold", 17f), AutoSize = true, ForeColor = Color.FromArgb(0x24, 0x24, 0x24) };
        var body = new Label
        {
            Text = $"Se você fechar o {Brand.Name}, os arquivos da unidade e das pastas com backup não serão sincronizados nem armazenados em backup na nuvem."
                   + (uploading > 0 ? $" Ainda há {uploading} arquivo(s) para enviar: eles sobem quando o {Brand.Name} abrir de novo." : ""),
            ForeColor = Color.FromArgb(0x42, 0x42, 0x42),
        };
        var quit = new Button { Text = $"Encerrar o {Brand.Name}", FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0xFB, 0xFB, 0xFB), DialogResult = DialogResult.OK };
        quit.FlatAppearance.BorderColor = Color.FromArgb(0xD1, 0xD1, 0xD1);
        var cancel = new Button { Text = "Cancelar", FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0x0F, 0x6C, 0xBD), ForeColor = Color.White, DialogResult = DialogResult.Cancel };
        cancel.FlatAppearance.BorderSize = 0;
        Controls.AddRange(new Control[] { title, body, quit, cancel });
        AcceptButton = cancel;   // Enter = Cancelar, como no OneDrive (fechar é a ação perigosa)
        CancelButton = cancel;

        HandleCreated += (_, _) =>
        {
            int S(int v) => DeviceDpi * v / 96;
            ClientSize = new Size(S(560), S(270));
            title.Location = new Point(S(28), S(24));
            body.Location = new Point(S(30), S(74));
            body.Size = new Size(S(500), S(100));
            quit.Size = cancel.Size = new Size(S(244), S(38));
            quit.Location = new Point(S(30), S(206));
            cancel.Location = new Point(S(286), S(206));
            ShellIntegration.CenterOnActiveScreen(this);
        };
        Shown += (_, _) => { cancel.Focus(); Activate(); };
    }
}
