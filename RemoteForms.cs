namespace McPanel;

internal sealed class RemoteSettingsForm : Form
{
    readonly RemoteSettings _original;
    readonly CheckBox _enabled = new() { Text = "允许其他电脑请求启动", AutoSize = true, ForeColor = Theme.Text };
    readonly NumericUpDown _port = Number(1024, 65535);
    readonly NumericUpDown _cpu = Number(1, 100);
    readonly NumericUpDown _memory = Number(1, 100);
    readonly CheckBox _usePassword = new() { Text = "使用口令", AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(0, 6, 8, 0) };
    readonly TextBox _password = Input();
    readonly Label _load = new() { AutoSize = true, ForeColor = Theme.Muted, Text = "正在读取当前占用…" };
    readonly Label _listen = new() { AutoSize = true, MaximumSize = new Size(500, 0), ForeColor = Theme.Muted };
    int _loadToken = 1;

    public RemoteSettings Result { get; private set; } = new();

    public RemoteSettingsForm(RemoteSettings settings)
    {
        _original = settings;
        Result = settings;
        Theme.Style(this);
        Text = "远程启动";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(560, 580);
        _enabled.Checked = settings.Enabled;
        _port.Value = settings.Port;
        _cpu.Value = settings.CpuThreshold;
        _memory.Value = settings.MemoryThreshold;
        _password.UseSystemPasswordChar = true;
        _password.Width = 180;
        _password.Enabled = settings.HasPassword;
        _password.PlaceholderText = settings.HasPassword ? "已设置，留空则不修改" : "至少 4 位";
        _usePassword.Checked = settings.HasPassword;
        _usePassword.CheckedChanged += (_, _) => _password.Enabled = _usePassword.Checked;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 8, Padding = new Padding(16) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 8; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, i switch { 6 => 96, 7 => 76, _ => 40 }));

        Add(layout, 0, "开关", _enabled);
        Add(layout, 1, "监听端口", _port);
        var passwordRow = new FlowLayoutPanel { WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Width = 360, Height = 32 };
        passwordRow.Controls.Add(_usePassword);
        passwordRow.Controls.Add(_password);
        Add(layout, 2, "口令", passwordRow);
        passwordRow.Width = 360;
        Add(layout, 3, "CPU 阈值 %", _cpu);
        Add(layout, 4, "内存阈值 %", _memory);
        layout.Controls.Add(_load, 1, 5);
        var hint = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            Text = "CPU 和内存都低于阈值时，网页上可以直接启动。超过阈值会提示稍后再试。网页不填口令只能启动；设置口令后，输入口令才能停止、看日志、改配置和备份。口令留空则不接受这些管理请求。",
        };
        layout.SetColumnSpan(hint, 2);
        layout.Controls.Add(hint, 0, 6);
        layout.SetColumnSpan(_listen, 2);
        layout.Controls.Add(_listen, 0, 7);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 52,
            Padding = new Padding(12, 6, 12, 6),
        };
        var save = Theme.MakeButton("保存", true);
        var cancel = Theme.MakeButton("取消");
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => Close();
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        Controls.Add(layout);
        Controls.Add(buttons);
        RefreshListen();
        _port.ValueChanged += (_, _) => RefreshListen();
        Shown += (_, _) => ReadLoad();
        FormClosed += (_, _) => _loadToken++;
    }

    void ReadLoad()
    {
        var token = _loadToken;
        Task.Run(() =>
        {
            string text;
            try
            {
                var sample = MachineLoad.Sample(800);
                text = "当前 CPU " + sample.Cpu.ToString("0") + "%    内存 " + sample.Memory.ToString("0") + "%";
            }
            catch (Exception ex)
            {
                text = "读不到当前占用：" + ex.Message;
            }
            try
            {
                BeginInvoke(() =>
                {
                    if (token != _loadToken || IsDisposed)
                        return;
                    _load.Text = text;
                });
            }
            catch
            {
                // 窗口已关闭。
            }
        });
    }

    void RefreshListen()
    {
        var ips = NetInfo.LanIpv4();
        var port = (int)_port.Value;
        var where = ips.Count == 0 ? "本机局域网地址" : string.Join("    ", ips.Select(ip => "http://" + ip + ":" + port));
        _listen.Text = "朋友用浏览器打开：\n" + where + "\n防火墙需要放行这个端口。";
    }

    void Save()
    {
        var password = _password.Text;
        var salt = "";
        var hash = "";
        if (_usePassword.Checked)
        {
            if (password.Length == 0 && _original.HasPassword)
            {
                salt = _original.PasswordSalt;
                hash = _original.PasswordHash;
            }
            else if (password.Length < 4)
            {
                MessageBox.Show(this, "口令至少 4 位。不需要口令时，取消「使用口令」。", "远程启动");
                return;
            }
            else
                (salt, hash) = RemotePassword.Create(password);
        }
        Result = new RemoteSettings
        {
            Enabled = _enabled.Checked,
            Port = (int)_port.Value,
            CpuThreshold = (int)_cpu.Value,
            MemoryThreshold = (int)_memory.Value,
            PasswordSalt = salt,
            PasswordHash = hash,
        };
        DialogResult = DialogResult.OK;
        Close();
    }

    static void Add(TableLayoutPanel layout, int row, string name, Control control)
    {
        layout.Controls.Add(new Label { Text = name, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Text }, 0, row);
        control.Dock = DockStyle.Left;
        control.Width = Math.Max(control.Width, 220);
        if (control is CheckBox)
            control.Dock = DockStyle.Fill;
        layout.Controls.Add(control, 1, row);
    }

    static NumericUpDown Number(int min, int max) => new()
    {
        Minimum = min,
        Maximum = max,
        Width = 120,
        BackColor = Theme.Input,
        ForeColor = Theme.Text,
        Font = Theme.Ui,
    };

    static TextBox Input() => new()
    {
        Width = 280,
        BackColor = Theme.Input,
        ForeColor = Theme.Text,
        BorderStyle = BorderStyle.FixedSingle,
        Font = Theme.Ui,
    };
}

