using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace McPanel;

internal enum RunState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Crashed,
    Detached,
}

internal sealed class ServerSnapshot
{
    public string Name { get; set; } = "";
    public string Directory { get; set; } = "";
    public int Port { get; set; } = 25565;
    public string LevelName { get; set; } = "world";
    public string Motd { get; set; } = "";
    public int MaxPlayers { get; set; } = 20;
    public bool? OnlineMode { get; set; }
    public string? JavaArgs { get; set; }
    public string VersionLabel { get; set; } = "";
    public string? MemoryWarning { get; set; }
    public bool EulaAccepted { get; set; }
    public string? LaunchError { get; set; }
}

internal sealed class ScanResult
{
    public string Root { get; set; } = "";
    public List<ServerSnapshot> Servers { get; set; } = new();
    public List<string> Unrecognized { get; set; } = new();
}

internal sealed class RuntimeState
{
    public RunState State { get; set; }
    public int HostPid { get; set; }
    public int JavaPid { get; set; }
    public int ViewerPid { get; set; }
    public string WindowTitle { get; set; } = "";
    public List<string> Endpoints { get; set; } = new();
    public int? ExitCode { get; set; }
    public DateTime? StartedAt { get; set; }
    public string? CrashLog { get; set; }
    public Process? HostProcess { get; set; }
}

internal sealed class HostSession
{
    public string ServerName { get; set; } = "";
    public string ServerDir { get; set; } = "";
    public string PanelDir { get; set; } = "";
    public string CrashDir { get; set; } = "";
    public string JavaExe { get; set; } = "java";
    public string JavaArgs { get; set; } = "";
    public string WindowTitle { get; set; } = "";
    public int Port { get; set; }
    public List<string> Endpoints { get; set; } = new();
    public string Banner { get; set; } = "";
}

internal sealed class ViewerSession
{
    public string LogPath { get; set; } = "";
    public string WindowTitle { get; set; } = "";
    public string InstancePath { get; set; } = "";
    public int HostPid { get; set; }
}

internal sealed class ViewerMark
{
    public int Pid { get; set; }
    public string WindowTitle { get; set; } = "";
}

internal sealed class InstanceState
{
    public int HostPid { get; set; }
    public int JavaPid { get; set; }
    public string WindowTitle { get; set; } = "";
    public string State { get; set; } = "";
    public int? ExitCode { get; set; }
    public DateTime StartedAt { get; set; }
    public List<string> Endpoints { get; set; } = new();
    public string? CrashLog { get; set; }
}

internal static class JsonFiles
{
    public static readonly JsonSerializerOptions Opt = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Write<T>(string path, T value)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Opt));
        File.Move(tmp, path, true);
    }

    public static T? Read<T>(string path)
    {
        try
        {
            if (!File.Exists(path))
                return default;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Opt);
        }
        catch
        {
            return default;
        }
    }
}

internal static class PanelPaths
{
    public static string Panel(string serverDir) => Path.Combine(serverDir, ".mcpanel");
    public static string ConsoleLog(string serverDir) => Path.Combine(Panel(serverDir), "console.log");
    public static string Instance(string serverDir) => Path.Combine(Panel(serverDir), "instance.json");
    public static string Session(string serverDir) => Path.Combine(Panel(serverDir), "session.json");
    public static string ViewerSession(string serverDir) => Path.Combine(Panel(serverDir), "viewer-session.json");
    public static string ViewerMark(string serverDir) => Path.Combine(Panel(serverDir), "viewer-mark.json");
    public static string Inbox(string serverDir) => Path.Combine(Panel(serverDir), "inbox");
    public static string Data(string root) => Path.Combine(root, "_panel-data");
    public static string Crash(string root, string serverName) => Path.Combine(Data(root), "crash-logs", Safe(serverName));

    public static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(invalid.Contains(c) ? '_' : c);
        var text = sb.ToString().Trim();
        return text.Length == 0 ? "server" : text;
    }
}

internal static class ProcUtil
{
    public static bool Alive(int pid, string? nameContains = null)
    {
        if (pid <= 0)
            return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
                return false;
            if (string.IsNullOrEmpty(nameContains))
                return true;
            return process.ProcessName.Contains(nameContains, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
