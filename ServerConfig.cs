using System.Text;

namespace McPanel;

internal enum SettingKind
{
    Text,
    Bool,
    Choice,
    Integer,
}

internal sealed class SettingChoice
{
    public SettingChoice(string label, string value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }
    public string Value { get; }
    public override string ToString() => Label;
}

internal sealed class SettingSpec
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Description { get; init; }
    public string Group { get; init; } = "其他";
    public int Order { get; init; }
    public SettingKind Kind { get; init; }
    public string DefaultValue { get; init; } = "";
    public int? Min { get; init; }
    public int? Max { get; init; }
    public bool StrictChoice { get; init; } = true;
    public bool Trim { get; init; } = true;
    public SettingChoice[]? Choices { get; init; }

    public string? Validate(string value)
    {
        switch (Kind)
        {
            case SettingKind.Integer:
                if (!int.TryParse(value, out var number))
                    return Label + " 需要填写整数。";
                if (Min != null && number < Min)
                    return Label + " 不能小于 " + Min + "。";
                if (Max != null && number > Max)
                    return Label + " 不能大于 " + Max + "。";
                return null;
            case SettingKind.Bool:
                if (value is not ("true" or "false"))
                    return Label + " 只能是开或关。";
                return null;
            case SettingKind.Choice when StrictChoice && Choices != null && Choices.All(choice => choice.Value != value):
                return Label + " 不在可选范围内。";
            default:
                if (Key == "level-name" && !WorldService.IsValidLevelName(value))
                    return "世界文件夹名无效，不能包含 \\ / : * ? \" < > |，也不能是 . 或 ..。";
                return null;
        }
    }
}

internal static class SettingCatalog
{
    static readonly SettingChoice[] BoolChoices =
    [
        new("开", "true"),
        new("关", "false"),
    ];

    static readonly SettingChoice[] Difficulty =
    [
        new("和平", "peaceful"),
        new("简单", "easy"),
        new("普通", "normal"),
        new("困难", "hard"),
    ];

    static readonly SettingChoice[] GameMode =
    [
        new("生存", "survival"),
        new("创造", "creative"),
        new("冒险", "adventure"),
        new("旁观", "spectator"),
    ];

    static readonly SettingChoice[] LevelType =
    [
        new("普通", "minecraft:normal"),
        new("超平坦", "minecraft:flat"),
        new("大型生物群系", "minecraft:large_biomes"),
        new("放大化", "minecraft:amplified"),
    ];

    public static IReadOnlyList<SettingSpec> All { get; } = Build();

