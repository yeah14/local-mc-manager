using System.Diagnostics;
using System.Text;

namespace McPanel;

internal static class ServerHost
{
    static readonly object Gate = new();
    static HostSession? _session;
    static Process? _java;
    static StreamWriter? _log;
    static readonly Queue<string> Ring = new();
    static string _state = "starting";
    static int? _exitCode;
    static string? _crashFolder;
    static bool _crashClaimed;
    static bool _sawCrash;
    static DateTime _startedAt;

    public static int Run(string sessionPath)
    {
        var session = JsonFiles.Read<HostSession>(sessionPath);
        if (session == null)
            return 2;
        _session = session;
        _startedAt = DateTime.Now;
        try
        {
            Directory.CreateDirectory(session.PanelDir);
            Directory.CreateDirectory(session.CrashDir);
            var inbox = Path.Combine(session.PanelDir, "inbox");
            if (Directory.Exists(inbox))
                Directory.Delete(inbox, true);
            Directory.CreateDirectory(inbox);

            var stream = new FileStream(Path.Combine(session.PanelDir, "console.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            _log = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            foreach (var line in session.Banner.Replace("\r", "").Split('\n'))
                Accept(line);

            _java = StartJava(session);
            _state = "starting";
            WriteInstance();

            var stdout = new Thread(() => Pump(_java.StandardOutput)) { IsBackground = true, Name = "mc-stdout" };
            var stderr = new Thread(() => Pump(_java.StandardError)) { IsBackground = true, Name = "mc-stderr" };
            stdout.Start();
            stderr.Start();

            while (!_java.HasExited)
            {
                DrainInbox();
                Thread.Sleep(150);
            }
            stdout.Join(4000);
            stderr.Join(4000);
            _exitCode = _java.ExitCode;
            if ((_exitCode != 0 || _sawCrash) && _crashFolder == null)
                SaveCrash(_sawCrash ? "崩溃日志" : "进程退出，代码 " + _exitCode);
            if (_crashFolder != null)
            {
                CrashArchive.MarkExit(_crashFolder, _exitCode.Value);
                CrashArchive.CopyNewReports(_crashFolder, session.ServerDir, _startedAt);
            }
            _state = _exitCode != 0 || _sawCrash ? "crashed" : "exited";
            WriteInstance();
            Accept(_state == "crashed" ? "服务器异常退出，代码 " + _exitCode : "服务器已关闭。");
            return _exitCode ?? 0;
        }
        catch (Exception ex)
        {
            try
            {
                Accept("启动失败: " + ex.Message);
                _state = "crashed";
                _exitCode = -1;
                SaveCrash(ex.Message);
                WriteInstance();
            }
            catch
            {
                // 失败信息写不进去时直接退出。
            }
            return 1;
        }
        finally
        {
            try { _log?.Dispose(); } catch { }
        }
    }

    static Process StartJava(HostSession session)
    {
        var info = new ProcessStartInfo
        {
            FileName = session.JavaExe,
            Arguments = session.JavaArgs,
            WorkingDirectory = session.ServerDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException("无法启动 java。");
        return process;
    }

    static void Pump(StreamReader reader)
    {
        try
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
                Accept(line);
        }
        catch
        {
            // 进程结束时管道会关掉。
        }
    }

    static void Accept(string line)
    {
        bool ready = false;
        bool crash = false;
        lock (Gate)
        {
            _log?.WriteLine(line);
            Ring.Enqueue(line);
            while (Ring.Count > 400)
                Ring.Dequeue();
            if (_state == "starting" && LogPaint.IsReady(line))
            {
                _state = "running";
                ready = true;
            }
            if (LogPaint.IsCrash(line))
            {
                _sawCrash = true;
                crash = true;
            }
        }
        if (ready)
            WriteInstance();
        if (crash)
            SaveCrash(line);
    }

    static void DrainInbox()
    {
        if (_session == null || _java == null)
            return;
        var inbox = Path.Combine(_session.PanelDir, "inbox");
        if (!Directory.Exists(inbox))
            return;
        string[] files;
        try
        {
            files = Directory.GetFiles(inbox, "*.txt");
        }
        catch
        {
            return;
        }
        Array.Sort(files, StringComparer.Ordinal);
        foreach (var file in files)
        {
            string command;
            try
            {
                command = File.ReadAllText(file).Trim();
                File.Delete(file);
            }
            catch
            {
                continue;
            }
            if (command.Length == 0)
                continue;
            if (command == "__KILL__")
            {
                Echo("> 强制结束");
                try { _java.Kill(true); } catch { }
                return;
            }
            try
            {
                _java.StandardInput.WriteLine(command);
                _java.StandardInput.Flush();
                Echo("> " + command);
                if (command.Equals("stop", StringComparison.OrdinalIgnoreCase))
                {
                    lock (Gate) _state = "stopping";
                    WriteInstance();
                }
            }
            catch (Exception ex)
            {
                Echo("> 指令发送失败: " + ex.Message);
            }
        }
    }

    static void Echo(string line) => Accept(line);

    static void SaveCrash(string trigger)
    {
        if (_session == null)
            return;
        string[] tail;
        lock (Gate)
        {
            if (_crashClaimed)
                return;
            _crashClaimed = true;
            tail = Ring.ToArray();
        }
        string folder;
        try
        {
            folder = CrashArchive.Create(
                _session.CrashDir,
                _session.ServerName,
                _session.WindowTitle,
                _session.Endpoints,
                trigger,
                tail,
                _session.JavaArgs);
            CrashArchive.CopyNewReports(folder, _session.ServerDir, _startedAt);
        }
        catch
        {
            lock (Gate)
                _crashClaimed = false;
            return;
        }
        lock (Gate)
        {
            _crashFolder = folder;
            WriteInstanceLocked();
        }
    }

    static void WriteInstance()
    {
        lock (Gate)
            WriteInstanceLocked();
    }

    static void WriteInstanceLocked()
    {
        if (_session == null)
            return;
        JsonFiles.Write(Path.Combine(_session.PanelDir, "instance.json"), new InstanceState
        {
            HostPid = Environment.ProcessId,
            JavaPid = _java?.Id ?? 0,
            WindowTitle = _session.WindowTitle,
            State = _state,
            ExitCode = _exitCode,
            StartedAt = _startedAt,
            Endpoints = _session.Endpoints,
            CrashLog = _crashFolder,
        });
    }
}

internal static class LogViewer
{
    public static int Run(string sessionPath)
    {
        var session = JsonFiles.Read<ViewerSession>(sessionPath);
        if (session == null)
            return 2;
        Win32.AllocConsole();
        Win32.SetConsoleOutputCP(65001);
        Win32.SetConsoleCP(65001);
        Win32.SetConsoleTitle(session.WindowTitle);
        var utf8 = new UTF8Encoding(false);
        Console.OutputEncoding = utf8;
        Console.InputEncoding = utf8;
        try
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
        }
        catch
        {
            // 控制台编码设不上时仍继续输出。
        }
        try
        {
            if (Console.BufferHeight < 8000)
                Console.BufferHeight = 8000;
        }
        catch
        {
            // 某些宿主不允许改缓冲区。
        }

        var serverDir = Path.GetDirectoryName(Path.GetDirectoryName(session.InstancePath));
        if (!string.IsNullOrEmpty(serverDir))
        {
            JsonFiles.Write(PanelPaths.ViewerMark(serverDir), new ViewerMark
            {
                Pid = Environment.ProcessId,
                WindowTitle = session.WindowTitle,
            });
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("日志窗口: " + session.WindowTitle);
        Console.WriteLine("关闭本窗口不会停止服务器。停止请回到管理器。");
        Console.ResetColor();
        Console.WriteLine();

        var fromEnd = File.Exists(session.LogPath) && new FileInfo(session.LogPath).Length > 180_000;
        var tail = new LogTail(session.LogPath, fromEnd);
        var idleSince = DateTime.UtcNow;
        while (true)
        {
            var lines = tail.ReadNew();
            if (lines.Count > 0)
                idleSince = DateTime.UtcNow;
            foreach (var line in lines)
                WriteColored(line);

            var hostAlive = ProcUtil.Alive(session.HostPid, "McPanel");
            var instance = JsonFiles.Read<InstanceState>(session.InstancePath);
            var finished = instance != null && instance.State is "exited" or "crashed";
            if (!hostAlive && (finished || (DateTime.UtcNow - idleSince).TotalSeconds > 1.5))
            {
                var extra = tail.ReadNew();
                foreach (var line in extra)
                    WriteColored(line);
                break;
            }
            Thread.Sleep(200);
        }

        var finalState = JsonFiles.Read<InstanceState>(session.InstancePath);
        Console.WriteLine();
        Console.ForegroundColor = finalState?.State == "crashed" ? ConsoleColor.Red : ConsoleColor.Green;
        Console.WriteLine(finalState?.State == "crashed" ? "服务器异常退出。崩溃记录已由管理器保存。" : "服务器已停止，或控制进程已结束。");
        Console.ResetColor();
        Console.WriteLine("按回车关闭此窗口。");
        Console.ReadLine();
        return 0;
    }

    static void WriteColored(string line)
    {
        Console.ForegroundColor = LogPaint.Kind(line) switch
        {
            LineKind.Error => ConsoleColor.Red,
            LineKind.Warn => ConsoleColor.Yellow,
            LineKind.Ok => ConsoleColor.Green,
            LineKind.Banner => ConsoleColor.Cyan,
            _ => ConsoleColor.Gray,
        };
        Console.WriteLine(line);
        Console.ResetColor();
    }
}
