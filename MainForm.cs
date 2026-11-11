using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace McPanel;

internal sealed class MainForm : Form, IRemoteDesk
{
    const int WM_SETREDRAW = 0x000B;

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    readonly List<ServerSnapshot> _servers = new();
    readonly Dictionary<string, RuntimeState> _runtime = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 300 };
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = 800 };
    FileSystemWatcher? _watcher;

    ListBox _list = null!;
    Label _subtitle = null!;
    Label _count = null!;
    Label _name = null!;
    Label _detail = null!;
    RichTextBox _log = null!;
    Label _logSource = null!;
    TextBox _command = null!;
    CheckBox _follow = null!;
    Label _status = null!;
    Button _start = null!;
    Button _stop = null!;
    Button _kill = null!;
    Button _focus = null!;
    Button _backup = null!;
    Button _newWorld = null!;
    Button _import = null!;
    Button _restore = null!;
    Button _open = null!;
    Button _config = null!;
    Button _crash = null!;
    Button _send = null!;
    Button _refresh = null!;

    string? _root;
    string? _shownPath;
    string _dirSig = "";
    string _worldSize = "";
    LogTail? _tail;
    bool _binding;
    bool _busy;
    bool _firstLoad = true;
    bool _crashAlert;
    DateTime _seenCrash = DateTime.MinValue;
    RemoteHostService? _remote;
    int _sizeToken;
    int _tick;

    public MainForm()
    {
        Theme.Style(this);
        Text = "MC 服务端管理器";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1040, 700);
        Size = new Size(1220, 780);
        KeyPreview = true;
        DoubleBuffered = true;
        BuildLayout();
        _timer.Tick += (_, _) => Tick();
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            RefreshList();
        };
        _root = RootLocator.Find();
        if (_root != null)
            SetupWatcher();
        Shown += (_, _) => RefreshList();
        Shown += (_, _) => ApplyRemoteListener(RemoteSettingsStore.LoadHost());
        _timer.Start();
    }

    void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Bg };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Theme.Panel, Padding = new Padding(16, 8, 12, 8) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
        var titles = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        titles.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        titles.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var title = new Label
        {
            Dock = DockStyle.Fill,
            Font = Theme.Title,
            ForeColor = Theme.Text,
            Text = "MC 服务端管理器",
        };
        _subtitle = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            AutoEllipsis = true,
            Text = "正在查找服务端目录…",
        };
        titles.Controls.Add(title, 0, 0);
        titles.Controls.Add(_subtitle, 0, 1);
        var topButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        _refresh = Theme.MakeButton("刷新", true);
        var change = Theme.MakeButton("更换目录");
        var remote = Theme.MakeButton("远程启动");
        _refresh.Click += (_, _) => RefreshList();
        change.Click += (_, _) => ChooseRoot();
        remote.Click += (_, _) => EditRemote();
        topButtons.Controls.Add(_refresh);
        topButtons.Controls.Add(change);
        topButtons.Controls.Add(remote);
        top.Controls.Add(titles, 0, 0);
        top.Controls.Add(topButtons, 1, 0);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6, BackColor = Theme.Border };
        split.Panel1.BackColor = Theme.Panel;
        split.Panel2.BackColor = Theme.Bg;
        BuildList(split.Panel1);
        BuildDetail(split.Panel2);

        _status = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 12, 0),
            Text = "F5 刷新目录",
        };

        root.Controls.Add(top, 0, 0);
        root.Controls.Add(split, 0, 1);
        root.Controls.Add(_status, 0, 2);
        Controls.Add(root);

        Shown += (_, _) =>
        {
            try
            {
                split.Panel1MinSize = 240;
                split.Panel2MinSize = 520;
                split.SplitterDistance = 320;
            }
            catch
            {
                // 窗口还没布局完时跳过，使用默认分隔位置。
            }
        };
    }

    void BuildList(Control parent)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Padding = new Padding(8, 8, 4, 8) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _count = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Muted, Text = "服务端" };
        _list = new ListBox
        {
            Dock = DockStyle.Fill,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 74,
            IntegralHeight = false,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Panel,
            ForeColor = Theme.Text,
        };
        typeof(ListBox).InvokeMember("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.SetProperty, null, _list, [true]);
        _list.DrawItem += DrawServer;
        _list.SelectedIndexChanged += (_, _) =>
        {
            if (!_binding)
                OnSelected();
        };
        layout.Controls.Add(_count, 0, 0);
        layout.Controls.Add(_list, 0, 1);
        parent.Controls.Add(layout);
    }

    void BuildDetail(Control parent)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(8, 8, 12, 8) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 168));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 148));

        var card = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Card, Padding = new Padding(12, 8, 12, 8), RowCount = 2 };
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _name = new Label { Dock = DockStyle.Fill, Font = Theme.Name, ForeColor = Theme.Text, Text = "未选择服务端" };
        _detail = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Muted, Text = "在左侧选择一个服务端。" };
        card.Controls.Add(_name, 0, 0);
        card.Controls.Add(_detail, 0, 1);

        _logSource = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Muted, AutoEllipsis = true, Text = "日志" };
        _log = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Input,
            ForeColor = Theme.Text,
            Font = Theme.Log,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            DetectUrls = false,
            HideSelection = false,
            WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.Both,
        };
        var logMenu = new ContextMenuStrip();
        logMenu.Items.Add("复制", null, (_, _) => _log.Copy());
        logMenu.Items.Add("全部复制", null, (_, _) =>
        {
            _log.SelectAll();
            _log.Copy();
            _log.DeselectAll();
        });
        logMenu.Items.Add("打开日志文件", null, (_, _) => OpenCurrentLog());
        _log.ContextMenuStrip = logMenu;

        var commandRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        commandRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        commandRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        commandRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        _command = new TextBox
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Input,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.Ui,
            PlaceholderText = "输入指令，例如 list、say hi、stop",
        };
        _command.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter)
                return;
            e.SuppressKeyPress = true;
            SendCommand();
        };
        _send = Theme.MakeButton("发送", true);
        _send.Dock = DockStyle.Fill;
        _send.Click += (_, _) => SendCommand();
        _follow = new CheckBox
        {
            Text = "跟随最新",
            Checked = true,
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
        };
        commandRow.Controls.Add(_command, 0, 0);
        commandRow.Controls.Add(_send, 1, 0);
        commandRow.Controls.Add(_follow, 2, 0);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoScroll = true };
        _start = Theme.MakeButton("启动", true);
        _stop = Theme.MakeButton("停止");
        _kill = Theme.MakeButton("强制结束");
        _focus = Theme.MakeButton("前置窗口");
        _backup = Theme.MakeButton("备份世界");
        _newWorld = Theme.MakeButton("创建新世界");
        _import = Theme.MakeButton("导入替换世界");
        _restore = Theme.MakeButton("从备份恢复");
        _open = Theme.MakeButton("打开目录");
        _config = Theme.MakeButton("服务器配置");
        _crash = Theme.MakeButton("崩溃记录");
        _start.Click += (_, _) => StartSelected();
        _stop.Click += (_, _) => StopSelected();
        _kill.Click += (_, _) => KillSelected();
        _focus.Click += (_, _) => FocusWindow();
        _backup.Click += (_, _) => BackupWorld();
        _newWorld.Click += (_, _) => CreateNewWorld();
        _restore.Click += (_, _) => RestoreWorld();
        _open.Click += (_, _) => OpenServerFolder();
        _config.Click += (_, _) => EditConfig();
        _crash.Click += (_, _) => ShowCrashes();
        var importMenu = new ContextMenuStrip();
        importMenu.Items.Add("从 ZIP 压缩包替换…", null, (_, _) => ImportZip());
        importMenu.Items.Add("从文件夹替换…", null, (_, _) => ImportFolder());
        _import.Click += (_, _) => importMenu.Show(_import, new Point(0, _import.Height));
        foreach (var button in new[] { _start, _stop, _kill, _focus, _backup, _newWorld, _import, _restore, _open, _config, _crash })
            actions.Controls.Add(button);

        layout.Controls.Add(card, 0, 0);
        layout.Controls.Add(_logSource, 0, 1);
        layout.Controls.Add(_log, 0, 2);
        layout.Controls.Add(commandRow, 0, 3);
        layout.Controls.Add(actions, 0, 4);
        parent.Controls.Add(layout);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F5)
        {
            e.Handled = true;
            RefreshList();
        }
        base.OnKeyDown(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            MessageBox.Show(this, "世界备份或导入还在进行，请等它结束再关闭。");
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _debounce.Stop();
        _watcher?.Dispose();
        _remote?.Dispose();
        _remote = null;
        base.OnFormClosed(e);
    }

    void ChooseRoot()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择存放各个 MC 服务端文件夹的目录",
            UseDescriptionForTitle = true,
            SelectedPath = _root ?? "",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        _root = dialog.SelectedPath;
        RootLocator.Remember(_root);
        _runtime.Clear();
        _known.Clear();
        _firstLoad = true;
        SetupWatcher();
        RefreshList();
    }

    void SetupWatcher()
    {
        _watcher?.Dispose();
        if (string.IsNullOrEmpty(_root))
            return;
        _watcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.DirectoryName,
            EnableRaisingEvents = true,
        };
        FileSystemEventHandler handler = (_, _) => DebouncedRefresh();
        _watcher.Created += handler;
        _watcher.Deleted += handler;
        _watcher.Renamed += (_, _) => DebouncedRefresh();
    }

    void DebouncedRefresh()
    {
        if (IsDisposed)
            return;
        try
        {
            BeginInvoke(() =>
            {
                _debounce.Stop();
                _debounce.Start();
            });
        }
        catch
        {
            // 窗口关闭时忽略目录变化。
        }
    }

    void RefreshList()
    {
        if (_root == null || !Directory.Exists(_root))
        {
            _subtitle.Text = "还没有选择目录。点右上角「更换目录」。";
            _servers.Clear();
            BindList();
            return;
        }

        var scan = ServerScanner.Scan(_root);
        var added = new List<string>();
        foreach (var server in scan.Servers)
        {
            if (!_known.Contains(server.Directory))
            {
                if (!_firstLoad)
                    added.Add(server.Name);
                _known.Add(server.Directory);
                _runtime[server.Directory] = Adopt(server.Directory);
            }
        }
        var alive = new HashSet<string>(scan.Servers.Select(server => server.Directory), StringComparer.OrdinalIgnoreCase);
        foreach (var key in _known.Where(path => !alive.Contains(path)).ToList())
        {
            _known.Remove(key);
            _runtime.Remove(key);
        }
        _servers.Clear();
        _servers.AddRange(scan.Servers);
        _dirSig = ServerScanner.Signature(_root);
        _firstLoad = false;
        BindList();

        _subtitle.Text = SubtitleText();
        _count.Text = "服务端（" + _servers.Count + "）";
        if (added.Count > 0)
            _status.Text = "发现新服务端：" + string.Join("、", added);
        else if (scan.Unrecognized.Count > 0)
            _status.Text = "已识别 " + _servers.Count + " 个。未识别文件夹：" + string.Join("、", scan.Unrecognized);
        else
            _status.Text = "已识别 " + _servers.Count + " 个服务端";
        try { Directory.CreateDirectory(PanelPaths.Data(_root)); } catch { }
    }

    void BindList()
    {
        _binding = true;
        var keep = Selected?.Directory ?? _shownPath;
        _list.BeginUpdate();
        _list.Items.Clear();
        var index = -1;
        for (var i = 0; i < _servers.Count; i++)
        {
            _list.Items.Add(_servers[i].Name);
            if (keep != null && string.Equals(_servers[i].Directory, keep, StringComparison.OrdinalIgnoreCase))
                index = i;
        }
        if (index >= 0)
            _list.SelectedIndex = index;
        else if (_servers.Count > 0)
            _list.SelectedIndex = 0;
        _list.EndUpdate();
        _binding = false;
        if (_servers.Count == 0)
        {
            _shownPath = null;
            _tail = null;
            _log.Clear();
        }
        OnSelected();
    }

    void OnSelected()
    {
        var server = Selected;
        if (server == null)
        {
            _name.Text = "未选择服务端";
            _detail.Text = _root == null ? "先选择存放服务端的目录。" : "这个目录下还没有识别到服务端文件夹。";
            UpdateButtons();
            return;
        }
        var switched = !string.Equals(_shownPath, server.Directory, StringComparison.OrdinalIgnoreCase);
        if (switched)
        {
            AttachLog(server, false);
            _crashAlert = false;
            _seenCrash = NewestCrash(server);
            RequestWorldSize(server);
        }
        UpdateDetails();
    }

    void Tick()
    {
        PumpLog();
        if (++_tick % 3 != 0)
            return;
        var changed = false;
        foreach (var server in _servers)
        {
            if (RefreshRuntime(server))
                changed = true;
        }
        if (changed)
            _list.Invalidate();
        WatchCrash();
        if (_tick % 9 == 0 && _root != null && !_busy)
        {
            var signature = SafeSignature(_root);
            if (signature != _dirSig)
                RefreshList();
        }
        if (_tick % 30 == 0 && _root != null)
            _subtitle.Text = SubtitleText();
        UpdateDetails();
    }

    static string SafeSignature(string root)
    {
        try { return ServerScanner.Signature(root); }
        catch { return ""; }
    }

    void DrawServer(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _servers.Count)
            return;
        var server = _servers[e.Index];
        var runtime = Rt(server);
        var selected = (e.State & DrawItemState.Selected) != 0;
        var bounds = e.Bounds;
        using var background = new SolidBrush(selected ? Theme.Selected : Theme.Panel);
        e.Graphics.FillRectangle(background, bounds);
        if (selected)
        {
            using var bar = new SolidBrush(Theme.Accent);
            e.Graphics.FillRectangle(bar, bounds.X, bounds.Y, 3, bounds.Height);
        }
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var dot = new SolidBrush(StateColor(runtime.State));
        e.Graphics.FillEllipse(dot, bounds.X + 14, bounds.Y + 14, 10, 10);
        var nameRect = new Rectangle(bounds.X + 32, bounds.Y + 8, bounds.Width - 40, 24);
        var subRect = new Rectangle(bounds.X + 32, bounds.Y + 32, bounds.Width - 40, 18);
        var winRect = new Rectangle(bounds.X + 32, bounds.Y + 50, bounds.Width - 40, 18);
        TextRenderer.DrawText(e.Graphics, server.Name, Theme.UiBold, nameRect, Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        var clash = _servers.Count(item => item.Port == server.Port) > 1;
        var sub = StateText(runtime.State) + "    端口 " + server.Port + "    " + server.VersionLabel;
        if (clash)
            sub += "    端口相同";
        TextRenderer.DrawText(e.Graphics, sub, Theme.Ui, subRect, clash ? Theme.Yellow : Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        var window = runtime.State is RunState.Stopped or RunState.Crashed
            ? "窗口：未运行"
            : "窗口：" + (string.IsNullOrEmpty(runtime.WindowTitle) ? "准备中" : runtime.WindowTitle);
        TextRenderer.DrawText(e.Graphics, window, Theme.Ui, winRect, Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    void UpdateDetails()
    {
        var server = Selected;
        if (server == null)
        {
            UpdateButtons();
            return;
        }
        var runtime = Rt(server);
        var state = StateText(runtime.State);
        if (runtime.State == RunState.Crashed && runtime.ExitCode is int code)
            state += "（退出码 " + code + "）";
        var endpoints = runtime.Endpoints.Count > 0 ? runtime.Endpoints : NetInfo.Endpoints(server.Port);
        var window = runtime.State is RunState.Stopped
            ? "未运行"
            : string.IsNullOrEmpty(runtime.WindowTitle) ? LaunchPlan.WindowTitle(server.Name, server.Port) : runtime.WindowTitle;
        if (runtime.State == RunState.Detached)
            window += "（控制窗口已关闭，进程还在）";
        var online = server.OnlineMode switch
        {
            true => "开",
            false => "关",
            _ => "未知",
        };
        var lines = new List<string>
        {
            "状态：" + state + PidText(runtime),
            "本机地址：" + string.Join("    ", endpoints),
            "运行窗口：" + window,
            "世界：" + server.LevelName + (string.IsNullOrEmpty(_worldSize) ? "" : "（" + _worldSize + "）") + "    " + server.VersionLabel,
            "MOTD：" + (string.IsNullOrEmpty(server.Motd) ? "（空）" : server.Motd) + "    人数上限 " + server.MaxPlayers + "    正版验证 " + online,
        };
        if (!string.IsNullOrEmpty(server.MemoryWarning))
            lines.Add(server.MemoryWarning);
        if (!string.IsNullOrEmpty(server.LaunchError))
            lines.Add(server.LaunchError);
        var samePort = _servers.Where(item => item.Port == server.Port && !string.Equals(item.Directory, server.Directory, StringComparison.OrdinalIgnoreCase)).Select(item => item.Name).ToList();
        if (samePort.Count > 0)
            lines.Add("端口 " + server.Port + " 还被这些服使用，不能同时开：" + string.Join("、", samePort));
        var text = string.Join(Environment.NewLine, lines);
        if (_detail.Text != text)
            _detail.Text = text;
        if (_name.Text != server.Name)
            _name.Text = server.Name;
        UpdateButtons();
    }

    static string PidText(RuntimeState runtime)
    {
        if (runtime.State is RunState.Stopped or RunState.Crashed)
            return "";
        var parts = new List<string>();
        if (runtime.ViewerPid > 0)
            parts.Add("窗口 " + runtime.ViewerPid);
        if (runtime.JavaPid > 0)
            parts.Add("Java " + runtime.JavaPid);
        return parts.Count == 0 ? "" : "    " + string.Join(" / ", parts);
    }

    void UpdateButtons()
    {
        var server = Selected;
        var state = server == null ? RunState.Stopped : Rt(server).State;
        var live = state is RunState.Starting or RunState.Running or RunState.Stopping or RunState.Detached;
        _start.Enabled = server != null && !live && !_busy && !string.IsNullOrWhiteSpace(server.JavaArgs);
        _stop.Enabled = server != null && state is RunState.Starting or RunState.Running;
        _kill.Enabled = server != null && live && !_busy;
        _focus.Enabled = server != null && live;
        _backup.Enabled = server != null && !_busy;
        _newWorld.Enabled = server != null && !_busy && !live;
        _import.Enabled = server != null && !_busy && !live;
        _restore.Enabled = server != null && !_busy && !live;
        _open.Enabled = server != null;
        _config.Enabled = server != null && !_busy;
        _crash.Enabled = server != null;
        _send.Enabled = server != null && state is RunState.Starting or RunState.Running or RunState.Stopping;
        _command.Enabled = _send.Enabled;
        _crash.Text = _crashAlert ? "崩溃记录 ●" : "崩溃记录";
        _crash.BackColor = _crashAlert ? Theme.Red : Theme.Button;
    }

    void AttachLog(ServerSnapshot server, bool fresh)
    {
        _shownPath = server.Directory;
        var path = ChooseLog(server);
        _logSource.Text = path == null ? "还没有日志文件" : "日志来源：" + path;
        _log.Clear();
        if (path == null || !File.Exists(path))
        {
            _tail = path == null ? null : new LogTail(path, false);
            AppendRaw("这个服务端还没有日志。\n启动后，这里和单独的日志窗口都会显示输出。\n", Theme.Muted);
            return;
        }
        var fromEnd = !fresh && new FileInfo(path).Length > 180_000;
        _tail = new LogTail(path, fromEnd);
        AppendLines(_tail.ReadNew());
    }

    string? ChooseLog(ServerSnapshot server)
    {
        var console = PanelPaths.ConsoleLog(server.Directory);
        var latest = Path.Combine(server.Directory, "logs", "latest.log");
        var state = Rt(server).State;
        if (state is RunState.Running or RunState.Starting or RunState.Stopping or RunState.Detached)
            return File.Exists(console) || !File.Exists(latest) ? console : latest;
        if (File.Exists(console) && File.Exists(latest))
            return File.GetLastWriteTimeUtc(console) >= File.GetLastWriteTimeUtc(latest) ? console : latest;
        if (File.Exists(console))
            return console;
        if (File.Exists(latest))
            return latest;
        return null;
    }

    void PumpLog()
    {
        if (_tail == null)
            return;
        var lines = _tail.ReadNew();
        if (_tail.Restarted)
            _log.Clear();
        if (lines.Count > 0)
            AppendLines(lines);
    }

    void AppendLines(List<string> lines)
    {
        if (lines.Count == 0 || !_log.IsHandleCreated)
            return;
        SendMessage(_log.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        foreach (var line in lines)
            AppendRaw(line + "\n", ColorFor(line));
        if (_log.TextLength > 700_000)
        {
            var cut = _log.GetFirstCharIndexFromLine(Math.Max(1, _log.Lines.Length / 3));
            if (cut > 0)
            {
                _log.Select(0, cut);
                _log.SelectedText = "";
            }
        }
        SendMessage(_log.Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
        _log.Invalidate();
        if (_follow.Checked)
        {
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }
    }

    void AppendRaw(string text, Color color)
    {
        _log.SelectionStart = _log.TextLength;
        _log.SelectionLength = 0;
        _log.SelectionColor = color;
        _log.AppendText(text);
    }

    static Color ColorFor(string line) => LogPaint.Kind(line) switch
    {
        LineKind.Error => Theme.Red,
        LineKind.Warn => Theme.Yellow,
        LineKind.Ok => Theme.Green,
        LineKind.Banner => Theme.Accent,
        _ => Theme.Text,
    };

    void StartSelected()
    {
        var server = Selected;
        if (server == null || _root == null)
            return;
        var error = TryLaunch(server, interactive: true);
        if (!string.IsNullOrEmpty(error))
            MessageBox.Show(this, error, "无法启动");
    }

    void EditRemote()
    {
        using var dialog = new RemoteSettingsForm(RemoteSettingsStore.LoadHost());
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        RemoteSettingsStore.SaveHost(dialog.Result);
        ApplyRemoteListener(dialog.Result);
    }

    void ApplyRemoteListener(RemoteSettings settings)
    {
        _remote?.Dispose();
        _remote = null;
        if (!settings.Enabled)
        {
            _subtitle.Text = SubtitleText();
            return;
        }
        try
        {
            var service = new RemoteHostService(this, CaptureRemoteServers, RemoteTryLaunch, this);
            service.Start(settings);
            _remote = service;
            _status.Text = "远程启动已监听端口 " + service.Port;
            _subtitle.Text = SubtitleText();
        }
        catch (Exception ex)
        {
            _status.Text = "远程启动没有开起来";
            MessageBox.Show(this, ex.Message, "远程启动");
        }
    }

    string SubtitleText()
    {
        if (_root == null)
            return "正在查找服务端目录…";
        var ips = NetInfo.LanIpv4();
        var ipText = ips.Count == 0 ? "本机 IP：仅 127.0.0.1" : "本机 IP：" + string.Join("    ", ips);
        var remote = _remote is { Listening: true } ? "    远程端口 " + _remote.Port : "";
        return _root + "    " + ipText + remote + "    新服务端放进此目录后点刷新";
    }

    List<RemoteServerView> CaptureRemoteServers()
    {
        var list = new List<RemoteServerView>();
        foreach (var server in _servers)
        {
            var runtime = Rt(server);
            var live = runtime.State is RunState.Starting or RunState.Running or RunState.Stopping or RunState.Detached;
            var endpoints = runtime.Endpoints.Count > 0 ? runtime.Endpoints : NetInfo.Endpoints(server.Port);
            var block = "";
            if (live)
                block = "「" + server.Name + "」已经在运行。";
            else if (_busy)
                block = "主机正在备份或导入世界。";
            else if (string.IsNullOrWhiteSpace(server.JavaArgs))
                block = server.LaunchError ?? "找不到启动命令。";
            else if (!server.EulaAccepted)
                block = "请先在主机上同意 Minecraft EULA。";
            list.Add(new RemoteServerView
            {
                Name = server.Name,
                Port = server.Port,
                State = StateText(runtime.State),
                Endpoints = endpoints,
                CanStart = block.Length == 0,
                Block = block,
            });
        }
        return list;
    }

    string? RemoteTryLaunch(string name)
    {
        if (InvokeRequired)
            return (string?)Invoke(() => RemoteTryLaunch(name));
        if (_root == null)
            return "主机还没有服务端目录。";
        if (_busy)
            return "主机正在备份或导入世界。";
        var matches = _servers.Where(item => item.Name == name).ToList();
        if (matches.Count == 0)
            return "找不到这个服务端。";
        if (matches.Count > 1)
            return "有多个同名服务端，请在主机上处理。";
        var error = TryLaunch(matches[0], interactive: false);
        if (error == null)
            _status.Text = "已接受远程启动：「" + name + "」";
        return error;
    }

    string? TryLaunch(ServerSnapshot server, bool interactive)
    {
        if (_root == null)
            return "还没有选择服务端目录。";
        var state = Rt(server).State;
        if (state is RunState.Starting or RunState.Running or RunState.Stopping or RunState.Detached)
            return "「" + server.Name + "」已经在运行。";
        if (string.IsNullOrWhiteSpace(server.JavaArgs))
            return server.LaunchError ?? "找不到启动命令。";
        var java = NetInfo.FindJava();
        if (java == null)
            return "没有在 PATH 里找到 java.exe。请先安装并配置 Java。";
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return "找不到管理器程序路径。";
        if (!server.EulaAccepted)
        {
            if (!interactive)
                return "请先在主机上同意 Minecraft EULA。";
            var agree = MessageBox.Show(this, "启动前需要同意 Minecraft EULA（https://aka.ms/MinecraftEULA）。\n同意并写入 eula.txt？", "EULA", MessageBoxButtons.YesNo);
            if (agree != DialogResult.Yes)
                return null;
            EulaFile.Accept(server.Directory);
            server.EulaAccepted = true;
        }

        var others = _servers.Where(item => item.Port == server.Port && !Same(item, server)).ToList();
        var runningOther = others.FirstOrDefault(item => Rt(item).State is RunState.Starting or RunState.Running or RunState.Stopping or RunState.Detached);
        if (runningOther != null)
            return "端口 " + server.Port + " 正在被「" + runningOther.Name + "」使用。请先停止它，或修改端口。";
        if (interactive && others.Count > 0)
        {
            var confirm = MessageBox.Show(this, "这些服务端也使用端口 " + server.Port + "：\n" + string.Join("\n", others.Select(item => item.Name)) + "\n\n同时只能开一个。确定启动「" + server.Name + "」吗？", "端口相同", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes)
                return null;
        }
        if (NetInfo.PortInUse(server.Port))
        {
            if (!interactive)
                return "本机 " + server.Port + " 端口已经有程序在监听。";
            var confirm = MessageBox.Show(this, "本机 " + server.Port + " 端口已经有程序在监听。仍然启动「" + server.Name + "」吗？", "端口被占用", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes)
                return null;
        }

        try
        {
            var title = LaunchPlan.WindowTitle(server.Name, server.Port);
            var endpoints = NetInfo.Endpoints(server.Port);
            Directory.CreateDirectory(PanelPaths.Panel(server.Directory));
            Directory.CreateDirectory(PanelPaths.Crash(_root, server.Name));
            try { File.WriteAllText(PanelPaths.ConsoleLog(server.Directory), ""); } catch { }
            try { File.Delete(PanelPaths.Instance(server.Directory)); } catch { }
            var session = new HostSession
            {
                ServerName = server.Name,
                ServerDir = server.Directory,
                PanelDir = PanelPaths.Panel(server.Directory),
                CrashDir = PanelPaths.Crash(_root, server.Name),
                JavaExe = java,
                JavaArgs = server.JavaArgs,
                WindowTitle = title,
                Port = server.Port,
                Endpoints = endpoints,
                Banner = LaunchPlan.Banner(server.Name, endpoints, title, java),
            };
            JsonFiles.Write(PanelPaths.Session(server.Directory), session);
            var info = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = server.Directory,
            };
            info.ArgumentList.Add("--host");
            info.ArgumentList.Add(PanelPaths.Session(server.Directory));
            var host = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException("没有启动控制进程。");
            var runtime = Rt(server);
            runtime.State = RunState.Starting;
            runtime.HostPid = host.Id;
            runtime.HostProcess = host;
            runtime.JavaPid = 0;
            runtime.WindowTitle = title;
            runtime.Endpoints = endpoints;
            runtime.StartedAt = DateTime.Now;
            runtime.ExitCode = null;
            runtime.CrashLog = null;
            OpenViewer(server, host.Id, title, exe);
            AttachLog(server, true);
            _status.Text = "已启动「" + server.Name + "」。地址 " + string.Join("  ", endpoints) + "    窗口 " + title;
            _list.Invalidate();
            UpdateDetails();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    void OpenViewer(ServerSnapshot server, int hostPid, string title, string? exe = null)
    {
        exe ??= Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return;
        var runtime = Rt(server);
        if (runtime.ViewerPid > 0 && ProcUtil.Alive(runtime.ViewerPid, "McPanel"))
        {
            try
            {
                using var old = System.Diagnostics.Process.GetProcessById(runtime.ViewerPid);
                old.Kill();
            }
            catch
            {
                // 旧日志窗口关不掉时继续开一个新的。
            }
        }
        var session = new ViewerSession
        {
            LogPath = PanelPaths.ConsoleLog(server.Directory),
            WindowTitle = title,
            InstancePath = PanelPaths.Instance(server.Directory),
            HostPid = hostPid,
        };
        JsonFiles.Write(PanelPaths.ViewerSession(server.Directory), session);
        var info = new System.Diagnostics.ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = server.Directory,
        };
        info.ArgumentList.Add("--view");
        info.ArgumentList.Add(PanelPaths.ViewerSession(server.Directory));
        var process = System.Diagnostics.Process.Start(info);
        if (process != null)
            runtime.ViewerPid = process.Id;
    }

    void StopSelected()
    {
        var server = Selected;
        if (server == null)
            return;
        PostCommand(server, "stop");
        var runtime = Rt(server);
        runtime.State = RunState.Stopping;
        _status.Text = "已向「" + server.Name + "」发送 stop，正在等待关闭。";
        UpdateDetails();
    }

    void KillSelected()
    {
        var server = Selected;
        if (server == null)
            return;
        var runtime = Rt(server);
        var answer = MessageBox.Show(this, "强制结束「" + server.Name + "」可能导致刚才的存档没写完。\n确定继续吗？", "强制结束", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
            return;
        if (runtime.State == RunState.Detached || !ProcUtil.Alive(runtime.HostPid, "McPanel"))
        {
            try
            {
                if (runtime.JavaPid > 0)
                {
                    using var java = System.Diagnostics.Process.GetProcessById(runtime.JavaPid);
                    java.Kill(true);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "强制结束失败");
                return;
            }
        }
        else
        {
            PostCommand(server, "__KILL__");
        }
        runtime.State = RunState.Stopping;
        _status.Text = "正在强制结束「" + server.Name + "」。";
        UpdateDetails();
    }

    void FocusWindow()
    {
        var server = Selected;
        if (server == null)
            return;
        var runtime = Rt(server);
        var pid = runtime.ViewerPid;
        if (!ProcUtil.Alive(pid, "McPanel"))
        {
            var mark = JsonFiles.Read<ViewerMark>(PanelPaths.ViewerMark(server.Directory));
            if (mark != null && ProcUtil.Alive(mark.Pid, "McPanel"))
                pid = mark.Pid;
        }
        if (ProcUtil.Alive(pid, "McPanel"))
        {
            var hwnd = Win32.FindVisibleWindow(pid, string.IsNullOrEmpty(runtime.WindowTitle) ? null : runtime.WindowTitle);
            if (hwnd == IntPtr.Zero)
                hwnd = Win32.FindVisibleWindow(pid, null);
            if (hwnd != IntPtr.Zero)
            {
                Win32.BringToFront(hwnd);
                _status.Text = "已切换到窗口：" + runtime.WindowTitle;
                return;
            }
        }
        var title = string.IsNullOrEmpty(runtime.WindowTitle) ? LaunchPlan.WindowTitle(server.Name, server.Port) : runtime.WindowTitle;
        OpenViewer(server, runtime.HostPid, title);
        _status.Text = "已重新打开日志窗口：" + title;
    }

    void SendCommand()
    {
        var server = Selected;
        if (server == null)
            return;
        var command = _command.Text.Trim();
        if (command.Length == 0 || command.StartsWith("__", StringComparison.Ordinal))
            return;
        PostCommand(server, command);
        _command.Clear();
        _status.Text = "已发送指令：" + command;
    }

    static void PostCommand(ServerSnapshot server, string command)
    {
        var inbox = PanelPaths.Inbox(server.Directory);
        Directory.CreateDirectory(inbox);
        var name = DateTime.UtcNow.Ticks.ToString("D20") + "-" + Guid.NewGuid().ToString("N");
        var finalPath = Path.Combine(inbox, name + ".txt");
        File.WriteAllText(finalPath + ".tmp", command);
        File.Move(finalPath + ".tmp", finalPath);
    }

    void CreateNewWorld()
    {
        var server = Selected;
        if (server == null)
            return;
        var state = Rt(server).State;
        if (state is RunState.Starting or RunState.Running or RunState.Stopping or RunState.Detached)
        {
            MessageBox.Show(this, "请先停止「" + server.Name + "」，再创建新世界。服务端运行时改配置，关服后会被游戏写回去。", "服务器正在运行");
            return;
        }
        var currentWorld = Path.Combine(server.Directory, server.LevelName);
        if (WorldService.IsWorldLocked(currentWorld))
        {
            MessageBox.Show(this, "当前世界文件仍被占用。请确认服务端已经退出。", "世界被占用");
            return;
        }
        var suggested = WorldService.SuggestNewLevelName(server.Directory, server.LevelName);
        using var dialog = new NewWorldForm(server.LevelName, suggested);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            var created = WorldService.CreateFreshWorld(server.Directory, server.LevelName, dialog.WorldName, dialog.Seed);
            RefreshList();
            _status.Text = "已切换到新世界「" + dialog.WorldName + "」，旧世界「" + server.LevelName + "」仍保留。";
            MessageBox.Show(this, "新世界文件夹：\n" + created + "\n\n配置里的世界文件夹已改为「" + dialog.WorldName + "」。\n旧世界「" + server.LevelName + "」还在服务端目录里，没有删除。\n地形会在下次启动时生成。想回到旧世界时，把配置里的世界文件夹改回去即可。", "新世界已创建");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "创建失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    async void BackupWorld()
    {
        var server = Selected;
        if (server == null)
            return;
        var runtime = Rt(server);
        if (runtime.State is RunState.Starting or RunState.Running or RunState.Stopping or RunState.Detached)
        {
            var answer = MessageBox.Show(this, "服务器正在运行，现在备份可能不完整。建议先停止。\n仍然备份吗？", "备份世界", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes)
                return;
        }
        var backup = "";
        await RunWorld("正在备份", progress =>
        {
            backup = WorldService.Backup(server.Directory, server.LevelName, progress);
            return backup;
        }, "备份完成");
    }

    async void ImportZip()
    {
        var server = RequireStopped();
        if (server == null)
            return;
        using var dialog = new OpenFileDialog
        {
            Title = "选择世界压缩包",
            Filter = "世界压缩包 (*.zip)|*.zip",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        if (!WorldService.ZipContainsLevelDat(dialog.FileName))
        {
            MessageBox.Show(this, "压缩包里没有 level.dat，这不是 Minecraft 世界存档。\n新的服务端请解压成文件夹放进目录，然后点刷新。", "不是世界存档");
            return;
        }
        if (!ConfirmReplace(server, dialog.FileName))
            return;
        await RunWorld("正在导入", progress =>
        {
            var backup = WorldService.ReplaceFromZip(server.Directory, server.LevelName, dialog.FileName, progress);
            return string.IsNullOrEmpty(backup) ? "世界已替换。" : "世界已替换。\n旧世界备份在：\n" + backup;
        }, "完成");
    }

    async void ImportFolder()
    {
        var server = RequireStopped();
        if (server == null)
            return;
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择包含 level.dat 的世界文件夹",
            UseDescriptionForTitle = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        if (WorldService.FindWorldRoot(dialog.SelectedPath, server.LevelName) == null)
        {
            MessageBox.Show(this, "这个文件夹里没有 level.dat，不是 Minecraft 世界。", "不是世界存档");
            return;
        }
        if (!ConfirmReplace(server, dialog.SelectedPath))
            return;
        await RunWorld("正在导入", progress =>
        {
            var backup = WorldService.ReplaceFromFolder(server.Directory, server.LevelName, dialog.SelectedPath, progress);
            return string.IsNullOrEmpty(backup) ? "世界已替换。" : "世界已替换。\n旧世界备份在：\n" + backup;
        }, "完成");
    }

    async void RestoreWorld()
    {
        var server = RequireStopped();
        if (server == null)
            return;
        var backups = WorldService.ListBackups(server.Directory);
        if (backups.Count == 0)
        {
            MessageBox.Show(this, "还没有手动备份。备份会放在服务端目录的 backups\\manual。", "从备份恢复");
            return;
        }
        using var dialog = new RestoreForm(backups);
        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrEmpty(dialog.SelectedZip))
            return;
        if (!ConfirmReplace(server, dialog.SelectedZip))
            return;
        var zip = dialog.SelectedZip;
        await RunWorld("正在恢复", progress =>
        {
            var backup = WorldService.ReplaceFromZip(server.Directory, server.LevelName, zip, progress);
            return string.IsNullOrEmpty(backup) ? "已从备份恢复。" : "已从备份恢复。\n恢复前的世界备份在：\n" + backup;
        }, "完成");
    }

    bool ConfirmReplace(ServerSnapshot server, string source)
    {
        var world = Path.Combine(server.Directory, server.LevelName);
        var text = "将要用新存档替换「" + server.Name + "」的世界文件夹：\n" + world + "\n\n来源：\n" + source + "\n\n如果当前世界存在，会先备份到 backups\\manual。";
        return MessageBox.Show(this, text, "替换世界", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
    }

    ServerSnapshot? RequireStopped()
    {
        var server = Selected;
        if (server == null)
            return null;
        var state = Rt(server).State;
        if (state is RunState.Starting or RunState.Running or RunState.Stopping or RunState.Detached)
        {
            MessageBox.Show(this, "请先停止「" + server.Name + "」，再替换世界。", "服务器正在运行");
            return null;
        }
        var world = Path.Combine(server.Directory, server.LevelName);
        if (WorldService.IsWorldLocked(world))
        {
            MessageBox.Show(this, "世界文件仍被占用。请确认服务端已经退出。", "世界被占用");
            return null;
        }
        return server;
    }

    async Task RunWorld(string title, Func<IProgress<string>, string> work, string done)
    {
        if (_busy)
            return;
        var server = Selected;
        if (server == null)
            return;
        _busy = true;
        UseWaitCursor = true;
        UpdateButtons();
        try
        {
            var progress = new Progress<string>(text => _status.Text = title + " " + text);
            var result = await Task.Run(() => work(progress));
            _status.Text = string.IsNullOrWhiteSpace(result) ? done : done + " " + result;
            MessageBox.Show(this, string.IsNullOrWhiteSpace(result) ? done : done + "\n" + result, "完成");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            WorldService.CleanupStage(server.Directory);
            _busy = false;
            UseWaitCursor = false;
            RequestWorldSize(server);
            UpdateDetails();
        }
    }

    void EditConfig()
    {
        var server = Selected;
        if (server == null)
            return;
        var state = Rt(server).State;
        var readOnly = state is RunState.Starting or RunState.Running or RunState.Stopping or RunState.Detached;
        var others = _servers
            .Where(item => !Same(item, server))
            .Select(item => (item.Name, item.Port))
            .ToList();
        using var dialog = new ServerConfigForm(server, readOnly, others);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            RefreshList();
            _status.Text = "已保存「" + server.Name + "」的服务器配置，下次启动生效。";
        }
    }

    void OpenServerFolder()
    {
        var server = Selected;
        if (server != null)
            ShellOpen.Folder(server.Directory);
    }

    void OpenCurrentLog()
    {
        var server = Selected;
        if (server == null)
            return;
        var path = ChooseLog(server);
        if (path == null || !File.Exists(path))
        {
            MessageBox.Show(this, "还没有日志文件。", "日志");
            return;
        }
        ShellOpen.Folder(Path.GetDirectoryName(path)!);
    }

    void ShowCrashes()
    {
        var server = Selected;
        if (server == null || _root == null)
            return;
        _crashAlert = false;
        var dir = PanelPaths.Crash(_root, server.Name);
        Directory.CreateDirectory(dir);
        using var dialog = new CrashForm(dir);
        dialog.ShowDialog(this);
        _seenCrash = NewestCrash(server);
        UpdateButtons();
    }

    void RequestWorldSize(ServerSnapshot server)
    {
        var token = ++_sizeToken;
        var path = Path.Combine(server.Directory, server.LevelName);
        if (!Directory.Exists(path))
        {
            _worldSize = "尚无世界";
            return;
        }
        _worldSize = "正在计算…";
        Task.Run(() =>
        {
            long size = 0;
            var files = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        size += new FileInfo(file).Length;
                        files++;
                    }
                    catch
                    {
                        // 跳过正在被占用的文件。
                    }
                }
            }
            catch
            {
                // 目录读不到时保留“正在计算”之外的空结果。
            }
            var text = RestoreForm.FormatSize(size) + " / " + files + " 个文件";
            try
            {
                BeginInvoke(() =>
                {
                    if (token != _sizeToken)
                        return;
                    _worldSize = text;
                    UpdateDetails();
                });
            }
            catch
            {
                // 窗口已关闭。
            }
        });
    }

    DateTime NewestCrash(ServerSnapshot server)
    {
        if (_root == null)
            return DateTime.MinValue;
        var dir = PanelPaths.Crash(_root, server.Name);
        if (!Directory.Exists(dir))
            return DateTime.MinValue;
        var newest = DateTime.MinValue;
        foreach (var folder in Directory.GetDirectories(dir))
        {
            var time = Directory.GetLastWriteTime(folder);
            if (time > newest)
                newest = time;
        }
        return newest;
    }

    void WatchCrash()
    {
        var server = Selected;
        if (server == null)
            return;
        var newest = NewestCrash(server);
        if (newest <= _seenCrash)
            return;
        _seenCrash = newest;
        _crashAlert = true;
        _status.Text = "已自动保存崩溃日志";
        UpdateButtons();
    }

    RuntimeState Rt(ServerSnapshot server)
    {
        if (!_runtime.TryGetValue(server.Directory, out var runtime))
        {
            runtime = Adopt(server.Directory);
            _runtime[server.Directory] = runtime;
        }
        return runtime;
    }

    static RuntimeState Adopt(string directory)
    {
        var runtime = new RuntimeState();
        var instance = JsonFiles.Read<InstanceState>(PanelPaths.Instance(directory));
        var mark = JsonFiles.Read<ViewerMark>(PanelPaths.ViewerMark(directory));
        if (mark != null && ProcUtil.Alive(mark.Pid, "McPanel"))
        {
            runtime.ViewerPid = mark.Pid;
            if (!string.IsNullOrEmpty(mark.WindowTitle))
                runtime.WindowTitle = mark.WindowTitle;
        }
        if (instance == null)
            return runtime;
        runtime.HostPid = instance.HostPid;
        runtime.JavaPid = instance.JavaPid;
        runtime.WindowTitle = instance.WindowTitle ?? runtime.WindowTitle;
        runtime.Endpoints = instance.Endpoints ?? new List<string>();
        runtime.ExitCode = instance.ExitCode;
        runtime.StartedAt = instance.StartedAt;
        runtime.CrashLog = instance.CrashLog;
        var host = ProcUtil.Alive(instance.HostPid, "McPanel");
        var java = ProcUtil.Alive(instance.JavaPid, "java");
        if (host)
        {
            runtime.State = instance.State switch
            {
                "starting" => RunState.Starting,
                "stopping" => RunState.Stopping,
                "crashed" => RunState.Crashed,
                "exited" => RunState.Stopped,
                _ => RunState.Running,
            };
        }
        else if (java)
            runtime.State = RunState.Detached;
        else if (instance.State == "crashed" || instance.ExitCode is int code && code != 0)
            runtime.State = RunState.Crashed;
        return runtime;
    }

    bool RefreshRuntime(ServerSnapshot server)
    {
        var runtime = Rt(server);
        if (runtime.State == RunState.Stopped && runtime.HostPid == 0 && runtime.HostProcess == null)
            return false;
        var before = runtime.State;
        var instance = JsonFiles.Read<InstanceState>(PanelPaths.Instance(server.Directory));
        var ownHost = runtime.HostProcess != null && !runtime.HostProcess.HasExited;
        if (ownHost && (instance == null || instance.HostPid != runtime.HostPid))
            return false;

        if (instance != null)
        {
            if (instance.JavaPid > 0)
                runtime.JavaPid = instance.JavaPid;
            if (instance.HostPid > 0)
                runtime.HostPid = instance.HostPid;
            if (!string.IsNullOrEmpty(instance.WindowTitle))
                runtime.WindowTitle = instance.WindowTitle;
            if (instance.Endpoints is { Count: > 0 })
                runtime.Endpoints = instance.Endpoints;
            runtime.ExitCode = instance.ExitCode;
            runtime.CrashLog = instance.CrashLog;
        }

        var host = ProcUtil.Alive(runtime.HostPid, "McPanel") || ownHost;
        var java = ProcUtil.Alive(runtime.JavaPid, "java");
        if (host && instance != null && instance.HostPid == runtime.HostPid)
        {
            var mapped = instance.State switch
            {
                "starting" => RunState.Starting,
                "running" => RunState.Running,
                "stopping" => RunState.Stopping,
                "crashed" => RunState.Crashed,
                "exited" => RunState.Stopped,
                _ => RunState.Running,
            };
            if (!(runtime.State == RunState.Stopping && mapped is RunState.Running or RunState.Starting))
                runtime.State = mapped;
        }
        else if (!host && java)
            runtime.State = RunState.Detached;
        else if (!host && !java && runtime.State is RunState.Running or RunState.Starting or RunState.Stopping or RunState.Detached)
            runtime.State = runtime.ExitCode is int code && code != 0 || instance?.State == "crashed" ? RunState.Crashed : RunState.Stopped;

        var mark = JsonFiles.Read<ViewerMark>(PanelPaths.ViewerMark(server.Directory));
        if (mark != null && ProcUtil.Alive(mark.Pid, "McPanel"))
            runtime.ViewerPid = mark.Pid;
        return before != runtime.State;
    }

    ServerSnapshot? Selected =>
        _list.SelectedIndex >= 0 && _list.SelectedIndex < _servers.Count ? _servers[_list.SelectedIndex] : null;

    static bool Same(ServerSnapshot left, ServerSnapshot right) =>
        string.Equals(left.Directory, right.Directory, StringComparison.OrdinalIgnoreCase);

    static string StateText(RunState state) => state switch
    {
        RunState.Running => "运行中",
        RunState.Starting => "启动中",
        RunState.Stopping => "关闭中",
        RunState.Crashed => "已崩溃",
        RunState.Detached => "窗口已关",
        _ => "已停止",
    };

    static Color StateColor(RunState state) => state switch
    {
        RunState.Running => Theme.Green,
        RunState.Starting => Theme.Orange,
        RunState.Stopping => Theme.Yellow,
        RunState.Crashed => Theme.Red,
        RunState.Detached => Theme.Yellow,
        _ => Theme.Muted,
    };

    public RemoteOutcome RemoteStop(string name)
    {
        if (InvokeRequired)
            return (RemoteOutcome)Invoke(() => RemoteStop(name))!;
        var server = FindNamed(name, out var error);
        if (server == null)
            return RemoteOutcome.Fail(error!);
        var state = Rt(server).State;
        if (state is not (RunState.Starting or RunState.Running))
            return RemoteOutcome.Fail(state == RunState.Detached ? "控制进程已关闭，请用强制结束。" : "现在没有在运行。");
        PostCommand(server, "stop");
        Rt(server).State = RunState.Stopping;
        _status.Text = "网页已请求停止「" + server.Name + "」。";
        UpdateDetails();
        return RemoteOutcome.Done("已发送停止。");
    }

    public RemoteOutcome RemoteKill(string name)
    {
        if (InvokeRequired)
            return (RemoteOutcome)Invoke(() => RemoteKill(name))!;
        var server = FindNamed(name, out var error);
        if (server == null)
            return RemoteOutcome.Fail(error!);
        var runtime = Rt(server);
        if (runtime.State is RunState.Stopped or RunState.Crashed)
            return RemoteOutcome.Fail("现在没有在运行。");
        try
        {
            if (runtime.State == RunState.Detached || !ProcUtil.Alive(runtime.HostPid, "McPanel"))
            {
                if (runtime.JavaPid <= 0)
                    return RemoteOutcome.Fail("找不到 Java 进程。");
                using var java = System.Diagnostics.Process.GetProcessById(runtime.JavaPid);
                java.Kill(true);
            }
            else
                PostCommand(server, "__KILL__");
        }
        catch (Exception ex)
        {
            return RemoteOutcome.Fail(ex.Message);
        }
        runtime.State = RunState.Stopping;
        _status.Text = "网页已请求强制结束「" + server.Name + "」。";
        UpdateDetails();
        return RemoteOutcome.Done("正在强制结束。刚才的存档可能没写完。");
    }

    public RemoteOutcome RemoteCommand(string name, string command)
    {
        if (InvokeRequired)
            return (RemoteOutcome)Invoke(() => RemoteCommand(name, command))!;
        command = command.Trim();
        if (command.Length == 0 || command.StartsWith("__", StringComparison.Ordinal) || command.Contains('\n') || command.Contains('\r'))
            return RemoteOutcome.Fail("指令无效。");
        var server = FindNamed(name, out var error);
        if (server == null)
            return RemoteOutcome.Fail(error!);
        var state = Rt(server).State;
        if (state is not (RunState.Starting or RunState.Running or RunState.Stopping))
            return RemoteOutcome.Fail("服务器不在运行，指令送不进去。");
        PostCommand(server, command);
        _status.Text = "网页已发送指令：" + command;
        return RemoteOutcome.Done("已发送：" + command);
    }

    public RemoteOutcome RemoteLog(string name)
    {
        string? path = null;
        string? error = null;
        Invoke(() =>
        {
            var server = FindNamed(name, out error);
            if (server != null)
                path = ChooseLog(server);
        });
        if (error != null)
            return RemoteOutcome.Fail(error);
        if (path == null || !File.Exists(path))
            return RemoteOutcome.Done("还没有日志。", new RemoteTextData(""));
        return RemoteOutcome.Done("日志", new RemoteTextData(ReadTail(path, 80_000)));
    }

    public RemoteOutcome RemoteBackup(string name, bool allowRunning)
    {
        string? dir = null;
        string? level = null;
        Invoke(() =>
        {
            var server = FindNamed(name, out var error);
            if (server == null)
            {
                dir = "!" + error;
                return;
            }
            if (_busy)
            {
                dir = "!主机正在处理世界文件。";
                return;
            }
            var live = Rt(server).State is RunState.Starting or RunState.Running or RunState.Stopping or RunState.Detached;
            if (live && !allowRunning)
            {
                dir = "?服务器正在运行，现在备份可能不完整。仍然备份吗？";
                return;
            }
            _busy = true;
            UpdateButtons();
            dir = server.Directory;
            level = server.LevelName;
        });
        if (dir == null)
            return RemoteOutcome.Fail("没有找到服务端。");
        if (dir.StartsWith('!'))
            return RemoteOutcome.Fail(dir[1..]);
        if (dir.StartsWith('?'))
            return RemoteOutcome.Ask(dir[1..]);
        try
        {
            var path = WorldService.Backup(dir, level!, null);
            Invoke(() => _status.Text = "网页已备份「" + name + "」。");
            return RemoteOutcome.Done("备份完成\n" + path);
        }
        catch (Exception ex)
        {
            return RemoteOutcome.Fail(ex.Message);
        }
        finally
        {
            Invoke(() =>
            {
                _busy = false;
                UpdateButtons();
            });
        }
    }

    public RemoteOutcome RemoteCreateWorld(string name, string world, string seed)
    {
        if (InvokeRequired)
            return (RemoteOutcome)Invoke(() => RemoteCreateWorld(name, world, seed))!;
        var server = FindNamed(name, out var error);
        if (server == null)
            return RemoteOutcome.Fail(error!);
        if (Rt(server).State is RunState.Starting or RunState.Running or RunState.Stopping or RunState.Detached)
            return RemoteOutcome.Fail("请先停止服务器，再创建新世界。");
        if (WorldService.IsWorldLocked(Path.Combine(server.Directory, server.LevelName)))
            return RemoteOutcome.Fail("当前世界文件仍被占用。");
        try
        {
            var created = WorldService.CreateFreshWorld(server.Directory, server.LevelName, world, seed);
            RefreshList();
            _status.Text = "网页已创建新世界「" + world.Trim() + "」。";
            return RemoteOutcome.Done("已切换到「" + world.Trim() + "」。旧世界仍保留。\n" + created);
        }
        catch (Exception ex)
        {
            return RemoteOutcome.Fail(ex.Message);
        }
    }

    public RemoteOutcome RemoteBackups(string name)
    {
        var server = FindNamedOnUi(name, out var error);
        if (server == null)
            return RemoteOutcome.Fail(error!);
        var files = WorldService.ListBackups(server.Directory)
            .Select(file => new RemoteFileItem(file.Name, file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"), RestoreForm.FormatSize(file.Length)))
            .ToList();
        return RemoteOutcome.Done(files.Count == 0 ? "还没有手动备份。" : "共 " + files.Count + " 份备份。", files);
    }

    public RemoteOutcome RemoteRestore(string name, string file)
    {
        string? dir = null;
        string? level = null;
        string? zip = null;
        Invoke(() =>
        {
            var server = FindNamed(name, out var error);
            if (server == null)
            {
                dir = "!" + error;
                return;
            }
            if (_busy)
            {
                dir = "!主机正在处理世界文件。";
                return;
            }
            if (Rt(server).State is RunState.Starting or RunState.Running or RunState.Stopping or RunState.Detached)
            {
                dir = "!请先停止服务器，再恢复备份。";
                return;
            }
            if (WorldService.IsWorldLocked(Path.Combine(server.Directory, server.LevelName)))
            {
                dir = "!世界文件仍被占用。";
                return;
            }
            var safe = Path.GetFileName(file);
            var full = Path.Combine(WorldService.ManualBackupDir(server.Directory), safe);
            if (!File.Exists(full))
            {
                dir = "!没有这份备份。";
                return;
            }
            _busy = true;
            UpdateButtons();
            dir = server.Directory;
            level = server.LevelName;
            zip = full;
        });
        if (dir == null || dir.StartsWith('!'))
            return RemoteOutcome.Fail(dir == null ? "没有找到服务端。" : dir[1..]);
        try
        {
            var backup = WorldService.ReplaceFromZip(dir, level!, zip!, null);
            Invoke(() =>
            {
                _status.Text = "网页已从备份恢复「" + name + "」。";
                RequestWorldSize(FindNamed(name, out _) ?? Selected!);
            });
            return RemoteOutcome.Done(string.IsNullOrEmpty(backup) ? "已从备份恢复。" : "已从备份恢复。\n恢复前的世界备份在：\n" + backup);
        }
        catch (Exception ex)
        {
            return RemoteOutcome.Fail(ex.Message);
        }
        finally
        {
            Invoke(() =>
            {
                WorldService.CleanupStage(dir);
                _busy = false;
                UpdateButtons();
            });
        }
    }

    public RemoteOutcome RemoteConfig(string name)
    {
        if (InvokeRequired)
            return (RemoteOutcome)Invoke(() => RemoteConfig(name))!;
        var server = FindNamed(name, out var error);
        if (server == null)
            return RemoteOutcome.Fail(error!);
        var readOnly = Rt(server).State is RunState.Starting or RunState.Running or RunState.Stopping or RunState.Detached;
        var document = ServerPropertiesDocument.Load(Path.Combine(server.Directory, "server.properties"));
        var items = new List<RemoteConfigItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in SettingCatalog.All)
        {
            seen.Add(spec.Key);
            var inFile = document.Has(spec.Key);
            items.Add(RemoteConfigItem.From(spec, inFile ? document.Get(spec.Key) ?? "" : spec.DefaultValue));
        }
        foreach (var key in document.Keys)
        {
            if (seen.Contains(key))
                continue;
            items.Add(new RemoteConfigItem
            {
                Key = key,
                Label = key,
                Description = "这项不在常见列表里。",
                Group = "其他",
                Kind = "text",
                Value = document.Get(key) ?? "",
            });
        }
        var notice = readOnly ? "服务器正在运行，现在只能查看。停服后再改。" : "大部分选项要下次启动才生效。";
        return RemoteOutcome.Done(notice, new RemoteConfigData(readOnly, items));
    }

    public RemoteOutcome RemoteSaveConfig(string name, IReadOnlyList<RemoteConfigChange> changes, bool confirmPort, bool confirmLevel)
    {
        if (InvokeRequired)
            return (RemoteOutcome)Invoke(() => RemoteSaveConfig(name, changes, confirmPort, confirmLevel))!;
        var server = FindNamed(name, out var error);
        if (server == null)
            return RemoteOutcome.Fail(error!);
        if (Rt(server).State is RunState.Starting or RunState.Running or RunState.Stopping or RunState.Detached)
            return RemoteOutcome.Fail("服务器正在运行，不能保存配置。");
        if (changes == null || changes.Count == 0)
            return RemoteOutcome.Fail("没有需要保存的修改。");
        var document = ServerPropertiesDocument.Load(Path.Combine(server.Directory, "server.properties"));
        var errors = new List<string>();
        foreach (var change in changes)
        {
            var spec = SettingCatalog.Find(change.Key);
            var value = spec == null || !spec.Trim ? change.Value : change.Value.Trim();
            var problem = spec?.Validate(value);
            if (problem != null)
                errors.Add(problem);
            if (spec == null && string.IsNullOrWhiteSpace(change.Key))
                errors.Add("配置项名称无效。");
        }
        if (errors.Count > 0)
            return RemoteOutcome.Fail(string.Join("\n", errors));
        string ValueOf(string key)
        {
            var change = changes.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (change != null)
            {
                var spec = SettingCatalog.Find(key);
                return spec == null || !spec.Trim ? change.Value : change.Value.Trim();
            }
            return document.Get(key) ?? SettingCatalog.Find(key)?.DefaultValue ?? "";
        }
        var portText = ValueOf("server-port");
        var oldPort = document.Get("server-port") ?? server.Port.ToString();
        if (portText != oldPort && int.TryParse(portText, out var portNumber))
        {
            var clash = _servers.Where(item => !Same(item, server) && item.Port == portNumber).Select(item => item.Name).ToList();
            if (clash.Count > 0 && !confirmPort)
                return RemoteOutcome.Ask("端口 " + portNumber + " 也被这些服务端使用：" + string.Join("、", clash) + "。仍然保存吗？");
        }
        var level = ValueOf("level-name");
        var oldLevel = document.Get("level-name") ?? server.LevelName;
        if (!level.Equals(oldLevel, StringComparison.Ordinal) && !confirmLevel)
            return RemoteOutcome.Ask("世界文件夹将从「" + oldLevel + "」改为「" + level + "」。这不会重命名现有存档。仍然保存吗？");
        if (ValueOf("enable-rcon") == "true" && string.IsNullOrEmpty(ValueOf("rcon.password")))
            return RemoteOutcome.Fail("打开了远程控制，但密码是空的。");
        foreach (var change in changes)
        {
            var spec = SettingCatalog.Find(change.Key);
            var value = spec == null || !spec.Trim ? change.Value : change.Value.Trim();
            document.Set(change.Key, value);
        }
        document.Save(Path.Combine(server.Directory, "server.properties"));
        RefreshList();
        _status.Text = "网页已保存「" + server.Name + "」的配置，下次启动生效。";
        return RemoteOutcome.Done("已保存，下次启动生效。");
    }

    public RemoteOutcome RemoteCrashes(string name)
    {
        if (_root == null)
            return RemoteOutcome.Fail("还没有服务端目录。");
        var server = FindNamedOnUi(name, out var error);
        if (server == null)
            return RemoteOutcome.Fail(error!);
        var dir = PanelPaths.Crash(_root, server.Name);
        if (!Directory.Exists(dir))
            return RemoteOutcome.Done("还没有崩溃记录。", new List<RemoteFileItem>());
        var files = Directory.GetDirectories(dir)
            .OrderByDescending(Directory.GetLastWriteTime)
            .Select(path => new RemoteFileItem(Path.GetFileName(path), Directory.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss"), ""))
            .ToList();
        return RemoteOutcome.Done(files.Count == 0 ? "还没有崩溃记录。" : "共 " + files.Count + " 条。", files);
    }

    public RemoteOutcome RemoteCrash(string name, string id)
    {
        if (_root == null)
            return RemoteOutcome.Fail("还没有服务端目录。");
        var server = FindNamedOnUi(name, out var error);
        if (server == null)
            return RemoteOutcome.Fail(error!);
        var safe = Path.GetFileName(id);
        if (string.IsNullOrEmpty(safe) || safe != id.Trim())
            return RemoteOutcome.Fail("没有这条崩溃记录。");
        var summary = Path.Combine(PanelPaths.Crash(_root, server.Name), safe, "summary.log");
        if (!File.Exists(summary))
            return RemoteOutcome.Fail("没有这条崩溃记录。");
        return RemoteOutcome.Done(safe, new RemoteTextData(ReadTail(summary, 80_000)));
    }

    ServerSnapshot? FindNamedOnUi(string name, out string? error)
    {
        if (!InvokeRequired)
            return FindNamed(name, out error);
        string? copy = null;
        var server = (ServerSnapshot?)Invoke(() =>
        {
            var found = FindNamed(name, out copy);
            return found;
        });
        error = copy;
        return server;
    }

    ServerSnapshot? FindNamed(string name, out string? error)
    {
        name = name.Trim();
        var matches = _servers.Where(item => item.Name == name).ToList();
        if (matches.Count == 0)
        {
            error = "找不到这个服务端。";
            return null;
        }
        if (matches.Count > 1)
        {
            error = "有多个同名服务端，请在主机上处理。";
            return null;
        }
        error = null;
        return matches[0];
    }

    static string ReadTail(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var start = stream.Length > maxBytes ? stream.Length - maxBytes : 0;
        stream.Position = start;
        var buffer = new byte[stream.Length - start];
        var filled = 0;
        while (filled < buffer.Length)
        {
            var count = stream.Read(buffer, filled, buffer.Length - filled);
            if (count <= 0)
                break;
            filled += count;
        }
        var text = Encoding.UTF8.GetString(buffer, 0, filled);
        if (start > 0)
        {
            var newline = text.IndexOf('\n');
            if (newline >= 0)
                text = text[(newline + 1)..];
        }
        return text;
    }
}

internal sealed class RemoteTextData
{
    public RemoteTextData(string text) => Text = text;
    public string Text { get; set; }
}

internal sealed class RemoteFileItem
{
    public RemoteFileItem(string name, string time, string size)
    {
        Name = name;
        Time = time;
        Size = size;
    }

    public string Name { get; set; }
    public string Time { get; set; }
    public string Size { get; set; }
}

internal sealed class RemoteConfigItem
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    public string Group { get; set; } = "";
    public string Kind { get; set; } = "text";
    public string Value { get; set; } = "";
    public List<RemoteConfigChoice> Choices { get; set; } = new();

    public static RemoteConfigItem From(SettingSpec spec, string value) => new()
    {
        Key = spec.Key,
        Label = spec.Label,
        Description = spec.Description,
        Group = spec.Group,
        Kind = spec.Kind switch
        {
            SettingKind.Bool => "bool",
            SettingKind.Choice => "choice",
            SettingKind.Integer => "integer",
            _ => "text",
        },
        Value = value,
        Choices = spec.Choices?.Select(choice => new RemoteConfigChoice(choice.Label, choice.Value)).ToList() ?? new List<RemoteConfigChoice>(),
    };
}

internal sealed class RemoteConfigChoice
{
    public RemoteConfigChoice(string label, string value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; set; }
    public string Value { get; set; }
}

internal sealed class RemoteConfigData
{
    public RemoteConfigData(bool readOnly, List<RemoteConfigItem> items)
    {
        ReadOnly = readOnly;
        Items = items;
    }

    public bool ReadOnly { get; set; }
    public List<RemoteConfigItem> Items { get; set; }
}