    public static SettingSpec? Find(string key) =>
        All.FirstOrDefault(spec => spec.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    static List<SettingSpec> Build()
    {
        var list = new List<SettingSpec>();
        void Add(string key, string label, string description, string group, int order, SettingKind kind, string defaultValue, int? min = null, int? max = null, SettingChoice[]? choices = null, bool strict = true, bool trim = true)
        {
            list.Add(new SettingSpec
            {
                Key = key,
                Label = label,
                Description = description,
                Group = group,
                Order = order,
                Kind = kind,
                DefaultValue = defaultValue,
                Min = min,
                Max = max,
                Choices = kind == SettingKind.Bool ? BoolChoices : choices,
                StrictChoice = strict,
                Trim = trim,
            });
        }

        Add("server-port", "服务器端口", "玩家连接时使用的端口。三个服不要同时开同一个端口。", "常用", 10, SettingKind.Integer, "25565", 1, 65535);
        Add("server-ip", "绑定地址", "留空表示监听本机全部网卡。一般不用填。", "常用", 20, SettingKind.Text, "");
        Add("motd", "服务器简介", "服务器列表里显示的文字。", "常用", 30, SettingKind.Text, "A Minecraft Server", trim: false);
        Add("max-players", "最大玩家数", "同时在线人数上限。", "常用", 40, SettingKind.Integer, "20", 1, 99999);
        Add("online-mode", "正版验证", "开启后只有正版账号能进。关了才能进离线或盗版客户端。", "常用", 50, SettingKind.Bool, "true");
        Add("white-list", "白名单", "开启后只有白名单里的玩家能进。", "常用", 60, SettingKind.Bool, "false");
        Add("enforce-whitelist", "强制白名单", "开启后，从白名单移除的玩家会被立刻踢出。", "常用", 70, SettingKind.Bool, "false");
        Add("pvp", "玩家伤害", "关闭后玩家不能互相攻击。", "常用", 80, SettingKind.Bool, "true");
        Add("difficulty", "难度", "和平、简单、普通或困难。", "常用", 90, SettingKind.Choice, "easy", choices: Difficulty);
        Add("gamemode", "默认模式", "新玩家第一次进入时的模式。", "常用", 100, SettingKind.Choice, "survival", choices: GameMode);
        Add("view-distance", "视距", "玩家能看到的区块距离。整合包服可以适当调低。", "常用", 110, SettingKind.Integer, "10", 2, 64);
        Add("simulation-distance", "模拟距离", "实体和作物实际运转的距离，通常小于或等于视距。", "常用", 120, SettingKind.Integer, "10", 2, 64);
        Add("allow-flight", "允许飞行", "生存里用模组或鞘翅飞的时候建议打开，否则可能被踢。", "常用", 130, SettingKind.Bool, "false");
        Add("hardcore", "极限模式", "死亡后变为旁观。已经生成的世界再改这项，效果要看服务端版本。", "常用", 140, SettingKind.Bool, "false");

        Add("level-name", "世界文件夹", "要加载的世界目录名，对应服务端文件夹里面的那个存档。改它不会改名，只是下次启动换一个文件夹。", "世界", 10, SettingKind.Text, "world");
        Add("level-seed", "世界种子", "只对新建世界有效。留空则随机。", "世界", 20, SettingKind.Text, "");
        Add("level-type", "世界类型", "可以选预设，也可以自己填模组提供的类型。", "世界", 30, SettingKind.Choice, "minecraft:normal", choices: LevelType, strict: false);
        Add("generate-structures", "生成结构", "村庄、地牢这类结构。只影响新生成的区块。", "世界", 40, SettingKind.Bool, "true");
        Add("allow-nether", "允许下界", "关闭后不能去下界。", "世界", 50, SettingKind.Bool, "true");
        Add("spawn-protection", "出生点保护", "出生点周围多少格内，非管理员不能破坏。0 表示不保护。", "世界", 60, SettingKind.Integer, "16", 0, 999999);
        Add("max-world-size", "世界边界", "世界半径的上限，单位是方块。", "世界", 70, SettingKind.Integer, "29999984", 1, 29999984);
        Add("generator-settings", "生成器参数", "超平坦等世界类型的额外参数，一般留空或 {}。", "世界", 80, SettingKind.Text, "{}", trim: false);

        Add("spawn-monsters", "生成怪物", "关闭后不再自然刷怪。", "游戏", 10, SettingKind.Bool, "true");
        Add("spawn-animals", "生成动物", "关闭后不再自然刷动物。", "游戏", 20, SettingKind.Bool, "true");
        Add("spawn-npcs", "生成村民", "关闭后不再自然刷村民。", "游戏", 30, SettingKind.Bool, "true");
        Add("force-gamemode", "强制游戏模式", "每次进入都改成上面的默认模式。", "游戏", 40, SettingKind.Bool, "false");
        Add("enable-command-block", "命令方块", "关闭后命令方块不会执行。", "游戏", 50, SettingKind.Bool, "false");
        Add("op-permission-level", "管理员权限等级", "1 到 4。4 才能使用大部分管理命令。", "游戏", 60, SettingKind.Integer, "4", 1, 4);
        Add("function-permission-level", "函数权限等级", "数据包函数能用到的权限。", "游戏", 70, SettingKind.Integer, "2", 1, 4);
        Add("player-idle-timeout", "闲置踢出", "多少分钟没有任何操作就踢出。0 表示不踢。", "游戏", 80, SettingKind.Integer, "0", 0, 100000);
        Add("hide-online-players", "隐藏在线玩家", "服务器列表里不显示谁在线。", "游戏", 90, SettingKind.Bool, "false");
        Add("enforce-secure-profile", "要求安全配置", "要求玩家完成 Mojang 的聊天签名。离线服一般关掉。", "游戏", 100, SettingKind.Bool, "true");
        Add("entity-broadcast-range-percentage", "实体同步范围", "100 是正常范围，调低可以减轻卡顿。", "游戏", 110, SettingKind.Integer, "100", 10, 1000);

        Add("enable-status", "允许查看状态", "关闭后，服务器列表可能显示不出来。", "网络", 10, SettingKind.Bool, "true");
        Add("enable-query", "启用 Query", "给外部统计工具查询在线人数。", "网络", 20, SettingKind.Bool, "false");
        Add("query.port", "Query 端口", "启用 Query 时使用的端口。", "网络", 30, SettingKind.Integer, "25565", 1, 65535);
        Add("enable-rcon", "启用远程控制", "打开后可以用 RCON 从别的机器发命令。密码不要留空。", "网络", 40, SettingKind.Bool, "false");
        Add("rcon.port", "远程控制端口", "RCON 使用的端口，不要和游戏端口相同。", "网络", 50, SettingKind.Integer, "25575", 1, 65535);
        Add("rcon.password", "远程控制密码", "启用远程控制时必填。", "网络", 60, SettingKind.Text, "", trim: false);
        Add("broadcast-console-to-ops", "控制台发给管理员", "控制台输出同时发给在线管理员。", "网络", 70, SettingKind.Bool, "true");
        Add("broadcast-rcon-to-ops", "远程控制发给管理员", "RCON 执行结果同时发给在线管理员。", "网络", 80, SettingKind.Bool, "true");
        Add("network-compression-threshold", "网络压缩阈值", "数据包大于这个字节数才压缩。-1 表示不压缩。", "网络", 90, SettingKind.Integer, "256", -1, 65535);
        Add("rate-limit", "连接速率限制", "限制玩家发包速度。0 表示不限制。", "网络", 100, SettingKind.Integer, "0", 0, 100000);
        Add("prevent-proxy-connections", "阻止代理连接", "拒绝通过转发代理进来的连接。", "网络", 110, SettingKind.Bool, "false");
        Add("use-native-transport", "使用系统网络", "Windows 上一般保持开启。", "网络", 120, SettingKind.Bool, "true");
        Add("max-tick-time", "卡死超时", "一个 tick 超过这么多毫秒就判定崩溃。0 表示不检测。", "网络", 130, SettingKind.Integer, "60000", 0, int.MaxValue);

        Add("require-resource-pack", "强制资源包", "玩家拒绝资源包就不能进服。", "资源包", 10, SettingKind.Bool, "false");
        Add("resource-pack", "资源包地址", "资源包的下载链接。", "资源包", 20, SettingKind.Text, "", trim: false);
        Add("resource-pack-sha1", "资源包校验", "资源包文件的 SHA1，留空则不校验。", "资源包", 30, SettingKind.Text, "");
        Add("resource-pack-prompt", "资源包提示", "提示玩家是否下载资源包的文字。", "资源包", 40, SettingKind.Text, "", trim: false);

        Add("sync-chunk-writes", "同步保存区块", "开启更安全，关闭可能快一点。", "高级", 10, SettingKind.Bool, "true");
        Add("enable-jmx-monitoring", "JMX 监控", "给外部监控程序用，平时保持关闭。", "高级", 20, SettingKind.Bool, "false");
        Add("max-chained-neighbor-updates", "连锁更新上限", "限制红石、流体一次连锁更新的次数。", "高级", 30, SettingKind.Integer, "1000000", 0, int.MaxValue);
        Add("initial-enabled-packs", "初始启用数据包", "新世界默认启用的数据包，一般是 vanilla。", "高级", 40, SettingKind.Text, "vanilla", trim: false);
        Add("initial-disabled-packs", "初始禁用数据包", "新世界默认禁用的数据包。", "高级", 50, SettingKind.Text, "", trim: false);
        Add("text-filtering-config", "文本过滤配置", "聊天过滤配置，一般留空。", "高级", 60, SettingKind.Text, "", trim: false);
        return list;
    }
}

internal sealed class ServerPropertiesDocument
{
    readonly List<Row> _rows = new();
    string _newline = "\r\n";

