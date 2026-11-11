using System.IO.Compression;
using System.Text;

namespace McPanel;

internal static class WorldService
{
    public static string ManualBackupDir(string serverDir) => Path.Combine(serverDir, "backups", "manual");

    public static List<FileInfo> ListBackups(string serverDir)
    {
        var dir = ManualBackupDir(serverDir);
        if (!Directory.Exists(dir))
            return new List<FileInfo>();
        return new DirectoryInfo(dir).GetFiles("*.zip").OrderByDescending(file => file.LastWriteTime).ToList();
    }

    public static bool IsValidLevelName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
            return false;
        return name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    public static string WorldPath(string serverDir, string levelName)
    {
        if (!IsValidLevelName(levelName))
            throw new InvalidOperationException("世界名无效：" + levelName);
        return Path.Combine(serverDir, levelName);
    }

    public static string SuggestNewLevelName(string serverDir, string currentLevel)
    {
        var stem = string.IsNullOrWhiteSpace(currentLevel) ? "world" : currentLevel.Trim();
        for (var i = 2; i < 100; i++)
        {
            var candidate = stem + "-" + i;
            if (!Directory.Exists(Path.Combine(serverDir, candidate)))
                return candidate;
        }
        return stem + "-" + DateTime.Now.ToString("yyyyMMdd-HHmm");
    }

    public static string CreateFreshWorld(string serverDir, string currentLevel, string newName, string? seed)
    {
        newName = newName.Trim();
        if (!IsValidLevelName(newName))
            throw new InvalidOperationException("新世界文件夹名无效，不能包含 \\ / : * ? \" < > |。");
        if (newName.Equals(currentLevel, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("新文件夹不能和当前世界同名。当前世界会被保留。");

        var dest = WorldPath(serverDir, newName);
        var created = false;
        if (Directory.Exists(dest))
        {
            if (File.Exists(Path.Combine(dest, "level.dat")))
                throw new InvalidOperationException("已经有同名世界「" + newName + "」。旧存档还在，请换一个文件夹名。");
            if (Directory.EnumerateFileSystemEntries(dest).Any())
                throw new InvalidOperationException("文件夹「" + newName + "」已存在，而且不是空的。");
        }
        else
        {
            Directory.CreateDirectory(dest);
            created = true;
        }

        try
        {
            var propertiesPath = Path.Combine(serverDir, "server.properties");
            var document = ServerPropertiesDocument.Load(propertiesPath);
            document.Set("level-name", newName);
            if (!string.IsNullOrWhiteSpace(seed))
                document.Set("level-seed", seed.Trim());
            document.Save(propertiesPath);
        }
        catch
        {
            if (created)
            {
                try { Directory.Delete(dest); } catch { }
            }
            throw;
        }
        return dest;
    }

    public static bool IsWorldLocked(string worldPath)
    {
        var lockFile = Path.Combine(worldPath, "session.lock");
        if (!File.Exists(lockFile))
            return false;
        try
        {
            using var stream = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string? FindWorldRoot(string dir, string? preferredName = null)
    {
        if (File.Exists(Path.Combine(dir, "level.dat")))
            return dir;
        var hits = new List<string>();
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "level.dat", SearchOption.AllDirectories))
            {
                var parent = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(parent))
                    hits.Add(parent);
            }
        }
        if (preferredName != null)
        {
            var preferred = hits.FirstOrDefault(path =>
                Path.GetFileName(path).Equals(preferredName, StringComparison.OrdinalIgnoreCase));
            if (preferred != null)
                return preferred;
        }
        return hits
            .OrderBy(path => path.Count(ch => ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar))
            .FirstOrDefault();
    }

    public static bool ZipContainsLevelDat(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries.Any(entry => entry.Name.Equals("level.dat", StringComparison.OrdinalIgnoreCase));
    }