internal sealed class RemoteConfirmForm : Form
{
    readonly Label _left;
    int _seconds = 60;
    public RemoteDecision Decision { get; private set; } = RemoteDecision.Deny;

    public RemoteConfirmForm(PendingStart pending)
    {
        Theme.Style(this);
        Text = "确认远程启动";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        TopMost = true;
        ShowInTaskbar = true;
        ClientSize = new Size(480, 240);
        var body = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Theme.Text,
            Padding = new Padding(16),
            Text = "来自 " + pending.Remote + "\n\n要启动「" + pending.Name + "」。\n当前 CPU " + pending.Cpu.ToString("0") + "%（阈值 " + pending.CpuLimit + "%）\n当前内存 " + pending.Memory.ToString("0") + "%（阈值 " + pending.MemoryLimit + "%）\n\n占用达到或超过阈值。是否仍然启动？",
        };
        _left = new Label { Dock = DockStyle.Bottom, Height = 28, ForeColor = Theme.Muted, Padding = new Padding(16, 0, 0, 0), Text = "60 秒内未确认将取消" };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12, 8, 12, 8) };
        var allow = Theme.MakeButton("允许启动", true);
        var deny = Theme.MakeButton("拒绝");
        allow.Click += (_, _) =>
        {
            Decision = RemoteDecision.Allow;
            Close();
        };
        deny.Click += (_, _) =>
        {
            Decision = RemoteDecision.Deny;
            Close();
        };
        buttons.Controls.Add(allow);
        buttons.Controls.Add(deny);
        Controls.Add(body);
        Controls.Add(_left);
        Controls.Add(buttons);
        var timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += (_, _) =>
        {
            _seconds--;
            if (_seconds <= 0)
            {
                timer.Stop();
                Decision = RemoteDecision.Timeout;
                Close();
                return;
            }
            _left.Text = _seconds + " 秒内未确认将取消";
        };
        timer.Start();
        FormClosed += (_, _) => timer.Stop();
    }
}