    public static ServerPropertiesDocument Load(string path)
    {
        var document = new ServerPropertiesDocument();
        if (!File.Exists(path))
            return document;
        var text = Decode(File.ReadAllBytes(path));
        document._newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                document._rows.Add(new Row { Raw = line });
                continue;
            }
            var split = line.IndexOf('=');
            if (split <= 0)
            {
                document._rows.Add(new Row { Raw = line });
                continue;
            }
            var key = line[..split].Trim();
            var value = Unescape(line[(split + 1)..]);
            document._rows.Add(new Row { Key = key, Raw = line, Original = value, Value = value, IsProperty = true });
        }
        return document;
    }

    public IEnumerable<string> Keys => _rows.Where(row => row.IsProperty).Select(row => row.Key);

    public bool Has(string key) => Find(key) != null;

    public string? Get(string key) => Find(key)?.Value;

    public void Set(string key, string value)
    {
        var row = Find(key);
        if (row == null)
        {
            _rows.Add(new Row { Key = key, IsProperty = true, Original = null, Value = value, Raw = "" });
            return;
        }
        row.Value = value;
    }

    public void Save(string path)
    {
        var sb = new StringBuilder();
        foreach (var row in _rows)
        {
            if (!row.IsProperty)
                sb.Append(row.Raw);
            else if (row.Original != null && row.Value == row.Original)
                sb.Append(row.Raw);
            else
                sb.Append(row.Key).Append('=').Append(Escape(row.Value));
            sb.Append(_newline);
        }
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }

    Row? Find(string key) =>
        _rows.FirstOrDefault(row => row.IsProperty && row.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public static string Unescape(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                sb.Append(value[i]);
                continue;
            }
            var next = value[++i];
            switch (next)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'f': sb.Append('\f'); break;
                case 'u' when i + 4 < value.Length && IsHex(value, i + 1):
                    sb.Append((char)Convert.ToInt32(value.Substring(i + 1, 4), 16));
                    i += 4;
                    break;
                default:
                    sb.Append(next);
                    break;
            }
        }
        return sb.ToString();
    }

    public static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case ':': sb.Append("\\:"); break;
                case '=': sb.Append("\\="); break;
                case '#': sb.Append("\\#"); break;
                case '!': sb.Append("\\!"); break;
                default:
                    if (ch < 32 || ch > 126)
                        sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else
                        sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    static bool IsHex(string text, int start)
    {
        for (var i = 0; i < 4; i++)
        {
            var ch = text[start + i];
            var hex = ch is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!hex)
                return false;
        }
        return true;
    }

    static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(936).GetString(bytes);
        }
    }

    sealed class Row
    {
        public bool IsProperty;
        public string Raw = "";
        public string Key = "";
        public string? Original;
        public string Value = "";
    }
}

