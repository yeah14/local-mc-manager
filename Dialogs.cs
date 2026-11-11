namespace McPanel;

internal sealed class RestoreForm : Form
{
    readonly ListBox _list = new()
    {
        Dock = DockStyle.Fill,
        BackColor = Theme.Input,
        ForeColor = Theme.Text,
        BorderStyle = BorderStyle.None,
        IntegralHeight = false,
        Font = Theme.Ui,
    };
    readonly List<FileInfo> _files;
    public string? SelectedZip { get; private set; }

    public RestoreForm(IReadOnlyList<FileInfo> files)
    {
        _files = files.ToList();
        Theme.Style(this);
        Text = "从备份恢复世界";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(640, 420);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        var hint = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            Text = "恢复前会再备份一次当前世界。服务器必须处于停止状态。",
        };
        foreach (var file in _files)
            _list.Items.Add($"{file.LastWriteTime:yyyy-MM-dd HH:mm:ss}    {FormatSize(file.Length)}    {file.Name}");
        if (_files.Count > 0)
            _list.SelectedIndex = 0;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var ok = Theme.MakeButton("恢复这份备份", true);
        var cancel = Theme.MakeButton("取消");
        var open = Theme.MakeButton("打开备份目录");
        ok.Click += (_, _) => Confirm();
        cancel.Click += (_, _) => Close();
        open.Click += (_, _) =>
        {
            if (_files.Count > 0)
                ShellOpen.Folder(_files[0].DirectoryName!);
        };
        _list.DoubleClick += (_, _) => Confirm();
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(open);
        layout.Controls.Add(hint, 0, 0);
        layout.Controls.Add(_list, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
    }

    void Confirm()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _files.Count)
            return;
        SelectedZip = _files[_list.SelectedIndex].FullName;
        DialogResult = DialogResult.OK;
        Close();
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return bytes + " B";
        if (bytes < 1024 * 1024)
            return (bytes / 1024d).ToString("0.0") + " KB";
        if (bytes < 1024L * 1024 * 1024)
            return (bytes / 1024d / 1024d).ToString("0.0") + " MB";
        return (bytes / 1024d / 1024d / 1024d).ToString("0.00") + " GB";
    }
}

internal sealed class CrashForm : Form
{
    readonly ListBox _list = new()
    {
        Dock = DockStyle.Fill,
        BackColor = Theme.Input,
        ForeColor = Theme.Text,
        BorderStyle = BorderStyle.None,
        IntegralHeight = false,
    };
    readonly RichTextBox _preview = new()
    {
        Dock = DockStyle.Fill,
        BackColor = Theme.Input,
        ForeColor = Theme.Text,
        BorderStyle = BorderStyle.None,
        ReadOnly = true,
        Font = Theme.Log,
        WordWrap = false,
        DetectUrls = false,
    };
    readonly List<string> _folders = new();

    public CrashForm(string directory)
    {
        Theme.Style(this);
        Text = "崩溃记录";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ClientSize = new Size(860, 520);
        if (Directory.Exists(directory))
        {
            _folders.AddRange(Directory.GetDirectories(directory)
                .OrderByDescending(Directory.GetLastWriteTime));
        }
        foreach (var folder in _folders)
            _list.Items.Add(Path.GetFileName(folder));
        _list.SelectedIndexChanged += (_, _) => ShowSelected();

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
        split.Panel1.Controls.Add(_list);
        split.Panel2.Controls.Add(_preview);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8) };
        var open = Theme.MakeButton("打开所在文件夹");
        var close = Theme.MakeButton("关闭");
        open.Click += (_, _) => ShellOpen.Folder(directory);
        close.Click += (_, _) => Close();
        buttons.Controls.Add(open);
        buttons.Controls.Add(close);
        var hint = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = _folders.Count == 0 ? "还没有自动保存的崩溃记录。" : "选择一条记录查看 summary.log。",
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.Controls.Add(hint, 0, 0);
        layout.Controls.Add(split, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
        Shown += (_, _) =>
        {
            try { split.SplitterDistance = 220; } catch { }
            if (_list.Items.Count > 0)
                _list.SelectedIndex = 0;
        };
    }

    void ShowSelected()
    {
        _preview.Clear();
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _folders.Count)
            return;
        var summary = Path.Combine(_folders[_list.SelectedIndex], "summary.log");
        if (!File.Exists(summary))
        {
            _preview.Text = "没有 summary.log。";
            return;
        }
        var text = File.ReadAllText(summary);
        if (text.Length > 200_000)
            text = text[..200_000] + "\n……已截断……";
        _preview.Text = text;
    }
}

internal sealed class NewWorldForm : Form
{
    readonly TextBox _name = new();
    readonly TextBox _seed = new();
    public string WorldName { get; private set; } = "";
    public string Seed { get; private set; } = "";

    public NewWorldForm(string currentName, string suggested)
    {
        Theme.Style(this);
        Text = "创建新世界";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(520, 280);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(16) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var hint = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            Text = "会新建一个空文件夹，并把配置里的「世界文件夹」改成它。\n旧世界「" + currentName + "」留在原地。地形在下次启动时生成。",
        };
        var nameLabel = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Text, Text = "新世界文件夹", TextAlign = ContentAlignment.MiddleLeft };
        _name.Dock = DockStyle.Fill;
        _name.BorderStyle = BorderStyle.FixedSingle;
        _name.BackColor = Theme.Input;
        _name.ForeColor = Theme.Text;
        _name.Text = suggested;
        var seedLabel = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Text, Text = "种子（可留空，留空则沿用当前种子）", TextAlign = ContentAlignment.MiddleLeft };
        _seed.Dock = DockStyle.Fill;
        _seed.BorderStyle = BorderStyle.FixedSingle;
        _seed.BackColor = Theme.Input;
        _seed.ForeColor = Theme.Text;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var ok = Theme.MakeButton("创建", true);
        var cancel = Theme.MakeButton("取消");
        ok.Click += (_, _) => Confirm();
        cancel.Click += (_, _) => Close();
        AcceptButton = ok;
        CancelButton = cancel;
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        layout.Controls.Add(hint, 0, 0);
        layout.Controls.Add(nameLabel, 0, 1);
        layout.Controls.Add(_name, 0, 2);
        layout.Controls.Add(seedLabel, 0, 3);
        layout.Controls.Add(_seed, 0, 4);
        layout.Controls.Add(buttons, 0, 5);
        Controls.Add(layout);
        Shown += (_, _) => _name.SelectAll();
    }

    void Confirm()
    {
        var name = _name.Text.Trim();
        if (!WorldService.IsValidLevelName(name))
        {
            MessageBox.Show(this, "文件夹名无效。不要使用 \\ / : * ? \" < > |，也不能是 . 或 ..。", "创建新世界");
            return;
        }
        WorldName = name;
        Seed = _seed.Text.Trim();
        DialogResult = DialogResult.OK;
        Close();
    }
}

internal static class ShellOpen
{
    public static void Folder(string path)
    {
        Directory.CreateDirectory(path);
        var info = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = false,
        };
        info.ArgumentList.Add(path);
        System.Diagnostics.Process.Start(info);
    }
}
