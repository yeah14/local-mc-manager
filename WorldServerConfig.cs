using System.Text;

namespace McPanel;

internal enum WorldConfigKind
{
    Bool,
    Number,
    String,
    Array,
    Raw,
}

internal sealed class WorldConfigField
{
    public string Key { get; init; } = "";
    public string Group { get; init; } = "";
    public string Description { get; init; } = "";
    public WorldConfigKind Kind { get; init; }
    public string Raw { get; init; } = "";
    public string Value { get; set; } = "";
    public string Baseline { get; set; } = "";
    public string[]? Choices { get; init; }
    public int Start { get; init; }
    public int Length { get; init; }
    public bool Dirty => Value != Baseline;
    public string Label => Key.LastIndexOf('.') is var dot and >= 0 ? Key[(dot + 1)..] : Key;
}

internal sealed class WorldConfigFile
{
    public string Name { get; }
    public string FilePath { get; }
    public string Text { get; private set; }
    public List<WorldConfigField> Fields { get; } = new();
    public string? Error { get; }
    public bool Dirty => Fields.Any(field => field.Dirty);
    readonly bool _bom;

    WorldConfigFile(string name, string path, string text, bool bom, string? error)
    {
        Name = name;
        FilePath = path;
        Text = text;
        _bom = bom;
        Error = error;
    }

    public static IReadOnlyList<WorldConfigFile> LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return Array.Empty<WorldConfigFile>();
        return Directory.GetFiles(directory)
            .Where(path => path.EndsWith(".toml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".snbt", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(Load)
            .ToList();
    }

    public static WorldConfigFile Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = Encoding.UTF8.GetString(bom ? bytes.AsSpan(3) : bytes);
        return Parse(Path.GetFileName(path), path, text, bom);
    }

    public static WorldConfigFile Parse(string name, string text) => Parse(name, "", text, false);