internal sealed class RemoteClientForm : Form
{
    readonly TextBox _host = Input();
    readonly NumericUpDown _port = new() { Minimum = 1024, Maximum = 65535, Width = 100, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.Ui };
    readonly TextBox _password = Input();
    readonly ListBox _list = new()
    {
        Dock = DockStyle.Fill,
        BackColor = Theme.Input,
        ForeColor = Theme.Text,
        BorderStyle = BorderStyle.None,
        IntegralHeight = false,
        Font = Theme.Ui,
    };
    readonly Label _status = new() { Dock = DockStyle.Fill, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 12, 0) };
    readonly Button _refresh;
    readonly Button _start;
    readonly System.Windows.Forms.Timer _poll = new() { Interval = 1500 };
    readonly List<RemoteServerView> _servers = new();
    string? _pendingId;
    string? _pendingName;
    bool _working;

    public RemoteClientForm()
    {
        Theme.Style(this);
        Text = "MC 远程启动";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 520);
        Size = new Size(860, 560);
        var saved = RemoteSettingsStore.LoadClient();
        _host.Text = saved.Host;
        _port.Value = saved.Port is >= 1024 and <= 65535 ? saved.Port : 28765;
        _password.Text = saved.Password;
        _password.UseSystemPasswordChar = true;
        _host.PlaceholderText = "主机 IP";
        _password.PlaceholderText = "口令，可留空";

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12, 8, 12, 4) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2 };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        fields.Controls.Add(LabelOf("主机"), 0, 0);
        _host.Dock = DockStyle.Fill;
        fields.Controls.Add(_host, 1, 0);
        fields.SetColumnSpan(_host, 3);
        fields.Controls.Add(LabelOf("端口"), 0, 1);
        fields.Controls.Add(_port, 1, 1);
        fields.Controls.Add(LabelOf("口令"), 2, 1);
        _password.Dock = DockStyle.Fill;
        fields.Controls.Add(_password, 3, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        _refresh = Theme.MakeButton("刷新", true);
        _start = Theme.MakeButton("启动");
        _refresh.Click += (_, _) => Reload();
        _start.Click += (_, _) => RequestStart();
        buttons.Controls.Add(_refresh);
        buttons.Controls.Add(_start);
        top.Controls.Add(fields, 0, 0);
        top.Controls.Add(buttons, 1, 0);

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            ForeColor = Theme.Muted,
            Padding = new Padding(12, 0, 12, 0),
            Text = "只能启动，以及查看地址和端口。停止服务器请在主机上操作。",
        };
        var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 8) };
        listHost.Controls.Add(_list);
        listHost.Controls.Add(hint);
        _list.DoubleClick += (_, _) => RequestStart();

        root.Controls.Add(top, 0, 0);
        root.Controls.Add(listHost, 0, 1);
        root.Controls.Add(_status, 0, 2);
        Controls.Add(root);
        _poll.Tick += (_, _) => PollPending();
        _poll.Start();
        _status.Text = "填写主机地址后点刷新。";
    }

    void Reload() => _ = LoadListAsync();

    async Task LoadListAsync()
    {
        if (_working)
            return;
        if (!TryTarget(out var host, out var port, out var token))
            return;
        _working = true;
        Toggle(false);
        _status.Text = "正在读取…";
        try
        {
            var list = await RemoteApi.ServersAsync(host, port, token, CancellationToken.None);
            RemoteSettingsStore.SaveClient(new RemoteClientSettings { Host = host, Port = port, Password = token });
            _servers.Clear();
            _servers.AddRange(list.Servers);
            var keep = _list.SelectedIndex;
            _list.Items.Clear();
            foreach (var server in _servers)
            {
                var address = server.Endpoints.Count == 0 ? "无地址" : string.Join("    ", server.Endpoints);
                _list.Items.Add(server.Name + "    " + server.State + "    端口 " + server.Port + "    " + address);
            }
            if (_servers.Count == 0)
                _status.Text = "主机上没有识别到服务端。";
            else
            {
                _list.SelectedIndex = keep >= 0 && keep < _servers.Count ? keep : 0;
                _status.Text = "已读取 " + _servers.Count + " 个服务端。";
            }
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
        finally
        {
            _working = false;
            Toggle(true);
        }
    }

    void RequestStart()
    {
        if (_working || _pendingId != null)
            return;
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _servers.Count)
        {
            _status.Text = "先选择一个服务端。";
            return;
        }
        var server = _servers[_list.SelectedIndex];
        if (!server.CanStart)
        {
            _status.Text = string.IsNullOrWhiteSpace(server.Block) ? "这个服务端现在不能启动。" : server.Block;
            return;
        }
        _ = StartAsync(server.Name);
    }

    async Task StartAsync(string name)
    {
        if (!TryTarget(out var host, out var port, out var token))
            return;
        _working = true;
        Toggle(false);
        _status.Text = "正在请求启动「" + name + "」…";
        RemoteStartResponse? result = null;
        try
        {
            result = await RemoteApi.StartAsync(host, port, token, name, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
        finally
        {
            _working = false;
            Toggle(true);
        }
        if (result != null)
            ApplyResult(result, name);
    }

    async void PollPending()
    {
        if (_pendingId == null || _working)
            return;
        if (!TryTarget(out var host, out var port, out var token))
            return;
        var id = _pendingId;
        var name = _pendingName ?? "";
        _working = true;
        RemoteStartResponse? result = null;
        try
        {
            result = await RemoteApi.RequestAsync(host, port, token, id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _pendingId = null;
            _status.Text = ex.Message;
        }
        finally
        {
            _working = false;
            Toggle(true);
        }
        if (result != null && _pendingId == id)
            ApplyResult(result, name);
    }

    void ApplyResult(RemoteStartResponse result, string name)
    {
        if (result.Status == "pending")
        {
            _pendingId = result.Id;
            _pendingName = name;
            _status.Text = string.IsNullOrWhiteSpace(result.Message) ? "等待主机确认…" : result.Message;
            return;
        }
        _pendingId = null;
        _pendingName = null;
        _status.Text = string.IsNullOrWhiteSpace(result.Message) ? result.Status : result.Message;
        if (result.Status is "denied" or "expired" or "failed")
            MessageBox.Show(this, _status.Text, "远程启动");
        if (result.Status == "started")
            _ = LoadListAsync();
    }

    bool TryTarget(out string host, out int port, out string token)
    {
        host = _host.Text.Trim();
        port = (int)_port.Value;
        token = _password.Text;
        if (host.Length == 0)
        {
            _status.Text = "填写主机地址。";
            return false;
        }
        if (host.Count(ch => ch == ':') == 1)
        {
            _status.Text = "地址栏只填 IP，端口填在旁边。";
            return false;
        }
        return true;
    }

    void Toggle(bool enabled)
    {
        _refresh.Enabled = enabled && _pendingId == null;
        _start.Enabled = enabled && _pendingId == null;
    }

    static Label LabelOf(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Theme.Muted,
    };

    static TextBox Input() => new()
    {
        BackColor = Theme.Input,
        ForeColor = Theme.Text,
        BorderStyle = BorderStyle.FixedSingle,
        Font = Theme.Ui,
    };
}