    public static string Backup(string serverDir, string levelName, IProgress<string>? progress)
    {
        var folders = WorldFolders(serverDir, levelName);
        if (folders.Count == 0)
            throw new InvalidOperationException("还没有世界文件夹「" + levelName + "」，无法备份。");

        var backupDir = ManualBackupDir(serverDir);
        Directory.CreateDirectory(backupDir);
        var zipPath = UniqueZip(backupDir, levelName);
        var added = 0;
        var skipped = 0;
        var reported = DateTime.UtcNow;
        try
        {
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            foreach (var folder in folders)
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    var entryName = Path.GetRelativePath(serverDir, file).Replace('\\', '/');
                    try
                    {
                        zip.CreateEntryFromFile(file, entryName, CompressionLevel.Fastest);
                        added++;
                    }
                    catch (IOException)
                    {
                        skipped++;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        skipped++;
                    }
                    if (added % 40 == 0 || (DateTime.UtcNow - reported).TotalMilliseconds > 250)
                    {
                        reported = DateTime.UtcNow;
                        progress?.Report(entryName);
                    }
                }
            }
        }
        catch
        {
            TryDelete(zipPath);
            throw;
        }

        if (added == 0)
        {
            TryDelete(zipPath);
            throw new InvalidOperationException("没有读到任何世界文件，备份失败。");
        }
        if (skipped > 0)
            progress?.Report($"完成，有 {skipped} 个文件被占用已跳过");
        else
            progress?.Report("完成");
        return zipPath;
    }

    public static string? ReplaceFromZip(string serverDir, string levelName, string zipPath, IProgress<string>? progress)
    {
        if (!ZipContainsLevelDat(zipPath))
            throw new InvalidOperationException("压缩包里没有 level.dat，这不是 Minecraft 世界存档。");
        var stage = PrepareStage(serverDir);
        try
        {
            var unpack = Path.Combine(stage, "unpack");
            Directory.CreateDirectory(unpack);
            ExtractZip(zipPath, unpack, progress);
            var root = FindWorldRoot(unpack, levelName);
            if (root == null)
                throw new InvalidOperationException("解压后没有找到 level.dat。");
            return SwapIn(serverDir, levelName, root, progress);
        }
        finally
        {
            CleanupStage(serverDir);
        }
    }

    public static string? ReplaceFromFolder(string serverDir, string levelName, string folder, IProgress<string>? progress)
    {
        var source = FindWorldRoot(folder, levelName);
        if (source == null)
            throw new InvalidOperationException("这个文件夹里没有 level.dat，不是 Minecraft 世界。");
        var dest = WorldPath(serverDir, levelName);
        if (PathsEqual(source, dest))
            throw new InvalidOperationException("不能把正在使用的世界文件夹导入到它自己。");
        PrepareStage(serverDir);
        try
        {
            var staged = Path.Combine(serverDir, ".mcpanel", "stage", "world");
            CopyDirectory(source, staged, progress);
            return SwapIn(serverDir, levelName, staged, progress);
        }
        finally
        {
            CleanupStage(serverDir);
        }
    }

    static string? SwapIn(string serverDir, string levelName, string worldRoot, IProgress<string>? progress)
    {
        var dest = WorldPath(serverDir, levelName);
        if (IsWorldLocked(dest))
            throw new InvalidOperationException("世界正被占用（session.lock）。请先停止服务端。");

        string? backup = null;
        if (Directory.Exists(dest))
        {
            progress?.Report("正在备份当前世界");
            backup = Backup(serverDir, levelName, progress);
            var aside = Path.Combine(serverDir, ".mcpanel", "aside-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(Path.GetDirectoryName(aside)!);
            Directory.Move(dest, aside);
            try
            {
                Directory.Move(worldRoot, dest);
            }
            catch
            {
                if (!Directory.Exists(dest) && Directory.Exists(aside))
                    Directory.Move(aside, dest);
                throw;
            }
            try
            {
                Directory.Delete(aside, true);
            }
            catch
            {
                // 旧目录删不掉时，备份压缩包里仍有一份。
            }
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            Directory.Move(worldRoot, dest);
        }

        progress?.Report("世界已替换");
        return backup;
    }

    static string PrepareStage(string serverDir)
    {
        var stage = Path.Combine(serverDir, ".mcpanel", "stage");
        if (Directory.Exists(stage))
            Directory.Delete(stage, true);
        Directory.CreateDirectory(stage);
        return stage;
    }

    public static void CleanupStage(string serverDir)
    {
        var stage = Path.Combine(serverDir, ".mcpanel", "stage");
        if (!Directory.Exists(stage))
            return;
        try
        {
            Directory.Delete(stage, true);
        }
        catch
        {
            // 临时目录留着不影响下次导入。
        }
    }

    static void ExtractZip(string zipPath, string unpack, IProgress<string>? progress)
    {
        var root = Path.GetFullPath(unpack);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
            root += Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipPath);
        var count = 0;
        var reported = DateTime.UtcNow;
        foreach (var entry in archive.Entries)
        {
            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('/', Path.AltDirectorySeparatorChar);
            if (relative.Contains(':') || Path.IsPathRooted(relative))
                throw new InvalidOperationException("压缩包包含非法路径：" + entry.FullName);
            var destination = Path.GetFullPath(Path.Combine(unpack, relative));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("压缩包包含非法路径：" + entry.FullName);
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, true);
            count++;
            if (count % 40 == 0 || (DateTime.UtcNow - reported).TotalMilliseconds > 250)
            {
                reported = DateTime.UtcNow;
                progress?.Report(entry.FullName);
            }
        }
    }

    static void CopyDirectory(string source, string dest, IProgress<string>? progress)
    {
        Directory.CreateDirectory(dest);
        var count = 0;
        var reported = DateTime.UtcNow;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(dest, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
            count++;
            if (count % 40 == 0 || (DateTime.UtcNow - reported).TotalMilliseconds > 250)
            {
                reported = DateTime.UtcNow;
                progress?.Report(relative);
            }
        }
    }

    static List<string> WorldFolders(string serverDir, string levelName)
    {
        var list = new List<string>();
        foreach (var name in new[] { levelName, levelName + "_nether", levelName + "_the_end" })
        {
            var path = Path.Combine(serverDir, name);
            if (Directory.Exists(path))
                list.Add(path);
        }
        return list;
    }

    static string UniqueZip(string backupDir, string levelName)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(backupDir, levelName + "-" + stamp + ".zip");
        var index = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(backupDir, levelName + "-" + stamp + "-" + index + ".zip");
            index++;
        }
        return path;
    }

    static bool PathsEqual(string left, string right)
    {
        return string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 残留的半成品压缩包可以手动删。
        }
    }
}