    static WorldConfigFile Parse(string name, string path, string text, bool bom)
    {
        var file = new WorldConfigFile(name, path, text, bom, null);
        try
        {
            if (name.EndsWith(".snbt", StringComparison.OrdinalIgnoreCase))
                Snbt.Read(text, file.Fields);
            else if (name.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
                Toml.Read(text, file.Fields);
            else
                return new WorldConfigFile(name, path, text, bom, "只编辑 .toml 和 .snbt。");
        }
        catch (Exception ex)
        {
            return new WorldConfigFile(name, path, text, bom, ex.Message);
        }
        return file;
    }

    public string Render()
    {
        if (!Dirty)
            return Text;
        var text = Text;
        foreach (var field in Fields.Where(item => item.Dirty).OrderByDescending(item => item.Start))
        {
            var formatted = Format(field);
            text = string.Concat(text.AsSpan(0, field.Start), formatted, text.AsSpan(field.Start + field.Length));
        }
        return text;
    }

    public void Save()
    {
        if (Error != null)
            throw new InvalidOperationException(Name + " 没能读完，没有保存。");
        if (FilePath.Length == 0)
            throw new InvalidOperationException("没有文件路径。");
        var rendered = Render();
        File.WriteAllText(FilePath, rendered, new UTF8Encoding(_bom));
        Text = rendered;
        foreach (var field in Fields)
            field.Baseline = field.Value;
    }

    public override string ToString() => Name;

    static string Format(WorldConfigField field)
    {
        var value = field.Value;
        switch (field.Kind)
        {
            case WorldConfigKind.Bool:
                if (value is not ("true" or "false"))
                    throw new InvalidOperationException(field.Key + " 只能是开或关。");
                return value;
            case WorldConfigKind.Number:
                value = value.Trim();
                if (!IsNumber(value))
                    throw new InvalidOperationException(field.Key + " 需要填写数字。");
                if (Suffix(value) == null && Suffix(field.Raw) is char suffix)
                    value += suffix;
                return value;
            case WorldConfigKind.String:
                return "\"" + Escape(value) + "\"";
            case WorldConfigKind.Array:
                var array = value.Trim();
                if (!array.StartsWith('[') || !Balanced(array))
                    throw new InvalidOperationException(field.Key + " 需要是成对括号包住的列表。");
                return value.TrimEnd();
            default:
                if (value.Contains('\n') || value.Contains('\r'))
                    throw new InvalidOperationException(field.Key + " 不能分成多行。");
                return value.Trim();
        }
    }

    static bool IsNumber(string text)
    {
        if (text.Length == 0)
            return false;
        var body = Suffix(text) == null ? text : text[..^1];
        var index = 0;
        if (body.Length > 0 && body[0] is '+' or '-')
            index++;
        var digit = false;
        var dot = false;
        for (; index < body.Length; index++)
        {
            var ch = body[index];
            if (char.IsDigit(ch))
            {
                digit = true;
                continue;
            }
            if (ch == '.' && !dot)
            {
                dot = true;
                continue;
            }
            if (ch is 'e' or 'E')
            {
                index++;
                if (index < body.Length && body[index] is '+' or '-')
                    index++;
                var exp = false;
                for (; index < body.Length; index++)
                {
                    if (!char.IsDigit(body[index]))
                        return false;
                    exp = true;
                }
                return digit && exp;
            }
            return false;
        }
        return digit;
    }

    static char? Suffix(string text)
    {
        if (text.Length == 0)
            return null;
        return text[^1] is 'd' or 'D' or 'f' or 'F' or 'b' or 'B' or 's' or 'S' or 'L' or 'l' ? text[^1] : null;
    }

    static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default: builder.Append(ch); break;
            }
        }
        return builder.ToString();
    }

    public static bool Balanced(string text)
    {
        var square = 0;
        var curly = 0;
        var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote != '\0')
            {
                if (ch == '\\' && quote == '"')
                {
                    i++;
                    continue;
                }
                if (ch == quote)
                    quote = '\0';
                continue;
            }
            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }
            if (ch == '#')
            {
                while (i < text.Length && text[i] != '\n')
                    i++;
                continue;
            }
            if (ch == '[') square++;
            else if (ch == ']') square--;
            else if (ch == '{') curly++;
            else if (ch == '}') curly--;
            if (square < 0 || curly < 0)
                return false;
        }
        return quote == '\0' && square == 0 && curly == 0;
    }

    static class Toml
    {
        public static void Read(string text, List<WorldConfigField> fields)
        {
            var table = "";
            var comments = new List<string>();
            var i = 0;
            while (i < text.Length)
            {
                var mark = i;
                var lineEnd = text.IndexOf('\n', i);
                if (lineEnd < 0)
                    lineEnd = text.Length;
                var line = text[i..lineEnd].TrimEnd('\r');
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    comments.Clear();
                    i = Step(text, lineEnd);
                    continue;
                }
                if (trimmed.StartsWith('#'))
                {
                    comments.Add(trimmed[1..].Trim());
                    i = Step(text, lineEnd);
                    continue;
                }
                if (IsTable(trimmed))
                {
                    var end = trimmed.IndexOf(']');
                    table = trimmed[1..end].Trim();
                    comments.Clear();
                    i = Step(text, lineEnd);
                    continue;
                }
                var eq = line.IndexOf('=');
                if (eq < 0 || !TryKey(line[..eq], out var key))
                {
                    comments.Clear();
                    i = Step(text, lineEnd);
                    continue;
                }
                var valueStart = i + eq + 1;
                while (valueStart < text.Length && text[valueStart] is ' ' or '\t')
                    valueStart++;
                var valueEnd = Consume(text, valueStart);
                if (valueEnd <= valueStart)
                {
                    comments.Clear();
                    i = Step(text, lineEnd);
                    continue;
                }
                Add(fields, table.Length == 0 ? key : table + "." + key, table.Length == 0 ? "根" : table, comments, text, valueStart, valueEnd);
                comments.Clear();
                i = valueEnd;
                if (i == mark)
                    i++;
                else if (i < text.Length && text[i] != '\n')
                    i = Step(text, text.IndexOf('\n', i) is var nl and >= 0 ? nl : text.Length);
                else
                    i = Step(text, i);
            }
        }

        static bool IsTable(string trimmed)
        {
            if (!trimmed.StartsWith('['))
                return false;
            var end = trimmed.IndexOf(']');
            if (end <= 1)
                return false;
            var after = trimmed[(end + 1)..].TrimStart();
            if (after.Length > 0 && !after.StartsWith('#'))
                return false;
            var inside = trimmed[1..end].Trim();
            return inside.Length > 0 && !inside.Contains('[') && !inside.Contains('=');
        }

        static bool TryKey(string raw, out string key)
        {
            key = raw.Trim();
            if (key.Length >= 2 && key[0] == '"' && key[^1] == '"')
                key = key[1..^1];
            if (key.Length == 0)
                return false;
            foreach (var ch in key)
            {
                if (!(char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.'))
                    return false;
            }
            return true;
        }
    }

    static class Snbt
    {
        public static void Read(string text, List<WorldConfigField> fields)
        {
            var path = new List<string>();
            var comments = new List<string>();
            var i = Skip(text, 0, comments);
            if (i < text.Length && text[i] == '{')
                i++;
            while (i < text.Length)
            {
                var mark = i;
                comments.Clear();
                i = Skip(text, i, comments);
                if (i >= text.Length)
                    break;
                if (text[i] == '}')
                {
                    if (path.Count > 0)
                        path.RemoveAt(path.Count - 1);
                    i++;
                    continue;
                }
                if (text[i] == ',')
                {
                    i++;
                    continue;
                }
                if (!TryReadKey(text, ref i, out var key))
                    throw new InvalidOperationException("读到无法识别的字段。");
                var description = comments.ToList();
                comments.Clear();
                i = Skip(text, i, description);
                if (i >= text.Length || text[i] != ':')
                    throw new InvalidOperationException("字段 " + key + " 后面没有冒号。");
                i++;
                i = Skip(text, i, description);
                if (i >= text.Length)
                    break;
                if (text[i] == '{')
                {
                    path.Add(key);
                    i++;
                    continue;
                }
                var valueEnd = Consume(text, i);
                if (valueEnd <= i)
                    throw new InvalidOperationException("字段 " + key + " 的值不完整。");
                var group = path.Count == 0 ? "根" : string.Join('.', path);
                var full = path.Count == 0 ? key : group + "." + key;
                Add(fields, full, group, description, text, i, valueEnd);
                i = valueEnd;
                if (i == mark)
                    i++;
            }
        }

        static bool TryReadKey(string text, ref int i, out string key)
        {
            key = "";
            if (i >= text.Length)
                return false;
            if (text[i] == '"')
            {
                var end = Consume(text, i);
                if (end <= i + 1)
                    return false;
                key = Unquote(text[i..end]);
                i = end;
                return key.Length > 0;
            }
            var start = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '-' or '.'))
                i++;
            if (i == start)
                return false;
            key = text[start..i];
            return true;
        }

        static int Skip(string text, int i, List<string> comments)
        {
            while (i < text.Length)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    i++;
                    continue;
                }
                if (text[i] != '#')
                    break;
                i++;
                var start = i;
                while (i < text.Length && text[i] != '\n')
                    i++;
                var comment = text[start..i].Trim();
                if (comment.Length > 0)
                    comments.Add(comment);
            }
            return i;
        }
    }

    static void Add(List<WorldConfigField> fields, string key, string group, List<string> comments, string text, int start, int end)
    {
        var raw = text[start..end];
        var kind = Classify(raw, out var value, out var choices);
        choices ??= Allowed(comments, value);
        fields.Add(new WorldConfigField
        {
            Key = key,
            Group = group,
            Description = string.Join("\n", comments),
            Kind = kind,
            Raw = raw,
            Value = value,
            Baseline = value,
            Choices = choices,
            Start = start,
            Length = end - start,
        });
    }

    static WorldConfigKind Classify(string raw, out string value, out string[]? choices)
    {
        choices = null;
        var trimmed = raw.Trim();
        if (trimmed is "true" or "false")
        {
            value = trimmed;
            return WorldConfigKind.Bool;
        }
        if (trimmed.StartsWith("\"\"\"", StringComparison.Ordinal))
        {
            value = trimmed;
            return WorldConfigKind.Raw;
        }
        if (trimmed.StartsWith('"') || trimmed.StartsWith('\''))
        {
            value = Unquote(trimmed);
            return WorldConfigKind.String;
        }
        if (trimmed.StartsWith('['))
        {
            value = raw.TrimEnd();
            return WorldConfigKind.Array;
        }
        if (trimmed.StartsWith('{'))
        {
            value = raw.TrimEnd();
            return WorldConfigKind.Raw;
        }
        if (IsNumber(trimmed))
        {
            value = trimmed;
            return WorldConfigKind.Number;
        }
        value = trimmed;
        return WorldConfigKind.Raw;
    }

    static string[]? Allowed(List<string> comments, string current)
    {
        foreach (var comment in comments)
        {
            const string marker = "Allowed Values:";
            var at = comment.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                continue;
            var parts = comment[(at + marker.Length)..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Trim().Trim('"'))
                .Where(part => part.Length > 0)
                .ToArray();
            if (parts.Length == 0)
                return null;
            if (parts.Contains(current, StringComparer.Ordinal))
                return parts;
            return parts.Append(current).ToArray();
        }
        return null;
    }

    static string Unquote(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length < 2)
            return trimmed;
        var quote = trimmed[0];
        if (quote is not ('"' or '\'') || trimmed[^1] != quote)
            return trimmed;
        var body = trimmed[1..^1];
        if (quote == '\'')
            return body;
        var builder = new StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != '\\' || i + 1 >= body.Length)
            {
                builder.Append(body[i]);
                continue;
            }
            var next = body[++i];
            builder.Append(next switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                'u' when i + 4 < body.Length && ushort.TryParse(body.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var code) => Take(ref i, 4, (char)code),
                _ => next,
            });
        }
        return builder.ToString();
    }

    static char Take(ref int index, int extra, char value)
    {
        index += extra;
        return value;
    }

    static int Consume(string text, int i)
    {
        if (i >= text.Length)
            return i;
        var ch = text[i];
        if (ch == '"')
            return ConsumeString(text, i, '"');
        if (ch == '\'')
            return ConsumeString(text, i, '\'');
        if (ch == '[')
            return ConsumeNest(text, i, '[', ']');
        if (ch == '{')
            return ConsumeNest(text, i, '{', '}');
        if (StartsWord(text, i, "true"))
            return i + 4;
        if (StartsWord(text, i, "false"))
            return i + 5;
        return ConsumeNumber(text, i);
    }

    static bool StartsWord(string text, int i, string word)
    {
        if (i + word.Length > text.Length || !text.AsSpan(i, word.Length).SequenceEqual(word))
            return false;
        var after = i + word.Length;
        return after >= text.Length || !(char.IsLetterOrDigit(text[after]) || text[after] == '_');
    }

    static int ConsumeString(string text, int i, char quote)
    {
        var triple = i + 2 < text.Length && text[i + 1] == quote && text[i + 2] == quote;
        i += triple ? 3 : 1;
        while (i < text.Length)
        {
            if (text[i] == '\\' && quote == '"')
            {
                i += i + 1 < text.Length ? 2 : 1;
                continue;
            }
            if (text[i] == quote)
            {
                if (!triple)
                    return i + 1;
                if (i + 2 < text.Length && text[i + 1] == quote && text[i + 2] == quote)
                    return i + 3;
            }
            i++;
        }
        return -1;
    }

    static int ConsumeNest(string text, int i, char open, char close)
    {
        var depth = 0;
        var quote = '\0';
        for (; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote != '\0')
            {
                if (ch == '\\' && quote == '"')
                {
                    i++;
                    continue;
                }
                if (ch == quote)
                    quote = '\0';
                continue;
            }
            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }
            if (ch == '#')
            {
                while (i < text.Length && text[i] != '\n')
                    i++;
                continue;
            }
            if (ch == open)
                depth++;
            else if (ch == close)
            {
                depth--;
                if (depth == 0)
                    return i + 1;
            }
        }
        return -1;
    }

    static int ConsumeNumber(string text, int i)
    {
        var start = i;
        if (i < text.Length && text[i] is '+' or '-')
            i++;
        var digit = false;
        while (i < text.Length && char.IsDigit(text[i]))
        {
            digit = true;
            i++;
        }
        if (i < text.Length && text[i] == '.')
        {
            i++;
            while (i < text.Length && char.IsDigit(text[i]))
            {
                digit = true;
                i++;
            }
        }
        if (i < text.Length && text[i] is 'e' or 'E')
        {
            var exp = i + 1;
            if (exp < text.Length && text[exp] is '+' or '-')
                exp++;
            var expDigit = false;
            while (exp < text.Length && char.IsDigit(text[exp]))
            {
                expDigit = true;
                exp++;
            }
            if (expDigit)
                i = exp;
        }
        if (!digit)
            return start;
        if (i < text.Length && text[i] is 'd' or 'D' or 'f' or 'F' or 'b' or 'B' or 's' or 'S' or 'L' or 'l')
            i++;
        return i;
    }

    static int Step(string text, int newline) => newline < text.Length && text[newline] == '\n' ? newline + 1 : newline;
}

