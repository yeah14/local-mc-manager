using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace McPanel;

internal static class NetInfo
{
    public static List<string> LanIpv4()
    {
        var result = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    var text = address.Address.ToString();
                    if (IPAddress.IsLoopback(address.Address) || text.StartsWith("169.254.", StringComparison.Ordinal))
                        continue;
                    if (!result.Contains(text))
                        result.Add(text);
                }
            }
        }
        catch
        {
            // 网卡信息读不到时仍可用本机回环地址。
        }
        return result;
    }

    public static List<string> Endpoints(int port)
    {
        var list = new List<string> { $"127.0.0.1:{port}" };
        foreach (var ip in LanIpv4())
            list.Add($"{ip}:{port}");
        return list;
    }

    public static bool PortInUse(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == port);
        }
        catch
        {
            return false;
        }
    }

    public static string? FindJava()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "java.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // 跳过无效 PATH 项。
            }
        }
        return null;
    }
}

internal static class PropertiesFile
{
    public static Dictionary<string, string> Read(string path)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
            return dict;
        var text = Decode(File.ReadAllBytes(path));
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#')
                continue;
            var split = line.IndexOf('=');
            if (split <= 0)
                continue;
            dict[line[..split].Trim()] = Unescape(line[(split + 1)..].Trim());
        }
        return dict;
    }

    public static string Unescape(string value)
    {
        return Regex.Replace(value, @"\\u([0-9a-fA-F]{4})", match =>
            ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString());
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
}