internal sealed class ConfigEntry
{
    public required SettingSpec Spec { get; init; }
    public string Baseline { get; set; } = "";
    public string Value { get; set; } = "";
    public bool InFile { get; init; }
    public bool Dirty => Value != Baseline;
}

internal sealed class ServerConfigForm : Form
{
    readonly ServerSnapshot _server;
    readonly bool _readOnly;
    readonly IReadOnlyList<(string Name, int Port)> _otherPorts;
    readonly ServerPropertiesDocument _document;
    readonly List<ConfigEntry> _entries = new();
    readonly ListView _list = new();
    readonly TextBox _search = new();
    readonly Label _description = new();
    readonly TextBox _text = new();
    readonly ComboBox _combo = new();
    readonly Button _save;
    ConfigEntry? _current;
    bool _loadingEditor;

    public ServerConfigForm(ServerSnapshot server, bool readOnly, IReadOnlyList<(string Name, int Port)> otherPorts)
    {
        _server = server;
        _readOnly = readOnly;
        _otherPorts = otherPorts;
        _document = ServerPropertiesDocument.Load(Path.Combine(server.Directory, "server.properties"));
        BuildEntries();

        Theme.Style(this);
        Text = "服务器配置 — " + server.Name;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MinimumSize = new Size(760, 560);
        ClientSize = new Size(920, 680);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, readOnly ? 48 : 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        var banner = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = readOnly ? Theme.Yellow : Theme.Muted,
            Text = readOnly
                ? "服务器正在运行，现在只能查看。停服后再改。关服时游戏会用内存里的配置覆盖这个文件。"
                : "改完点保存。大部分选项要下次启动才生效。",
        };
        _search.Dock = DockStyle.Fill;
        _search.BorderStyle = BorderStyle.FixedSingle;
        _search.BackColor = Theme.Input;
        _search.ForeColor = Theme.Text;
        _search.PlaceholderText = "搜索名称、键或当前值";
        _search.TextChanged += (_, _) => FillList();

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HideSelection = false;
        _list.MultiSelect = false;
        _list.BorderStyle = BorderStyle.None;
        _list.BackColor = Theme.Input;
        _list.ForeColor = Theme.Text;
        _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _list.Columns.Add("项目", 210);
        _list.Columns.Add("当前值", 280);
        _list.Columns.Add("键", 220);
        _list.Columns.Add("分组", 80);
        _list.SelectedIndexChanged += (_, _) => SelectCurrent();