internal static class WorldConfigCheck
{
    public static void Run(Action<bool, string> check)
    {
        const string toml = """
            # keep
            [server]
            	#Range: 0.0 ~ 1.0
            	zombieBaseSummonChance = 0.1
            	permissionHandler = "forge:default_handler"
            	removeErroringEntities = false
            	#Allowed Values: ONLY, ALL_BUT
            	claimableDimensionsListType = "ALL_BUT"
            	claimableDimensionsList = []

            """;
        var parsed = WorldConfigFile.Parse("forge-server.toml", toml);
        check(parsed.Error == null, "世界配置能读 toml");
        var chance = parsed.Fields.First(field => field.Key == "server.zombieBaseSummonChance");
        var handler = parsed.Fields.First(field => field.Key == "server.permissionHandler");
        var entities = parsed.Fields.First(field => field.Key == "server.removeErroringEntities");
        var listType = parsed.Fields.First(field => field.Key == "server.claimableDimensionsListType");
        check(chance.Kind == WorldConfigKind.Number && chance.Value == "0.1", "toml 数字");
        check(handler.Kind == WorldConfigKind.String && handler.Value == "forge:default_handler", "toml 字符串");
        check(entities.Kind == WorldConfigKind.Bool && chance.Description.Contains("Range", StringComparison.Ordinal), "toml 注释跟在字段上");
        check(listType.Choices is ["ONLY", "ALL_BUT"], "toml 可选值");
        entities.Value = "true";
        handler.Value = "mine:handler";
        var saved = parsed.Render();
        check(saved.Contains("# keep", StringComparison.Ordinal) && saved.Contains("zombieBaseSummonChance = 0.1", StringComparison.Ordinal), "没改的 toml 字段保持原样");
        check(saved.Contains("removeErroringEntities = true", StringComparison.Ordinal) && saved.Contains("permissionHandler = \"mine:handler\"", StringComparison.Ordinal), "改过的 toml 字段已写回");

        const string snbt = """
            {
            	# Hunger
            	# Default: 20.0
            	exhaustion_per_block: 20.0d
            	admin: {
            		# Default: true
            		enabled: true
            	}
            	merge_tags: [
            		"a"
            		"b"
            	]
            }

            """;
        var tree = WorldConfigFile.Parse("demo.snbt", snbt);
        check(tree.Error == null && tree.Render() == snbt, "没改的 snbt 原样返回");
        var hunger = tree.Fields.First(field => field.Key == "exhaustion_per_block");
        var enabled = tree.Fields.First(field => field.Key == "admin.enabled");
        var tags = tree.Fields.First(field => field.Key == "merge_tags");
        check(hunger.Kind == WorldConfigKind.Number && hunger.Value == "20.0d" && hunger.Description.Contains("Hunger", StringComparison.Ordinal), "snbt 带后缀的数字");
        check(enabled.Kind == WorldConfigKind.Bool && tags.Kind == WorldConfigKind.Array, "snbt 嵌套字段和列表");
        enabled.Value = "false";
        tags.Value = "[\"a\", \"c\"]";
        var snbtSaved = tree.Render();
        check(snbtSaved.Contains("exhaustion_per_block: 20.0d", StringComparison.Ordinal) && snbtSaved.Contains("# Hunger", StringComparison.Ordinal), "没改的 snbt 字段保持原样");
        check(snbtSaved.Contains("enabled: false", StringComparison.Ordinal) && snbtSaved.Contains("[\"a\", \"c\"]", StringComparison.Ordinal), "改过的 snbt 字段已写回");
        hunger.Value = "15";
        check(tree.Render().Contains("exhaustion_per_block: 15d", StringComparison.Ordinal), "数字保留原来的后缀");
    }
}