internal static class CrashArchive
{
    public static string Create(string crashDir, string serverName, string windowTitle, IReadOnlyList<string> endpoints, string trigger, IReadOnlyList<string> tail, string? javaArgs)
    {
        var folder = Path.Combine(crashDir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(folder);
        var sb = new StringBuilder();
        sb.AppendLine("服务器: " + serverName);
        sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("运行窗口: " + windowTitle);
        sb.AppendLine("本机地址: " + (endpoints.Count == 0 ? "（无）" : string.Join(", ", endpoints)));
        sb.AppendLine("触发: " + trigger);
        if (!string.IsNullOrWhiteSpace(javaArgs))
            sb.AppendLine("启动参数: " + javaArgs);
        sb.AppendLine("-------- 日志节选 --------");
        foreach (var line in tail)
            sb.AppendLine(line);
        File.WriteAllText(Path.Combine(folder, "summary.log"), sb.ToString(), new UTF8Encoding(true));
        return folder;
    }

    public static void MarkExit(string folder, int code)
    {
        var summary = Path.Combine(folder, "summary.log");
        File.AppendAllText(summary, Environment.NewLine + "退出码: " + code + Environment.NewLine, new UTF8Encoding(false));
    }

    public static int CopyNewReports(string folder, string serverDir, DateTime startedAt)
    {
        var source = Path.Combine(serverDir, "crash-reports");
        if (!Directory.Exists(source))
            return 0;
        var copied = 0;
        foreach (var file in Directory.GetFiles(source))
        {
            if (File.GetLastWriteTime(file) < startedAt.AddMinutes(-1))
                continue;
            var dest = Path.Combine(folder, Path.GetFileName(file));
            try
            {
                File.Copy(file, dest, true);
                copied++;
            }
            catch
            {
                // 崩溃报告被占用时，日志节选仍然保留。
            }
        }
        return copied;
    }
}

internal enum LineKind
{
    Normal,
    Banner,
    Warn,
    Error,
    Ok,
}

internal static class LogPaint
{
    public static LineKind Kind(string line)
    {
        if (line.Contains("/WARN", StringComparison.OrdinalIgnoreCase) || line.Contains("[WARN]", StringComparison.OrdinalIgnoreCase))
            return LineKind.Warn;
        if (line.Contains("/FATAL", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[FATAL]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("ERROR", StringComparison.Ordinal) ||
            line.Contains("Exception", StringComparison.Ordinal))
            return LineKind.Error;
        if (line.Contains("Done (", StringComparison.Ordinal))
            return LineKind.Ok;
        if (line.StartsWith("====", StringComparison.Ordinal) ||
            line.StartsWith("服务器:", StringComparison.Ordinal) ||
            line.StartsWith("启动时间:", StringComparison.Ordinal) ||
            line.StartsWith("本机 IP", StringComparison.Ordinal) ||
            line.StartsWith("运行窗口:", StringComparison.Ordinal) ||
            line.StartsWith("Java:", StringComparison.Ordinal) ||
            line.StartsWith("关闭日志窗口", StringComparison.Ordinal) ||
            line.StartsWith("> ", StringComparison.Ordinal))
            return LineKind.Banner;
        return LineKind.Normal;
    }

    public static bool IsCrash(string line)
    {
        return line.Contains("/FATAL", StringComparison.OrdinalIgnoreCase)
            || line.Contains("[FATAL]", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Crash report", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Preparing crash report", StringComparison.OrdinalIgnoreCase)
            || line.Contains("A fatal error has", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Failed to start the minecraft server", StringComparison.OrdinalIgnoreCase)
            || line.Contains("This crash report has been saved", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsReady(string line)
    {
        return line.Contains("Done (", StringComparison.Ordinal) ||
               line.Contains("For help, type \"help\"", StringComparison.Ordinal);
    }
}

internal sealed class LogTail
{
    readonly string _path;
    long _position;
    readonly List<byte> _pending = new();
    bool _skipPartial;
    public bool Restarted { get; private set; }

    public LogTail(string path, bool fromEnd)
    {
        _path = path;
        if (!File.Exists(path))
            return;
        var length = new FileInfo(path).Length;
        if (fromEnd && length > 180_000)
        {
            _position = length - 180_000;
            _skipPartial = true;
        }
    }

    public List<string> ReadNew()
    {
        Restarted = false;
        var lines = new List<string>();
        if (!File.Exists(_path))
            return lines;
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < _position)
            {
                _position = 0;
                _pending.Clear();
                _skipPartial = false;
                Restarted = true;
            }
            if (stream.Length == _position)
                return lines;
            stream.Position = _position;
            var buffer = new byte[64 * 1024];
            var reads = 0;
            while (stream.Position < stream.Length && reads++ < 30)
            {
                var count = stream.Read(buffer, 0, buffer.Length);
                if (count <= 0)
                    break;
                _position += count;
                for (var i = 0; i < count; i++)
                    _pending.Add(buffer[i]);
                Extract(lines);
                if (lines.Count >= 2500)
                    break;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return lines;
    }

    void Extract(List<string> lines)
    {
        var start = 0;
        for (var i = 0; i < _pending.Count; i++)
        {
            if (_pending[i] != (byte)'\n')
                continue;
            var length = i - start;
            if (length > 0 && _pending[i - 1] == (byte)'\r')
                length--;
            if (_skipPartial)
                _skipPartial = false;
            else if (length > 0)
            {
                var slice = new byte[length];
                _pending.CopyTo(start, slice, 0, length);
                lines.Add(Encoding.UTF8.GetString(slice));
            }
            else if (length == 0)
            {
                lines.Add("");
            }
            start = i + 1;
        }
        if (start > 0)
            _pending.RemoveRange(0, start);
        if (_pending.Count > 2_000_000)
            _pending.Clear();
    }
}
