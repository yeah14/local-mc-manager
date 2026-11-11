using System.IO.Compression;
using System.Text;

namespace McPanel;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var host = Arg(args, "--host");
        if (host != null)
            return ServerHost.Run(host);
        var view = Arg(args, "--view");
        if (view != null)
            return LogViewer.Run(view);
        if (args.Any(arg => arg is "--scan" or "--selftest"))
        {
            AttachParent();
            return args.Contains("--selftest") ? SelfTest.Run() : PrintScan();
        }

        return RunWindow(args.Contains("--client"));
    }

    static int RunWindow(bool client)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, eventArgs) => Report(eventArgs.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception ex)
                Report(ex);
        };
        Application.Run(client ? new RemoteClientForm() : new MainForm());
        return 0;
    }

    static void AttachParent()
    {
        if (!Win32.AttachConsole(-1))
            return;
        var utf8 = new UTF8Encoding(false);
        try
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
        }
        catch
        {
            // 父进程没有控制台时，自测结果仍会写到文件。
        }
    }

    static int PrintScan()
    {
        var root = RootLocator.Find();
        if (root == null)
        {
            Console.WriteLine("NO_ROOT");
            return 1;
        }
        var scan = ServerScanner.Scan(root);
        Console.WriteLine(root);
        foreach (var server in scan.Servers)
            Console.WriteLine($"{server.Name}\t{server.Port}\t{server.LevelName}\t{server.VersionLabel}\t{server.Motd}");
        foreach (var name in scan.Unrecognized)
            Console.WriteLine("UNRECOGNIZED\t" + name);
        return 0;
    }

    static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
                return args[i + 1];
        }
        return null;
    }

    static void Report(Exception ex)
    {
        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.log"), ex.ToString());
        }
        catch
        {
            // 日志写失败时仍弹出错误。
        }
        MessageBox.Show(ex.ToString(), "MC 服务端管理器");
    }
}