internal sealed class WorldConfigForm : Form
{
    readonly bool _readOnly;
    readonly List<WorldConfigFile> _files;
    readonly ListView _list = new();
    readonly ComboBox _filesBox = new();
    readonly TextBox _search = new();
    readonly Label _description = new();
    readonly TextBox _text = new();
    readonly ComboBox _combo = new();
    readonly Button _save;
    WorldConfigFile? _file;
    WorldConfigField? _current;
    bool _loading;

    public WorldConfigForm(ServerSnapshot server, bool readOnly)
    {
        _readOnly = readOnly;
        var directory = Path.Combine(server.Directory, server.LevelName, "serverconfig");
        _files = WorldConfigFile.LoadDirectory(directory).ToList();

        Theme.Style(this);
        Text = "世界配置 — " + server.Name;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MinimumSize = new Size(780, 580);
        ClientSize = new Size(960, 700);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, readOnly || _files.Count == 0 ? 64 : 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        var banner = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = readOnly ? Theme.Yellow : Theme.Muted,
            Text = _files.Count == 0
                ? "当前世界还没有 serverconfig。\n" + directory + "\n先启动一次服务端，Forge 会在世界文件夹里生成这些配置。"
                : readOnly
                    ? "服务器正在运行，现在只能查看。停服后再改。关服时游戏可能把这些文件写回去。\n" + directory
                    : "改的是当前世界 serverconfig 里的模组配置。没改过的行会原样保留。改完点保存，下次启动生效。\n" + directory,
        };