        var editor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        _description.Dock = DockStyle.Fill;
        _description.ForeColor = Theme.Muted;
        _text.Dock = DockStyle.Fill;
        _text.BorderStyle = BorderStyle.FixedSingle;
        _text.BackColor = Theme.Input;
        _text.ForeColor = Theme.Text;
        _text.TextChanged += (_, _) => PushEditor();
        _combo.Dock = DockStyle.Fill;
        _combo.FlatStyle = FlatStyle.Flat;
        _combo.BackColor = Theme.Input;
        _combo.ForeColor = Theme.Text;
        _combo.DropDownStyle = ComboBoxStyle.DropDownList;
        _combo.SelectedIndexChanged += (_, _) => PushEditor();
        _combo.TextChanged += (_, _) => PushEditor();
        var editorHost = new Panel { Dock = DockStyle.Fill };
        editorHost.Controls.Add(_text);
        editorHost.Controls.Add(_combo);
        editor.Controls.Add(_description, 0, 0);
        editor.Controls.Add(editorHost, 0, 1);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        _save = Theme.MakeButton("保存", true);
        var close = Theme.MakeButton("关闭");
        _save.Enabled = !readOnly;
        _save.Click += (_, _) => Save();
        close.Click += (_, _) => Close();
        buttons.Controls.Add(close);
        buttons.Controls.Add(_save);

