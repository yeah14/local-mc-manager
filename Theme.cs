namespace McPanel;

internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(22, 24, 30);
    public static readonly Color Panel = Color.FromArgb(30, 33, 41);
    public static readonly Color Card = Color.FromArgb(38, 42, 52);
    public static readonly Color Input = Color.FromArgb(18, 20, 26);
    public static readonly Color Text = Color.FromArgb(232, 234, 237);
    public static readonly Color Muted = Color.FromArgb(154, 160, 172);
    public static readonly Color Accent = Color.FromArgb(76, 148, 255);
    public static readonly Color Selected = Color.FromArgb(46, 62, 92);
    public static readonly Color Green = Color.FromArgb(72, 196, 126);
    public static readonly Color Red = Color.FromArgb(214, 84, 84);
    public static readonly Color Yellow = Color.FromArgb(232, 186, 74);
    public static readonly Color Orange = Color.FromArgb(232, 150, 64);
    public static readonly Color Border = Color.FromArgb(68, 74, 88);
    public static readonly Color Button = Color.FromArgb(48, 53, 66);

    public static readonly Font Ui = new("Microsoft YaHei UI", 9.5f);
    public static readonly Font UiBold = new("Microsoft YaHei UI", 10f, FontStyle.Bold);
    public static readonly Font Title = new("Microsoft YaHei UI", 16f, FontStyle.Bold);
    public static readonly Font Name = new("Microsoft YaHei UI", 13f, FontStyle.Bold);
    public static readonly Font Log = new("Consolas", 10f);

    public static void Style(Form form)
    {
        form.BackColor = Bg;
        form.ForeColor = Text;
        form.Font = Ui;
    }

    public static Button MakeButton(string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 6, 12, 6),
            Margin = new Padding(4, 4, 4, 4),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Accent : Button,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderColor = primary ? Accent : Border;
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        return button;
    }
}