        _filesBox.Dock = DockStyle.Fill;
        _filesBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _filesBox.FlatStyle = FlatStyle.Flat;
        _filesBox.BackColor = Theme.Input;
        _filesBox.ForeColor = Theme.Text;
        foreach (var file in _files)
            _filesBox.Items.Add(file);
        _filesBox.SelectedIndexChanged += (_, _) => SelectFile();

        _search.Dock = DockStyle.Fill;
        _search.BorderStyle = BorderStyle.FixedSingle;
        _search.BackColor = Theme.Input;
        _search.ForeColor = Theme.Text;
        _search.PlaceholderText = "搜索字段、说明或当前值";
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
        _list.Columns.Add("字段", 220);
        _list.Columns.Add("当前值", 240);
        _list.Columns.Add("分组", 180);
        _list.Columns.Add("说明", 260);
        _list.SelectedIndexChanged += (_, _) => SelectCurrent();

        var editor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        _description.Dock = DockStyle.Fill;
        _description.ForeColor = Theme.Muted;
        _text.Dock = DockStyle.Fill;
        _text.Multiline = true;
        _text.ScrollBars = ScrollBars.Vertical;
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
        var editorHost = new Panel { Dock = DockStyle.Fill };
        editorHost.Controls.Add(_text);
        editorHost.Controls.Add(_combo);
        editor.Controls.Add(_description, 0, 0);
        editor.Controls.Add(editorHost, 0, 1);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        _save = Theme.MakeButton("保存", true);
        var close = Theme.MakeButton("关闭");
        _save.Enabled = !readOnly && _files.Count > 0;
        _save.Click += (_, _) => SaveFiles();
        close.Click += (_, _) => Close();
        buttons.Controls.Add(close);
        buttons.Controls.Add(_save);