internal static class EulaFile
{
    public static bool IsAccepted(string serverDir)
    {
        var path = Path.Combine(serverDir, "eula.txt");
        if (!File.Exists(path))
            return false;
        var text = File.ReadAllText(path);
        return Regex.IsMatch(text, @"^\s*eula\s*=\s*true\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    }

    public static void Accept(string serverDir)
    {
        var path = Path.Combine(serverDir, "eula.txt");
        File.WriteAllText(path, """
            #By changing the setting below to TRUE you are indicating your agreement to our EULA (https://aka.ms/MinecraftEULA).
            eula=true

            """, Encoding.ASCII);
    }
}

internal static class LaunchPlan
{
    public const string JvmPrefix =
        "-Dfile.encoding=UTF-8 -Dsun.stdout.encoding=UTF-8 -Dsun.stderr.encoding=UTF-8 -Dsun.jnu.encoding=UTF-8 -Dterminal.jline=false -Dterminal.ansi=false -Dlog4j2.skipJansi=true ";

    public static bool TryFromDirectory(string dir, out string args, out string error)
    {
        args = "";
        error = "";
        foreach (var bat in BatCandidates(dir))
        {
            if (TryFromBat(bat, out args))
                return true;
        }

        var jar = FindServerJar(dir);
        if (jar != null)
        {
            args = JvmPrefix + "-jar \"" + Path.GetFileName(jar).Replace("\"", "") + "\" nogui";
            return true;
        }

        error = "没有找到 run.bat / start.bat 里的 java 命令，也没有服务端 jar。";
        return false;
    }

    public static bool TryFromBat(string batPath, out string args)
    {
        args = "";
        foreach (var raw in File.ReadAllLines(batPath))
        {
            if (TryParseJavaLine(raw, out args))
                return true;
        }
        return false;
    }

    public static bool TryParseJavaLine(string raw, out string args)
    {
        args = "";
        var line = raw.Trim();
        if (line.Length == 0 || IsIgnorable(line))
            return false;

        string rest;
        if (line.StartsWith('\"'))
        {
            var end = line.IndexOf('"', 1);
            if (end < 0)
                return false;
            var exe = line[1..end];
            var file = Path.GetFileName(exe);
            if (!file.Equals("java", StringComparison.OrdinalIgnoreCase) &&
                !file.Equals("java.exe", StringComparison.OrdinalIgnoreCase))
                return false;
            rest = line[(end + 1)..].Trim();
        }
        else
        {
            var match = Regex.Match(line, @"^(?:call\s+)?java(?:\.exe)?\s+(.*)$", RegexOptions.IgnoreCase);
            if (!match.Success)
                return false;
            rest = match.Groups[1].Value.Trim();
        }

        rest = Regex.Replace(rest, @"\s+%+\*\s*$", "");
        rest = Regex.Replace(rest, @"\s+pause\s*$", "", RegexOptions.IgnoreCase).Trim();
        if (!Regex.IsMatch(rest, @"(^|\s)nogui(\s|$)", RegexOptions.IgnoreCase))
            rest += " nogui";
        args = JvmPrefix + rest;
        return true;
    }

    public static string VersionLabel(string? args)
    {
        if (string.IsNullOrWhiteSpace(args))
            return "未知版本";
        var forge = Regex.Match(args, @"minecraftforge[/\\]forge[/\\]([^/\\\s]+)[/\\]", RegexOptions.IgnoreCase);
        if (forge.Success)
            return "Forge " + forge.Groups[1].Value;
        var neo = Regex.Match(args, @"neoforge[/\\]([^/\\\s]+)[/\\]", RegexOptions.IgnoreCase);
        if (neo.Success)
            return "NeoForge " + neo.Groups[1].Value;
        var fabric = Regex.Match(args, @"fabric-loader[/\\]([^/\\\s]+)[/\\]", RegexOptions.IgnoreCase);
        if (fabric.Success)
            return "Fabric " + fabric.Groups[1].Value;
        if (args.Contains("-jar", StringComparison.OrdinalIgnoreCase))
            return "Jar 服务端";
        return "自定义启动";
    }

    public static string? MemoryWarning(string dir)
    {
        var path = Path.Combine(dir, "user_jvm_args.txt");
        if (!File.Exists(path))
            return "没有 user_jvm_args.txt。整合包服建议写上 -Xmx。";
        var active = File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'));
        var text = string.Join(' ', active);
        if (!text.Contains("-Xmx", StringComparison.OrdinalIgnoreCase))
            return "user_jvm_args.txt 里没有启用 -Xmx，整合包可能会因为内存不足崩溃。";
        return null;
    }

    public static string WindowTitle(string name, int port)
    {
        var clean = name.Replace("\r", " ").Replace("\n", " ");
        if (clean.Length > 80)
            clean = clean[..80];
        return $"[MC] {clean} | 端口 {port}";
    }

    public static string Banner(string name, IReadOnlyList<string> endpoints, string windowTitle, string? javaExe)
    {
        var sb = new StringBuilder();
        sb.AppendLine("========================================");
        sb.AppendLine("服务器: " + name);
        sb.AppendLine("启动时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("本机 IP:");
        foreach (var endpoint in endpoints)
            sb.AppendLine("  " + endpoint);
        sb.AppendLine("运行窗口: " + windowTitle);
        if (!string.IsNullOrWhiteSpace(javaExe))
            sb.AppendLine("Java: " + javaExe);
        sb.AppendLine("关闭日志窗口不会停止服务器。请在管理器里点「停止」。");
        sb.AppendLine("========================================");
        return sb.ToString().TrimEnd();
    }

    static bool IsIgnorable(string line)
    {
        var text = line.Trim();
        if (text.StartsWith("::", StringComparison.Ordinal))
            return true;
        if (text.StartsWith('@'))
            text = text[1..].Trim();
        if (text.StartsWith("echo", StringComparison.OrdinalIgnoreCase) &&
            (text.Length == 4 || char.IsWhiteSpace(text[4])))
            return true;
        if (text.StartsWith("rem", StringComparison.OrdinalIgnoreCase) &&
            (text.Length == 3 || char.IsWhiteSpace(text[3])))
            return true;
        return text.Equals("pause", StringComparison.OrdinalIgnoreCase);
    }

    static IEnumerable<string> BatCandidates(string dir)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "run.bat", "start.bat", "启动.bat" })
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path) && seen.Add(path))
                yield return path;
        }
        IEnumerable<string> bats;
        try
        {
            bats = Directory.EnumerateFiles(dir, "*.bat");
        }
        catch
        {
            yield break;
        }
        foreach (var bat in bats)
        {
            if (seen.Add(bat))
                yield return bat;
        }
    }

    static string? FindServerJar(string dir)
    {
        try
        {
            string? fallback = null;
            foreach (var jar in Directory.EnumerateFiles(dir, "*.jar"))
            {
                var name = Path.GetFileName(jar).ToLowerInvariant();
                if (name.Contains("server") || name.Contains("forge") || name.Contains("fabric") ||
                    name.Contains("neoforge") || name.Contains("paper") || name.Contains("purpur") ||
                    name.Contains("spigot") || name.Contains("mohist") || name.Contains("arclight"))
                    return jar;
                fallback ??= jar;
            }
            return fallback;
        }
        catch
        {
            return null;
        }
    }
}