        layout.Controls.Add(banner, 0, 0);
        layout.Controls.Add(_search, 0, 1);
        layout.Controls.Add(_list, 0, 2);
        layout.Controls.Add(editor, 0, 3);
        layout.Controls.Add(buttons, 0, 4);
        Controls.Add(layout);
        FillList();
        if (_list.Items.Count > 0)
            _list.Items[0].Selected = true;
    }

    void BuildEntries()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in SettingCatalog.All)
        {
            seen.Add(spec.Key);
            var inFile = _document.Has(spec.Key);
            var baseline = inFile ? _document.Get(spec.Key) ?? "" : spec.DefaultValue;
            _entries.Add(new ConfigEntry { Spec = spec, Baseline = baseline, Value = baseline, InFile = inFile });
        }
        foreach (var key in _document.Keys)
        {
            if (seen.Contains(key))
                continue;
            var spec = new SettingSpec
            {
                Key = key,
                Label = key,
                Description = "这项不在常见列表里，按文件中的原样保留。",
                Group = "其他",
                Order = 1000,
                Kind = SettingKind.Text,
                Trim = false,
            };
            var value = _document.Get(key) ?? "";
            _entries.Add(new ConfigEntry { Spec = spec, Baseline = value, Value = value, InFile = true });
        }
    }

    void FillList()
    {
        var selected = _current?.Spec.Key;
        var query = _search.Text.Trim();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var entry in _entries.OrderBy(item => GroupRank(item.Spec.Group)).ThenBy(item => item.Spec.Order).ThenBy(item => item.Spec.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!Matches(entry, query))
                continue;
            var item = new ListViewItem(entry.Dirty ? entry.Spec.Label + " *" : entry.Spec.Label);
            item.SubItems.Add(Display(entry));
            item.SubItems.Add(entry.Spec.Key);
            item.SubItems.Add(entry.Spec.Group);
            item.Tag = entry;
            item.ForeColor = entry.Dirty ? Theme.Yellow : Theme.Text;
            _list.Items.Add(item);
            if (selected != null && entry.Spec.Key.Equals(selected, StringComparison.OrdinalIgnoreCase))
                item.Selected = true;
        }
        _list.EndUpdate();
        if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0)
            _list.Items[0].Selected = true;
    }

    static int GroupRank(string group) => group switch
    {
        "常用" => 0,
        "世界" => 1,
        "游戏" => 2,
        "网络" => 3,
        "资源包" => 4,
        "高级" => 5,
        _ => 6,
    };

    static bool Matches(ConfigEntry entry, string query)
    {
        if (query.Length == 0)
            return true;
        return entry.Spec.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || entry.Spec.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Spec.Group.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || Display(entry).Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    static string Display(ConfigEntry entry)
    {
        if (entry.Spec.Kind == SettingKind.Bool)
            return entry.Value == "true" ? "开" : entry.Value == "false" ? "关" : entry.Value;
        var choice = entry.Spec.Choices?.FirstOrDefault(item => item.Value == entry.Value);
        return choice == null ? entry.Value : choice.Label + " (" + entry.Value + ")";
    }

    void SelectCurrent()
    {
        if (_list.SelectedItems.Count == 0)
            return;
        if (_list.SelectedItems[0].Tag is not ConfigEntry entry || ReferenceEquals(entry, _current))
            return;
        _current = entry;
        _loadingEditor = true;
        var missing = entry.InFile ? "" : "文件里还没有这项，改动并保存后才会写进去。\n";
        _description.Text = entry.Spec.Group + "  ·  " + entry.Spec.Key + "\n" + missing + entry.Spec.Description;
        if (entry.Spec.Kind is SettingKind.Bool or SettingKind.Choice && entry.Spec.Choices != null)
        {
            _combo.Visible = true;
            _text.Visible = false;
            _combo.Items.Clear();
            _combo.DropDownStyle = entry.Spec.StrictChoice ? ComboBoxStyle.DropDownList : ComboBoxStyle.DropDown;
            foreach (var choice in entry.Spec.Choices)
                _combo.Items.Add(choice);
            var selected = entry.Spec.Choices.FirstOrDefault(choice => choice.Value == entry.Value);
            if (selected != null)
                _combo.SelectedItem = selected;
            else
                _combo.Text = entry.Value;
        }
        else
        {
            _combo.Visible = false;
            _text.Visible = true;
            _text.Text = entry.Value;
        }
        _text.ReadOnly = _readOnly;
        _combo.Enabled = !_readOnly;
        _loadingEditor = false;
    }

    void PushEditor()
    {
        if (_loadingEditor || _current == null || _readOnly)
            return;
        var value = ReadEditor();
        if (_current.Spec.Trim)
            value = value.Trim();
        if (value == _current.Value)
            return;
        _current.Value = value;
        foreach (ListViewItem item in _list.Items)
        {
            if (!ReferenceEquals(item.Tag, _current))
                continue;
            item.Text = _current.Dirty ? _current.Spec.Label + " *" : _current.Spec.Label;
            item.SubItems[1].Text = Display(_current);
            item.ForeColor = _current.Dirty ? Theme.Yellow : Theme.Text;
            break;
        }
    }

    string ReadEditor()
    {
        if (_combo.Visible)
        {
            if (_combo.SelectedItem is SettingChoice choice)
                return choice.Value;
            return _combo.Text;
        }
        return _text.Text;
    }

    void Save()
    {
        if (_readOnly)
            return;
        PushEditor();
        var errors = new List<string>();
        foreach (var entry in _entries.Where(item => item.Dirty))
        {
            var error = entry.Spec.Validate(entry.Value);
            if (error != null)
                errors.Add(error);
        }
        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", errors), "还有项没填对");
            return;
        }
        var dirty = _entries.Where(item => item.Dirty).ToList();
        if (dirty.Count == 0)
        {
            MessageBox.Show(this, "没有需要保存的修改。", "服务器配置");
            return;
        }
        var port = dirty.FirstOrDefault(item => item.Spec.Key == "server-port");
        if (port != null && int.TryParse(port.Value, out var portNumber))
        {
            var clash = _otherPorts.Where(item => item.Port == portNumber).Select(item => item.Name).ToList();
            if (clash.Count > 0)
            {
                var answer = MessageBox.Show(this, "端口 " + portNumber + " 也被这些服务端使用：\n" + string.Join("\n", clash) + "\n\n仍然保存吗？", "端口相同", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes)
                    return;
            }
        }
        var level = dirty.FirstOrDefault(item => item.Spec.Key == "level-name");
        if (level != null)
        {
            var answer = MessageBox.Show(this, "世界文件夹将从「" + level.Baseline + "」改为「" + level.Value + "」。\n这不会重命名现有存档，下次启动会去加载新的文件夹。\n仍然保存吗？", "更换世界文件夹", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes)
                return;
        }
        var rcon = _entries.FirstOrDefault(item => item.Spec.Key == "enable-rcon");
        var password = _entries.FirstOrDefault(item => item.Spec.Key == "rcon.password");
        if (rcon is { Value: "true", Dirty: true } && string.IsNullOrEmpty(password?.Value))
        {
            MessageBox.Show(this, "打开了远程控制，但密码是空的。请先填写远程控制密码。", "服务器配置");
            return;
        }
        try
        {
            foreach (var entry in dirty)
                _document.Set(entry.Spec.Key, entry.Value);
            _document.Save(Path.Combine(_server.Directory, "server.properties"));
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存失败");
        }
    }
}