        layout.Controls.Add(banner, 0, 0);
        layout.Controls.Add(_filesBox, 0, 1);
        layout.Controls.Add(_search, 0, 2);
        layout.Controls.Add(_list, 0, 3);
        layout.Controls.Add(editor, 0, 4);
        layout.Controls.Add(buttons, 0, 5);
        Controls.Add(layout);
        if (_filesBox.Items.Count > 0)
            _filesBox.SelectedIndex = 0;
    }

    void SelectFile()
    {
        if (_loading)
            return;
        PushEditor();
        _file = _filesBox.SelectedItem as WorldConfigFile;
        _current = null;
        FillList();
    }

    void FillList()
    {
        var selected = _current?.Key;
        var query = _search.Text.Trim();
        _list.BeginUpdate();
        _list.Items.Clear();
        if (_file?.Error != null)
        {
            _description.Text = _file.Name + "\n" + _file.Error;
        }
        else if (_file != null)
        {
            foreach (var field in _file.Fields)
            {
                if (!Matches(field, query))
                    continue;
                var item = new ListViewItem(field.Dirty ? field.Label + " *" : field.Label);
                item.SubItems.Add(Display(field));
                item.SubItems.Add(field.Group);
                item.SubItems.Add(FirstLine(field.Description));
                item.Tag = field;
                item.ForeColor = field.Dirty ? Theme.Yellow : Theme.Text;
                _list.Items.Add(item);
                if (selected != null && field.Key == selected)
                    item.Selected = true;
            }
        }
        _list.EndUpdate();
        if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0)
            _list.Items[0].Selected = true;
        else if (_list.Items.Count == 0)
            _current = null;
    }

    static bool Matches(WorldConfigField field, string query)
    {
        if (query.Length == 0)
            return true;
        return field.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
            || field.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
            || field.Group.Contains(query, StringComparison.OrdinalIgnoreCase)
            || field.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || Display(field).Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    static string Display(WorldConfigField field)
    {
        if (field.Kind == WorldConfigKind.Bool)
            return field.Value == "true" ? "开" : field.Value == "false" ? "关" : field.Value;
        var text = field.Value.Replace("\r", "").Replace('\n', ' ');
        return text.Length > 80 ? text[..80] + "…" : text;
    }

    static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 80 ? line[..80] + "…" : line;
    }

    void SelectCurrent()
    {
        if (_list.SelectedItems.Count == 0)
            return;
        if (_list.SelectedItems[0].Tag is not WorldConfigField field || ReferenceEquals(field, _current))
            return;
        _current = field;
        _loading = true;
        _description.Text = field.Key + (field.Description.Length == 0 ? "" : "\n" + field.Description);
        var choices = field.Kind == WorldConfigKind.Bool
            ? new[] { new SettingChoice("开", "true"), new SettingChoice("关", "false") }
            : field.Choices?.Select(choice => new SettingChoice(choice, choice)).ToArray();
        if (choices != null)
        {
            _combo.Visible = true;
            _text.Visible = false;
            _combo.Items.Clear();
            foreach (var choice in choices)
                _combo.Items.Add(choice);
            var selected = choices.FirstOrDefault(choice => choice.Value == field.Value);
            if (selected != null)
                _combo.SelectedItem = selected;
            else if (_combo.Items.Count > 0)
                _combo.SelectedIndex = 0;
        }
        else
        {
            _combo.Visible = false;
            _text.Visible = true;
            _text.AcceptsReturn = field.Kind == WorldConfigKind.Array;
            _text.Text = field.Value;
        }
        _text.ReadOnly = _readOnly;
        _combo.Enabled = !_readOnly;
        _loading = false;
    }

    void PushEditor()
    {
        if (_loading || _current == null || _readOnly)
            return;
        var value = _combo.Visible && _combo.SelectedItem is SettingChoice choice ? choice.Value : _text.Text;
        if (_current.Kind is WorldConfigKind.Number or WorldConfigKind.Raw)
            value = value.Trim();
        if (value == _current.Value)
            return;
        _current.Value = value;
        foreach (ListViewItem item in _list.Items)
        {
            if (!ReferenceEquals(item.Tag, _current))
                continue;
            item.Text = _current.Dirty ? _current.Label + " *" : _current.Label;
            item.SubItems[1].Text = Display(_current);
            item.ForeColor = _current.Dirty ? Theme.Yellow : Theme.Text;
            break;
        }
    }

    void SaveFiles()
    {
        if (_readOnly)
            return;
        PushEditor();
        var dirty = _files.Where(file => file.Dirty).ToList();
        if (dirty.Count == 0)
        {
            MessageBox.Show(this, "没有需要保存的修改。", "世界配置");
            return;
        }
        try
        {
            foreach (var file in dirty)
                file.Save();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存失败");
        }
    }
}