internal static class SelfTest
{
    public static int Run()
    {
        var failures = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition)
                failures.Add(message);
            Console.WriteLine((condition ? "OK  " : "FAIL ") + message);
        }

        try
        {
            Check(PropertiesFile.Unescape(@"\u9b54\u5973") == "魔女", "unicode 转义");
            var root = RootLocator.Walk();
            Check(root != null, "向上找到服务端目录");
            if (root != null)
            {
                var scan = ServerScanner.Scan(root);
                Check(scan.Servers.Count >= 3, "至少识别 3 个服务端，实际 " + scan.Servers.Count);
                Check(scan.Servers.All(server => server.Name != "McPanel"), "管理器目录不会被当成服务端");
                Check(scan.Servers.Any(server => server.Name.Contains("神道传说", StringComparison.Ordinal)), "识别嵌套的神道传说服务端");
                foreach (var server in scan.Servers.Where(server => server.Name is "gtl" or "诡厄：使徒-服务端" || server.Name.Contains("魔女", StringComparison.Ordinal)))
                {
                    var props = PropertiesFile.Read(Path.Combine(server.Directory, "server.properties"));
                    var expectedPort = props.TryGetValue("server-port", out var portText) && int.TryParse(portText, out var portNumber) ? portNumber : 25565;
                    Check(server.Port == expectedPort, server.Name + " 端口 " + server.Port);
                    Check(server.JavaArgs != null && server.JavaArgs.Contains("nogui", StringComparison.OrdinalIgnoreCase), server.Name + " 含 nogui");
                    Check(server.JavaArgs != null && server.JavaArgs.Contains("file.encoding=UTF-8", StringComparison.Ordinal), server.Name + " 指定 UTF-8");
                    var noguiCount = CountToken(server.JavaArgs ?? "", "nogui");
                    Check(noguiCount == 1, server.Name + " 的 nogui 只有一次，实际 " + noguiCount);
                }
                var witch = scan.Servers.FirstOrDefault(server => server.Name.Contains("魔女", StringComparison.Ordinal));
                Check(witch != null && witch.Motd.Contains("魔女", StringComparison.Ordinal), "魔女 MOTD 解码");
                Check(witch != null && witch.MemoryWarning == null, "已设置 -Xmx 的服不报警");
                var gtl = scan.Servers.FirstOrDefault(server => server.Name.Equals("gtl", StringComparison.OrdinalIgnoreCase));
                Check(gtl != null && gtl.VersionLabel.Contains("47.4.16", StringComparison.Ordinal), "gtl 版本 " + gtl?.VersionLabel);
                Check(gtl != null && gtl.MemoryWarning != null, "gtl 未设置 -Xmx 会提示");
            }

            Check(LaunchPlan.TryParseJavaLine("java @user_jvm_args.txt @libraries/net/minecraftforge/forge/1.20.1-47.4.16/win_args.txt %*", out var parsed), "解析 run.bat");
            Check(parsed.Contains("win_args.txt", StringComparison.Ordinal) && !parsed.Contains("%*", StringComparison.Ordinal), "去掉 %*");

            var temp = Path.Combine(Path.GetTempPath(), "mcpanel-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var server = Path.Combine(temp, "srv");
                Directory.CreateDirectory(Path.Combine(server, "world"));
                File.WriteAllText(Path.Combine(server, "world", "level.dat"), "old");
                File.WriteAllText(Path.Combine(server, "server.properties"), "server-port=25570\nlevel-name=world\nlevel-seed=111\n");
                File.WriteAllText(Path.Combine(server, "run.bat"), "java -jar server.jar %*\r\n");
                var scanTemp = ServerScanner.Scan(temp);
                Check(scanTemp.Servers.Count == 1 && scanTemp.Servers[0].Port == 25570, "临时目录识别");

                var fresh = WorldService.CreateFreshWorld(server, "world", "world-2", "12345");
                Check(Directory.Exists(fresh) && Path.GetFileName(fresh) == "world-2", "创建新世界文件夹");
                Check(File.ReadAllText(Path.Combine(server, "world", "level.dat")) == "old", "旧世界内容还在");
                var switched = ServerPropertiesDocument.Load(Path.Combine(server, "server.properties"));
                Check(switched.Get("level-name") == "world-2", "配置改指向新世界");
                Check(switched.Get("level-seed") == "12345", "新世界种子已写入");
                Check(switched.Get("server-port") == "25570", "创建世界时保留其他配置");

                var source = Path.Combine(temp, "srcworld");
                Directory.CreateDirectory(source);
                File.WriteAllText(Path.Combine(source, "level.dat"), "newworld");
                var zip = Path.Combine(temp, "new.zip");
                ZipFile.CreateFromDirectory(source, zip, CompressionLevel.Fastest, true);
                var backup = WorldService.ReplaceFromZip(server, "world", zip, null);
                Check(File.ReadAllText(Path.Combine(server, "world", "level.dat")) == "newworld", "导入后世界内容");
                Check(backup != null && File.Exists(backup), "替换前自动备份");
                Check(WorldService.ListBackups(server).Count == 1, "备份列表");

                var slipZip = Path.Combine(temp, "slip.zip");
                using (var archive = ZipFile.Open(slipZip, ZipArchiveMode.Create))
                {
                    AddText(archive, "world/level.dat", "safe");
                    AddText(archive, "../evil.txt", "pwned");
                }
                var threw = false;
                try
                {
                    WorldService.ReplaceFromZip(server, "world", slipZip, null);
                }
                catch (InvalidOperationException)
                {
                    threw = true;
                }
                Check(threw, "拒绝压缩包路径穿越");
                Check(!File.Exists(Path.Combine(temp, "evil.txt")), "穿越文件没有写到目录外");

                var crash = CrashArchive.Create(
                    Path.Combine(temp, "crashes"),
                    "srv",
                    "[MC] srv | 端口 25570",
                    ["127.0.0.1:25570", "10.0.0.8:25570"],
                    "测试触发",
                    ["启动失败"],
                    "java -jar server.jar nogui");
                var summary = File.ReadAllText(Path.Combine(crash, "summary.log"));
                Check(summary.Contains("运行窗口: [MC] srv | 端口 25570", StringComparison.Ordinal), "崩溃记录包含窗口");
                Check(summary.Contains("10.0.0.8:25570", StringComparison.Ordinal), "崩溃记录包含 IP");

                var propsPath = Path.Combine(temp, "server.properties");
                File.WriteAllText(propsPath, "#keep me\r\nlevel-type=minecraft\\:normal\r\nmotd=\\u9b54\\u5973\r\nserver-port=25565\r\n", new UTF8Encoding(false));
                var properties = ServerPropertiesDocument.Load(propsPath);
                Check(properties.Get("level-type") == "minecraft:normal", "配置反转义冒号");
                Check(properties.Get("motd") == "魔女", "配置反转义中文");
                properties.Set("motd", "测试服");
                properties.Set("server-port", "25570");
                properties.Set("white-list", "true");
                properties.Save(propsPath);
                var saved = File.ReadAllText(propsPath);
                var reloaded = ServerPropertiesDocument.Load(propsPath);
                Check(reloaded.Get("motd") == "测试服", "保存后的简介");
                Check(reloaded.Get("server-port") == "25570", "保存后的端口");
                Check(reloaded.Get("white-list") == "true", "新增配置项");
                Check(reloaded.Get("level-type") == "minecraft:normal", "未修改的项保持可读");
                Check(saved.Contains("#keep me", StringComparison.Ordinal), "保留配置注释");
                Check(saved.Contains("minecraft\\:normal", StringComparison.Ordinal), "未修改的项保持原写法");
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { }
            }

            RemoteSelfCheck.Run(Check);
        }
        catch (Exception ex)
        {
            failures.Add(ex.ToString());
            Console.WriteLine(ex);
        }

        var report = failures.Count == 0 ? "SELFTEST PASS" : "SELFTEST FAIL\n" + string.Join("\n", failures);
        Console.WriteLine(report);
        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest-out.txt"), report);
        }
        catch
        {
            // 结果已经打到控制台。
        }
        return failures.Count == 0 ? 0 : 1;
    }

    static int CountToken(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += token.Length;
        }
        return count;
    }

    static void AddText(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
    }
}