internal static class ServerScanner
{
    public static bool IsIgnoredName(string name)
    {
        return name.Equals("McPanel", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".mcpanel", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith('.')
            || name.StartsWith('_');
    }

    public static bool LooksLikeServer(string dir)
    {
        try
        {
            if (File.Exists(Path.Combine(dir, "server.properties")))
                return true;
            if (File.Exists(Path.Combine(dir, "eula.txt")))
                return true;
            if (File.Exists(Path.Combine(dir, "run.bat")) || File.Exists(Path.Combine(dir, "start.bat")))
                return true;
            if (File.Exists(Path.Combine(dir, "user_jvm_args.txt")) && Directory.Exists(Path.Combine(dir, "libraries")))
                return true;
            return Directory.EnumerateFiles(dir, "*.jar").Any(path =>
            {
                var name = Path.GetFileName(path).ToLowerInvariant();
                return name.Contains("server") || name.Contains("forge") || name.Contains("fabric") || name.Contains("paper");
            });
        }
        catch
        {
            return false;
        }
    }

    public static ScanResult Scan(string root)
    {
        var result = new ScanResult { Root = root };
        foreach (var dir in Directory.GetDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (IsIgnoredName(name))
                continue;
            if (LooksLikeServer(dir))
            {
                result.Servers.Add(Inspect(dir));
                continue;
            }

            var nested = NestedServers(dir);
            if (nested.Count > 0)
            {
                foreach (var child in nested)
                    result.Servers.Add(Inspect(child));
            }
            else
                result.Unrecognized.Add(name);
        }
        result.Servers.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        result.Unrecognized.Sort(StringComparer.CurrentCultureIgnoreCase);
        return result;
    }

    public static string Signature(string root)
    {
        var sb = new StringBuilder();
        foreach (var dir in Directory.GetDirectories(root).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);
            if (IsIgnoredName(name))
                continue;
            AppendMark(sb, dir);
            if (LooksLikeServer(dir))
                continue;
            foreach (var child in NestedServers(dir))
                AppendMark(sb, child);
        }
        return sb.ToString();
    }

    static List<string> NestedServers(string dir)
    {
        var nested = new List<string>();
        try
        {
            foreach (var child in Directory.GetDirectories(dir).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(child);
                if (IsIgnoredName(name))
                    continue;
                if (LooksLikeServer(child))
                    nested.Add(child);
            }
        }
        catch
        {
            // 读不到子目录时，外层文件夹会显示为未识别。
        }
        return nested;
    }

    static void AppendMark(StringBuilder sb, string dir)
    {
        var ready = File.Exists(Path.Combine(dir, "server.properties"))
            || File.Exists(Path.Combine(dir, "run.bat"))
            || File.Exists(Path.Combine(dir, "eula.txt"));
        sb.Append(dir).Append(ready ? '1' : '0').Append('|');
    }

    static ServerSnapshot Inspect(string dir)
    {
        var props = PropertiesFile.Read(Path.Combine(dir, "server.properties"));
        var port = 25565;
        if (props.TryGetValue("server-port", out var portText) && int.TryParse(portText, out var parsed) && parsed is > 0 and < 65536)
            port = parsed;
        var level = props.TryGetValue("level-name", out var levelName) && IsSafeLevel(levelName) ? levelName : "world";
        var snap = new ServerSnapshot
        {
            Name = Path.GetFileName(dir),
            Directory = dir,
            Port = port,
            LevelName = level,
            Motd = props.TryGetValue("motd", out var motd) ? motd : "",
            MaxPlayers = props.TryGetValue("max-players", out var players) && int.TryParse(players, out var count) ? count : 20,
            EulaAccepted = EulaFile.IsAccepted(dir),
            MemoryWarning = LaunchPlan.MemoryWarning(dir),
        };
        if (props.TryGetValue("online-mode", out var online))
            snap.OnlineMode = online.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (LaunchPlan.TryFromDirectory(dir, out var args, out var error))
        {
            snap.JavaArgs = args;
            snap.VersionLabel = LaunchPlan.VersionLabel(args);
        }
        else
        {
            snap.LaunchError = error;
            snap.VersionLabel = "无法启动";
        }
        return snap;
    }

    static bool IsSafeLevel(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
            return false;
        return name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }
}

internal static class RootLocator
{
    static string AppDataFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "McPanel", "root.txt");

    public static string? Find()
    {
        var walked = Walk();
        if (walked != null)
            return walked;
        try
        {
            if (File.Exists(AppDataFile))
            {
                var saved = File.ReadAllText(AppDataFile).Trim();
                if (Directory.Exists(saved))
                    return saved;
            }
        }
        catch
        {
            // 读不到上次选择的目录时继续让用户手选。
        }
        return null;
    }

    public static void Remember(string root)
    {
        var parent = Path.GetDirectoryName(AppDataFile);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);
        File.WriteAllText(AppDataFile, root);
    }

    public static string? Walk()
    {
        var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            if (LooksLikeRoot(dir))
                return dir;
            var parent = Directory.GetParent(dir);
            if (parent == null)
                break;
            dir = parent.FullName;
        }
        return null;
    }

    public static bool LooksLikeRoot(string dir)
    {
        try
        {
            return Directory.GetDirectories(dir).Any(path =>
            {
                var name = Path.GetFileName(path);
                return !ServerScanner.IsIgnoredName(name) && ServerScanner.LooksLikeServer(path);
            });
        }
        catch
        {
            return false;
        }
    }
}
