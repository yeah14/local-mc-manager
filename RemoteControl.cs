using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace McPanel;

internal sealed class RemoteSettings
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 28765;
    public int CpuThreshold { get; set; } = 40;
    public int MemoryThreshold { get; set; } = 70;
    public string PasswordSalt { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool HasPassword => PasswordHash.Length > 0;
}

internal sealed class RemoteClientSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 28765;
    public string Password { get; set; } = "";
}

internal sealed class RemoteServerView
{
    public string Name { get; set; } = "";
    public int Port { get; set; }
    public string State { get; set; } = "";
    public List<string> Endpoints { get; set; } = new();
    public bool CanStart { get; set; }
    public string Block { get; set; } = "";
}

internal sealed class RemoteListResponse
{
    public List<RemoteServerView> Servers { get; set; } = new();
    public bool PrivilegeEnabled { get; set; }
}

internal sealed class RemoteActionBody
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string World { get; set; } = "";
    public string Seed { get; set; } = "";
    public string File { get; set; } = "";
    public string Id { get; set; } = "";
    public bool AllowRunning { get; set; }
    public bool ConfirmPort { get; set; }
    public bool ConfirmLevel { get; set; }
    public List<RemoteConfigChange> Changes { get; set; } = new();
}

internal sealed class RemoteConfigChange
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

internal sealed class RemoteOutcome
{
    public string Status { get; set; } = "failed";
    public string Message { get; set; } = "";
    public object? Data { get; set; }

    public static RemoteOutcome Done(string message, object? data = null) => new() { Status = "ok", Message = message, Data = data };
    public static RemoteOutcome Fail(string message) => new() { Status = "failed", Message = message };
    public static RemoteOutcome Ask(string message) => new() { Status = "confirm", Message = message };
}

internal interface IRemoteDesk
{
    RemoteOutcome RemoteStop(string name);
    RemoteOutcome RemoteKill(string name);
    RemoteOutcome RemoteCommand(string name, string command);
    RemoteOutcome RemoteLog(string name);
    RemoteOutcome RemoteBackup(string name, bool allowRunning);
    RemoteOutcome RemoteCreateWorld(string name, string world, string seed);
    RemoteOutcome RemoteBackups(string name);
    RemoteOutcome RemoteRestore(string name, string file);
    RemoteOutcome RemoteConfig(string name);
    RemoteOutcome RemoteSaveConfig(string name, IReadOnlyList<RemoteConfigChange> changes, bool confirmPort, bool confirmLevel);
    RemoteOutcome RemoteCrashes(string name);
    RemoteOutcome RemoteCrash(string name, string id);
}

internal sealed class RemoteStartBody
{
    public string Name { get; set; } = "";
}

internal sealed class RemoteStartResponse
{
    public string Id { get; set; } = "";
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
}

internal static class RemoteSettingsStore
{
    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "McPanel");
    static string HostPath => Path.Combine(Dir, "remote.json");
    static string ClientPath => Path.Combine(Dir, "client.json");

    public static RemoteSettings LoadHost()
    {
        var settings = JsonFiles.Read<RemoteSettings>(HostPath) ?? new RemoteSettings();
        if (settings.Port is < 1024 or > 65535)
            settings.Port = 28765;
        settings.CpuThreshold = Math.Clamp(settings.CpuThreshold <= 0 ? 40 : settings.CpuThreshold, 1, 100);
        settings.MemoryThreshold = Math.Clamp(settings.MemoryThreshold <= 0 ? 70 : settings.MemoryThreshold, 1, 100);
        return settings;
    }

    public static void SaveHost(RemoteSettings settings) => JsonFiles.Write(HostPath, settings);

    public static RemoteClientSettings LoadClient() => JsonFiles.Read<RemoteClientSettings>(ClientPath) ?? new RemoteClientSettings();

    public static void SaveClient(RemoteClientSettings settings) => JsonFiles.Write(ClientPath, settings);
}

internal static class RemotePassword
{
    public static (string Salt, string Hash) Create(string password)
    {
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        return (salt, Hash(salt, password));
    }

    public static bool Verify(string password, string salt, string hash)
    {
        if (salt.Length == 0 || hash.Length == 0)
            return false;
        try
        {
            var actual = Convert.FromHexString(Hash(salt, password));
            var expected = Convert.FromHexString(hash);
            return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    static string Hash(string salt, string password)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(salt + "\n" + password));
        return Convert.ToHexString(bytes);
    }
}

internal static class RemoteGate
{
    public static bool Below(double cpu, double memory, int cpuLimit, int memoryLimit)
    {
        return cpu < cpuLimit && memory < memoryLimit;
    }
}

internal readonly record struct LoadSample(double Cpu, double Memory);

internal static class MachineLoad
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    public static LoadSample Sample(int milliseconds)
    {
        if (!GetSystemTimes(out var idle1, out var kernel1, out var user1))
            throw new InvalidOperationException("读不到 CPU 占用。");
        Thread.Sleep(Math.Clamp(milliseconds, 200, 2000));
        if (!GetSystemTimes(out var idle2, out var kernel2, out var user2))
            throw new InvalidOperationException("读不到 CPU 占用。");
        var idle = idle2 - idle1;
        var total = kernel2 - kernel1 + user2 - user1;
        var cpu = total == 0 ? 0 : (total - idle) * 100d / total;
        if (cpu < 0)
            cpu = 0;
        if (cpu > 100)
            cpu = 100;

        var memory = new MemoryStatus { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory))
            throw new InvalidOperationException("读不到内存占用。");
        return new LoadSample(cpu, memory.MemoryLoad);
    }
}

internal sealed class RemoteReply
{
    public int StatusCode { get; init; } = 200;
    public string Json { get; init; } = "{}";
    public string ContentType { get; init; } = "application/json; charset=utf-8";

    public static RemoteReply Of(object value, int status = 200) =>
        new() { StatusCode = status, Json = JsonSerializer.Serialize(value, JsonFiles.Opt) };

    public static RemoteReply Page(string html) =>
        new() { Json = html, ContentType = "text/html; charset=utf-8" };
}

internal interface IRemoteHandler
{
    RemoteReply Handle(string method, string path, string body, string remoteIp);
}

/// <summary>
/// 自己用套接字收 HTTP，避免 HttpListener 在 Windows 上要求管理员登记 URL。
/// </summary>
internal sealed class RemoteHttpServer : IDisposable
{
    readonly string _salt;
    readonly string _hash;
    readonly IRemoteHandler _handler;
    readonly CancellationTokenSource _cancel = new();
    readonly SemaphoreSlim _slots = new(8);
    TcpListener? _listener;
    Thread? _thread;

    public RemoteHttpServer(IPAddress address, int port, string salt, string hash, IRemoteHandler handler)
    {
        _salt = salt;
        _hash = hash;
        _handler = handler;
        var listener = new TcpListener(address, port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException("端口 " + port + " 无法监听：" + ex.Message);
        }
        _listener = listener;
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public void Start()
    {
        _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "mcpanel-remote" };
        _thread.Start();
    }

    public void Dispose()
    {
        _cancel.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    void AcceptLoop()
    {
        while (!_cancel.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = _listener!.AcceptTcpClient();
            }
            catch
            {
                break;
            }
            if (!_slots.Wait(0))
            {
                try { Write(client, RemoteReply.Of(new RemoteStartResponse { Status = "failed", Message = "主机正忙，请稍后再试。" }, 503)); } catch { }
                client.Dispose();
                continue;
            }
            Task.Run(() =>
            {
                try { Serve(client); }
                catch { }
                finally
                {
                    _slots.Release();
                    client.Dispose();
                }
            });
        }
    }

    void Serve(TcpClient client)
    {
        client.ReceiveTimeout = 8000;
        client.SendTimeout = 600000;
        using var stream = client.GetStream();
        if (!TryRead(stream, out var method, out var path, out var headers, out var body))
        {
            Write(stream, RemoteReply.Of(new RemoteStartResponse { Status = "failed", Message = "请求不完整。" }, 400));
            return;
        }
        if (method == "GET" && path is "/" or "/index.html")
        {
            Write(stream, RemoteReply.Page(RemotePage.Html));
            return;
        }
        headers.TryGetValue("x-token", out var token);
        var password = DecodeToken(token);
        var hasPassword = _hash.Length > 0;
        var supplied = password.Length > 0;
        var admin = hasPassword && RemotePassword.Verify(password, _salt, _hash);
        if (supplied && hasPassword && !admin)
        {
            Thread.Sleep(300);
            Write(stream, RemoteReply.Of(new RemoteStartResponse { Status = "failed", Message = "口令不对。" }, 401));
            return;
        }
        if (!IsPublic(method, path) && !admin)
        {
            var message = hasPassword ? "需要口令。" : "主机没有设置口令，不接受网页管理。";
            Write(stream, RemoteReply.Of(new RemoteStartResponse { Status = "failed", Message = message }, hasPassword ? 401 : 403));
            return;
        }
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "";
        var reply = _handler.Handle(method, path, body, remote);
        Write(stream, reply);
    }

    static bool TryRead(NetworkStream stream, out string method, out string path, out Dictionary<string, string> headers, out string body)
    {
        method = "";
        path = "";
        headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        body = "";
        var data = new MemoryStream();
        var buffer = new byte[2048];
        var split = -1;
        while (data.Length < 32768 && split < 0)
        {
            int count;
            try { count = stream.Read(buffer, 0, buffer.Length); }
            catch (IOException) { return false; }
            if (count <= 0)
                return false;
            data.Write(buffer, 0, count);
            split = Find(data.GetBuffer(), (int)data.Length, "\r\n\r\n"u8);
        }
        if (split < 0)
            return false;
        var headerText = Encoding.ASCII.GetString(data.GetBuffer(), 0, split);
        var lines = headerText.Split("\r\n");
        if (lines.Length == 0)
            return false;
        var parts = lines[0].Split(' ');
        if (parts.Length < 2)
            return false;
        method = parts[0].ToUpperInvariant();
        path = parts[1];
        var query = path.IndexOf('?');
        if (query >= 0)
            path = path[..query];
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0)
                continue;
            headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }
        var bodyStart = split + 4;
        var have = (int)data.Length - bodyStart;
        var length = 0;
        if (headers.TryGetValue("content-length", out var lengthText))
            int.TryParse(lengthText, out length);
        if (length < 0 || length > 262144)
            return false;
        var payload = new byte[length];
        if (have > 0)
            Buffer.BlockCopy(data.GetBuffer(), bodyStart, payload, 0, Math.Min(have, length));
        var filled = Math.Min(have, length);
        while (filled < length)
        {
            int count;
            try { count = stream.Read(payload, filled, length - filled); }
            catch (IOException) { return false; }
            if (count <= 0)
                return false;
            filled += count;
        }
        body = Encoding.UTF8.GetString(payload);
        return true;
    }

    static bool IsPublic(string method, string path)
    {
        if (method == "GET" && path == "/api/servers")
            return true;
        if (method == "POST" && path == "/api/start")
            return true;
        return method == "GET" && path.StartsWith("/api/requests/", StringComparison.Ordinal);
    }

    static string DecodeToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return "";
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(token));
        }
        catch (FormatException)
        {
            return "";
        }
    }

    static int Find(byte[] data, int length, ReadOnlySpan<byte> pattern)
    {
        for (var i = 0; i <= length - pattern.Length; i++)
        {
            var match = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return i;
        }
        return -1;
    }

    static void Write(TcpClient client, RemoteReply reply)
    {
        using var stream = client.GetStream();
        Write(stream, reply);
    }

    static void Write(NetworkStream stream, RemoteReply reply)
    {
        var body = Encoding.UTF8.GetBytes(reply.Json);
        var reason = reply.StatusCode switch
        {
            200 => "OK",
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            503 => "Unavailable",
            _ => "Error",
        };
        var header = "HTTP/1.1 " + reply.StatusCode + " " + reason + "\r\n"
            + "Content-Type: " + reply.ContentType + "\r\n"
            + "Content-Length: " + body.Length + "\r\n"
            + "Connection: close\r\n\r\n";
        var head = Encoding.ASCII.GetBytes(header);
        stream.Write(head);
        stream.Write(body);
        stream.Flush();
    }
}

internal static class RemoteApi
{
    static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(25),
    };

    public static Task<RemoteListResponse> ServersAsync(string host, int port, string token, CancellationToken cancel) =>
        SendAsync<RemoteListResponse>(Create(host, port, "/api/servers", token), cancel);

    public static Task<RemoteStartResponse> StartAsync(string host, int port, string token, string name, CancellationToken cancel)
    {
        var request = Create(host, port, "/api/start", token);
        request.Method = HttpMethod.Post;
        request.Content = new StringContent(JsonSerializer.Serialize(new RemoteStartBody { Name = name }, JsonFiles.Opt), Encoding.UTF8, "application/json");
        return SendAsync<RemoteStartResponse>(request, cancel);
    }

    public static Task<RemoteStartResponse> RequestAsync(string host, int port, string token, string id, CancellationToken cancel) =>
        SendAsync<RemoteStartResponse>(Create(host, port, "/api/requests/" + id, token), cancel);

    static HttpRequestMessage Create(string host, int port, string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Build(host, port, path));
        request.Headers.TryAddWithoutValidation("X-Token", Convert.ToBase64String(Encoding.UTF8.GetBytes(token)));
        request.Headers.ExpectContinue = false;
        request.Headers.ConnectionClose = true;
        return request;
    }

    static Uri Build(string host, int port, string path)
    {
        host = host.Trim();
        if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            host = host["http://".Length..];
        host = host.Trim().TrimEnd('/');
        if (host.Contains(':') && !host.StartsWith('['))
            host = "[" + host + "]";
        return new Uri("http://" + host + ":" + port + path);
    }

    static async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancel)
    {
        try
        {
            using var response = await Http.SendAsync(request, cancel);
            var text = await response.Content.ReadAsStringAsync(cancel);
            if (!response.IsSuccessStatusCode)
            {
                var message = "主机返回 " + (int)response.StatusCode;
                try
                {
                    var parsed = JsonSerializer.Deserialize<RemoteStartResponse>(text, JsonFiles.Opt);
                    if (!string.IsNullOrWhiteSpace(parsed?.Message))
                        message = parsed.Message;
                }
                catch
                {
                    // 用状态码说明即可。
                }
                throw new InvalidOperationException(message);
            }
            return JsonSerializer.Deserialize<T>(text, JsonFiles.Opt) ?? throw new InvalidOperationException("主机返回了空内容。");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException("主机没有在时限内回应。");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("连不上主机：" + ex.Message);
        }
    }
}

internal enum RemoteDecision
{
    Deny,
    Allow,
    Timeout,
}

sealed class PendingStart
{
    public string Id = "";
    public string Name = "";
    public string Remote = "";
    public double Cpu;
    public double Memory;
    public int CpuLimit;
    public int MemoryLimit;
    public string Status = "pending";
    public string Message = "等待主机确认";
    public DateTime Created = DateTime.Now;
}

internal sealed class RemoteHostService : IRemoteHandler, IDisposable
{
    readonly Control _ui;
    readonly Func<List<RemoteServerView>> _list;
    readonly Func<string, string?> _start;
    readonly IRemoteDesk _desk;
    readonly Dictionary<string, PendingStart> _pending = new(StringComparer.Ordinal);
    readonly Queue<string> _dialogs = new();
    readonly object _gate = new();
    RemoteHttpServer? _http;
    RemoteSettings _settings = new();
    bool _showing;
    bool _disposed;

    public RemoteHostService(Control ui, Func<List<RemoteServerView>> list, Func<string, string?> start, IRemoteDesk desk)
    {
        _ui = ui;
        _list = list;
        _start = start;
        _desk = desk;
    }

    public bool Listening => _http != null && !_disposed;
    public int Port => _http?.Port ?? _settings.Port;

    public void Start(RemoteSettings settings)
    {
        _settings = settings;
        _http = new RemoteHttpServer(IPAddress.Any, settings.Port, settings.PasswordSalt, settings.PasswordHash, this);
        _http.Start();
    }

    public void Dispose()
    {
        _disposed = true;
        _http?.Dispose();
        _http = null;
    }

    public RemoteReply Handle(string method, string path, string body, string remoteIp)
    {
        try
        {
            if (method == "GET" && path == "/api/servers")
                return RemoteReply.Of(new RemoteListResponse
                {
                    Servers = OnUi(_list) ?? new List<RemoteServerView>(),
                    PrivilegeEnabled = _settings.HasPassword,
                });
            if (method == "POST" && path == "/api/start")
                return Start(body, remoteIp);
            if (method == "POST" && path == "/api/auth")
                return RemoteReply.Of(new RemoteStartResponse { Status = "ok", Message = "已获得管理权限。" });
            if (method == "POST")
            {
                var action = ReadAction(body);
                if (action == null)
                    return RemoteReply.Of(new RemoteStartResponse { Status = "failed", Message = "请求内容无效。" }, 400);
                var outcome = path switch
                {
                    "/api/stop" => _desk.RemoteStop(action.Name),
                    "/api/kill" => _desk.RemoteKill(action.Name),
                    "/api/command" => _desk.RemoteCommand(action.Name, action.Command),
                    "/api/log" => _desk.RemoteLog(action.Name),
                    "/api/backup" => _desk.RemoteBackup(action.Name, action.AllowRunning),
                    "/api/world" => _desk.RemoteCreateWorld(action.Name, action.World, action.Seed),
                    "/api/backups" => _desk.RemoteBackups(action.Name),
                    "/api/restore" => _desk.RemoteRestore(action.Name, action.File),
                    "/api/config" => _desk.RemoteConfig(action.Name),
                    "/api/config/save" => _desk.RemoteSaveConfig(action.Name, action.Changes, action.ConfirmPort, action.ConfirmLevel),
                    "/api/crashes" => _desk.RemoteCrashes(action.Name),
                    "/api/crash" => _desk.RemoteCrash(action.Name, action.Id),
                    _ => null,
                };
                if (outcome != null)
                    return RemoteReply.Of(outcome);
            }
            if (method == "GET" && path.StartsWith("/api/requests/", StringComparison.Ordinal))
            {
                var id = path["/api/requests/".Length..];
                if (id.Length == 0 || id.Length > 64 || !id.All(char.IsAsciiLetterOrDigit))
                    return RemoteReply.Of(new RemoteStartResponse { Status = "failed", Message = "没有这条请求。" }, 404);
                lock (_gate)
                {
                    if (!_pending.TryGetValue(id, out var pending))
                        return RemoteReply.Of(new RemoteStartResponse { Status = "failed", Message = "没有这条请求。" }, 404);
                    return RemoteReply.Of(new RemoteStartResponse { Id = pending.Id, Status = pending.Status, Message = pending.Message });
                }
            }
            return RemoteReply.Of(new RemoteStartResponse { Status = "failed", Message = "没有这个接口。" }, 404);
        }
        catch (Exception ex)
        {
            return RemoteReply.Of(new RemoteStartResponse { Status = "failed", Message = ex.Message }, 503);
        }
    }

    static RemoteActionBody? ReadAction(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<RemoteActionBody>(body, JsonFiles.Opt) ?? new RemoteActionBody();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    RemoteReply Start(string body, string remoteIp)
    {
        RemoteStartBody? request;
        try
        {
            request = JsonSerializer.Deserialize<RemoteStartBody>(body, JsonFiles.Opt);
        }
        catch (JsonException)
        {
            return RemoteReply.Of(new RemoteStartResponse { Status = "failed", Message = "请求内容无效。" }, 400);
        }
        var name = request?.Name?.Trim() ?? "";
        if (name.Length == 0 || name.Length > 120)
            return RemoteReply.Of(new RemoteStartResponse { Status = "failed", Message = "没有指定服务端。" }, 400);

        var servers = OnUi(_list) ?? new List<RemoteServerView>();
        var matches = servers.Where(item => item.Name == name).ToList();
        if (matches.Count == 0)
            return RemoteReply.Of(new RemoteStartResponse { Status = "failed", Message = "找不到这个服务端。" });
        if (matches.Count > 1)
            return RemoteReply.Of(new RemoteStartResponse { Status = "failed", Message = "有多个同名服务端，请在主机上处理。" });
        if (!matches[0].CanStart)
            return RemoteReply.Of(new RemoteStartResponse { Status = "failed", Message = string.IsNullOrWhiteSpace(matches[0].Block) ? "现在不能启动。" : matches[0].Block });

        LoadSample sample;
        try
        {
            sample = MachineLoad.Sample(1000);
        }
        catch (Exception ex)
        {
            sample = new LoadSample(100, 100);
            remoteIp += "（" + ex.Message + "）";
        }

        if (RemoteGate.Below(sample.Cpu, sample.Memory, _settings.CpuThreshold, _settings.MemoryThreshold))
        {
            var error = OnUi(() => _start(name));
            if (string.IsNullOrEmpty(error))
                return RemoteReply.Of(new RemoteStartResponse { Status = "started", Message = "已启动「" + name + "」。" });
            return RemoteReply.Of(new RemoteStartResponse { Status = "failed", Message = error });
        }

        return RemoteReply.Of(new RemoteStartResponse
        {
            Status = "failed",
            Message = "这台电脑正在使用（CPU " + sample.Cpu.ToString("0") + "%，内存 " + sample.Memory.ToString("0") + "%）。请稍后再试。",
        });
    }

    void Enqueue(string id)
    {
        if (_disposed)
            return;
        _dialogs.Enqueue(id);
        Pump();
    }

    void Pump()
    {
        if (_showing || _disposed)
            return;
        while (_dialogs.Count > 0)
        {
            var id = _dialogs.Dequeue();
            PendingStart? pending;
            lock (_gate)
                _pending.TryGetValue(id, out pending);
            if (pending == null || pending.Status != "pending")
                continue;
            _showing = true;
            try
            {
                System.Media.SystemSounds.Exclamation.Play();
                using var dialog = new RemoteConfirmForm(pending);
                dialog.ShowDialog(_ui);
                if (dialog.Decision == RemoteDecision.Allow)
                {
                    var error = _start(pending.Name);
                    lock (_gate)
                    {
                        pending.Status = string.IsNullOrEmpty(error) ? "started" : "failed";
                        pending.Message = string.IsNullOrEmpty(error) ? "主机已允许启动。" : error!;
                    }
                }
                else
                {
                    lock (_gate)
                    {
                        pending.Status = dialog.Decision == RemoteDecision.Timeout ? "expired" : "denied";
                        pending.Message = dialog.Decision == RemoteDecision.Timeout ? "主机没有确认，请求已取消。" : "主机拒绝了这次启动。";
                    }
                }
            }
            finally
            {
                _showing = false;
            }
        }
        var cutoff = DateTime.Now.AddMinutes(-10);
        lock (_gate)
        {
            foreach (var key in _pending.Where(item => item.Value.Created < cutoff).Select(item => item.Key).ToList())
                _pending.Remove(key);
        }
    }

    T? OnUi<T>(Func<T> work)
    {
        if (_ui.IsDisposed)
            throw new InvalidOperationException("主机界面已经关闭。");
        if (_ui.InvokeRequired)
            return (T?)_ui.Invoke(work);
        return work();
    }
}

internal static class RemoteSelfCheck
{
    public static void Run(Action<bool, string> check)
    {
        check(RemoteGate.Below(10, 20, 40, 70), "占用都低于阈值");
        check(!RemoteGate.Below(40, 20, 40, 70), "CPU 达到阈值需要确认");
        check(!RemoteGate.Below(10, 70, 40, 70), "内存达到阈值需要确认");
        var pair = RemotePassword.Create("secret");
        check(RemotePassword.Verify("secret", pair.Salt, pair.Hash), "远程口令正确");
        check(!RemotePassword.Verify("nope", pair.Salt, pair.Hash), "远程口令错误");
        var sample = MachineLoad.Sample(300);
        check(sample.Cpu is >= 0 and <= 100 && sample.Memory is >= 0 and <= 100, "读取本机 CPU 和内存占用");

        var handler = new FakeRemote();
        using var server = new RemoteHttpServer(IPAddress.Loopback, 0, pair.Salt, pair.Hash, handler);
        server.Start();
        var port = server.Port;
        var denied = false;
        try
        {
            RemoteApi.ServersAsync("127.0.0.1", port, "nope", CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException ex)
        {
            denied = ex.Message.Contains("口令", StringComparison.Ordinal);
        }
        check(denied, "错误口令被拒绝");
        using (var pageClient = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) })
        {
            var html = pageClient.GetStringAsync("http://127.0.0.1:" + port + "/").GetAwaiter().GetResult();
            check(html.Contains("<title>MC 远程启动</title>", StringComparison.Ordinal) && html.Contains("进入管理", StringComparison.Ordinal), "浏览器打开根地址能看到页面");
        }

        var bare = RemoteApi.ServersAsync("127.0.0.1", port, "", CancellationToken.None).GetAwaiter().GetResult();
        check(bare.Servers.Count == 1, "不填口令仍能看到服务器");
        var (needCode, needText) = Probe(port, "POST", "/api/stop", "", """{"name":"demo"}""");
        check(needCode == 401 && needText.Contains("需要口令", StringComparison.Ordinal), "不填口令不能管理");
        var (adminCode, adminText) = Probe(port, "POST", "/api/stop", "secret", """{"name":"demo"}""");
        check(adminCode == 404 && adminText.Contains("没有这个接口", StringComparison.Ordinal), "正确口令可以进入管理接口");

        var list = RemoteApi.ServersAsync("127.0.0.1", port, "secret", CancellationToken.None).GetAwaiter().GetResult();
        check(list.Servers.Count == 1 && list.Servers[0].Port == 25565 && list.Servers[0].Endpoints.Contains("10.0.0.8:25565"), "从程序能读到端口和地址");

        var started = RemoteApi.StartAsync("127.0.0.1", port, "secret", "demo", CancellationToken.None).GetAwaiter().GetResult();
        check(started.Status == "started" && handler.Started == "demo", "低于阈值时直接启动");

        handler.ForcePending = true;
        var pending = RemoteApi.StartAsync("127.0.0.1", port, "secret", "demo", CancellationToken.None).GetAwaiter().GetResult();
        check(pending.Status == "pending" && pending.Id.Length > 0, "超过阈值时等待主机确认");
        var polled = RemoteApi.RequestAsync("127.0.0.1", port, "secret", pending.Id, CancellationToken.None).GetAwaiter().GetResult();
        check(polled.Status == "pending", "从程序能查询确认结果");

        var missing = RemoteApi.StartAsync("127.0.0.1", port, "secret", "none", CancellationToken.None).GetAwaiter().GetResult();
        check(missing.Status == "failed", "不能启动列表外的服务端");

        using var open = new RemoteHttpServer(IPAddress.Loopback, 0, "", "", handler);
        open.Start();
        var openList = RemoteApi.ServersAsync("127.0.0.1", open.Port, "", CancellationToken.None).GetAwaiter().GetResult();
        check(openList.Servers.Count == 1, "空口令可以直接读取");
        var (blockedCode, blockedText) = Probe(open.Port, "POST", "/api/stop", "", """{"name":"demo"}""");
        check(blockedCode == 403 && blockedText.Contains("不接受网页管理", StringComparison.Ordinal), "主机没设口令时拒绝网页管理");
    }

    static (int Code, string Body) Probe(int port, string method, string path, string token, string json)
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var request = new HttpRequestMessage(new HttpMethod(method), "http://127.0.0.1:" + port + path);
        request.Headers.TryAddWithoutValidation("X-Token", Convert.ToBase64String(Encoding.UTF8.GetBytes(token)));
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = client.Send(request);
        return ((int)response.StatusCode, response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
    }

    sealed class FakeRemote : IRemoteHandler
    {
        public string Started = "";
        public bool ForcePending;
        readonly Dictionary<string, RemoteStartResponse> _requests = new();

        public RemoteReply Handle(string method, string path, string body, string remoteIp)
        {
            if (method == "GET" && path == "/api/servers")
            {
                return RemoteReply.Of(new RemoteListResponse
                {
                    Servers =
                    [
                        new RemoteServerView
                        {
                            Name = "demo",
                            Port = 25565,
                            State = "已停止",
                            CanStart = true,
                            Endpoints = ["127.0.0.1:25565", "10.0.0.8:25565"],
                        },
                    ],
                });
            }
            if (method == "POST" && path == "/api/start")
            {
                var request = JsonSerializer.Deserialize<RemoteStartBody>(body, JsonFiles.Opt);
                if (request?.Name != "demo")
                    return RemoteReply.Of(new RemoteStartResponse { Status = "failed", Message = "找不到这个服务端。" });
                if (ForcePending)
                {
                    var id = "abc123";
                    var pending = new RemoteStartResponse { Id = id, Status = "pending", Message = "等待主机确认" };
                    _requests[id] = pending;
                    return RemoteReply.Of(pending);
                }
                Started = request.Name;
                return RemoteReply.Of(new RemoteStartResponse { Status = "started", Message = "已启动" });
            }
            if (method == "GET" && path.StartsWith("/api/requests/", StringComparison.Ordinal))
            {
                var id = path["/api/requests/".Length..];
                if (_requests.TryGetValue(id, out var found))
                    return RemoteReply.Of(found);
                return RemoteReply.Of(new RemoteStartResponse { Status = "failed", Message = "没有这条请求。" }, 404);
            }
            return RemoteReply.Of(new RemoteStartResponse { Status = "failed", Message = "没有这个接口。" }, 404);
        }
    }
}

internal static class RemotePage
{
    public const string Html = """
        <!DOCTYPE html>
        <html lang="zh-CN">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>MC 远程启动</title>
        <style>
        body { margin: 0; background: #16181e; color: #e8eaed; font-family: "Microsoft YaHei UI", sans-serif; }
        main { max-width: 880px; margin: 0 auto; padding: 28px 20px 64px; }
        h1 { font-size: 28px; margin: 0 0 8px; }
        #status, .hint { color: #9aa0ac; }
        #pass { display: flex; gap: 8px; margin: 16px 0; flex-wrap: wrap; }
        input, button, select, textarea { font: inherit; border-radius: 8px; }
        input, select, textarea { background: #12141a; color: #e8eaed; border: 1px solid #444a58; padding: 8px 12px; }
        button { background: #4c94ff; color: white; border: 0; padding: 8px 14px; cursor: pointer; }
        button.ghost { background: #303542; }
        button.warn { background: #d65454; }
        button:disabled { opacity: .5; cursor: default; }
        section { background: #262a34; border-radius: 12px; padding: 16px 18px; margin-top: 14px; }
        h2 { margin: 0 0 8px; font-size: 20px; }
        p { margin: 4px 0; }
        .addr { color: #8cbcff; font-size: 18px; word-break: break-all; }
        .note { color: #e8ba4a; }
        .actions { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 12px; }
        #panel { margin-top: 16px; }
        #panel[hidden] { display: none; }
        pre { white-space: pre-wrap; word-break: break-word; background: #12141a; padding: 12px; border-radius: 8px; max-height: 360px; overflow: auto; }
        .config { max-height: 480px; overflow: auto; }
        .field { margin: 10px 0; }
        .field span { display: block; color: #9aa0ac; font-size: 13px; }
        </style>
        </head>
        <body>
        <main>
          <h1>MC 远程启动</h1>
          <p class="hint">不填口令只能启动。输入主机上设置的口令后，可以停止、看日志、发指令、备份、改配置。主机没设口令时，这些管理请求会被拒绝。</p>
          <p id="status">正在读取…</p>
          <div id="pass">
            <input id="password" type="password" placeholder="管理口令，可留空">
            <button id="save" type="button">进入管理</button>
            <button id="leave" class="ghost" type="button">只保留启动</button>
          </div>
          <div id="list"></div>
          <section id="panel" hidden>
            <h2 id="panel-title"></h2>
            <div id="panel-body"></div>
          </section>
        </main>
        <script>
        const key = "mcpanel-pass";
        const statusEl = document.querySelector("#status");
        const listEl = document.querySelector("#list");
        const panel = document.querySelector("#panel");
        const panelTitle = document.querySelector("#panel-title");
        const panelBody = document.querySelector("#panel-body");
        let admin = false;
        let servers = [];
        function token() {
          const text = sessionStorage.getItem(key) || "";
          const bytes = new TextEncoder().encode(text);
          let bin = "";
          for (const b of bytes) bin += String.fromCharCode(b);
          return btoa(bin);
        }
        async function call(path, options) {
          const headers = Object.assign({ "X-Token": token() }, options && options.headers);
          const res = await fetch(path, Object.assign({}, options, { headers }));
          const data = await res.json().catch(function () { return {}; });
          if (!res.ok) throw new Error(data.message || "请求失败");
          return data;
        }
        function post(path, body) {
          return call(path, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body || {}) });
        }
        async function act(path, body) {
          let data = await post(path, body);
          if (data.status === "confirm" && window.confirm(data.message)) {
            if (data.message.indexOf("端口") >= 0) body.confirmPort = true;
            if (data.message.indexOf("世界文件夹") >= 0) body.confirmLevel = true;
            if (data.message.indexOf("备份") >= 0) body.allowRunning = true;
            data = await post(path, body);
          }
          return data;
        }
        function showPanel(title) {
          panel.hidden = false;
          panelTitle.textContent = title;
          panelBody.replaceChildren();
          return panelBody;
        }
        function say(data) {
          statusEl.textContent = data.message || data.status || "完成";
        }
        function button(text, className, onclick) {
          const node = document.createElement("button");
          node.textContent = text;
          if (className) node.className = className;
          node.onclick = onclick;
          return node;
        }
        function render() {
          listEl.replaceChildren();
          servers.forEach(function (server) {
            const card = document.createElement("section");
            const title = document.createElement("h2");
            title.textContent = server.name;
            const meta = document.createElement("p");
            meta.textContent = server.state + "    端口 " + server.port;
            const addr = document.createElement("p");
            addr.className = "addr";
            addr.textContent = (server.endpoints || []).join("    ") || "无地址";
            card.append(title, meta, addr);
            const actions = document.createElement("div");
            actions.className = "actions";
            if (server.canStart) actions.append(button("启动", "", function () { runStart(server.name, this); }));
            else if (server.block) {
              const note = document.createElement("p");
              note.className = "note";
              note.textContent = server.block;
              card.append(note);
            }
            if (admin) {
              actions.append(button("停止", "ghost", function () { runSimple("/api/stop", server.name); }));
              actions.append(button("强制结束", "warn", function () {
                if (window.confirm("强制结束可能导致刚才的存档没写完。继续吗？")) runSimple("/api/kill", server.name);
              }));
              actions.append(button("日志", "ghost", function () { runLog(server.name); }));
              actions.append(button("指令", "ghost", function () { runCommand(server.name); }));
              actions.append(button("备份", "ghost", function () { runBackup(server.name); }));
              actions.append(button("新世界", "ghost", function () { runWorld(server.name); }));
              actions.append(button("配置", "ghost", function () { runConfig(server.name); }));
              actions.append(button("从备份恢复", "ghost", function () { runBackups(server.name); }));
              actions.append(button("崩溃记录", "ghost", function () { runCrashes(server.name); }));
            }
            card.append(actions);
            listEl.append(card);
          });
        }
        async function load() {
          try {
            const data = await call("/api/servers");
            servers = data.servers || [];
            render();
            const mode = admin ? "管理权限已打开。" : (data.privilegeEnabled ? "现在只能启动。输入口令可管理。" : "主机没有设置口令，网页只能启动。");
            if (!statusEl.dataset.lock) statusEl.textContent = (servers.length ? "下面的地址用来进游戏。" : "主机上没有服务端。") + mode;
          } catch (e) {
            statusEl.textContent = e.message;
          }
        }
        async function runStart(name, node) {
          node.disabled = true;
          statusEl.textContent = "正在请求启动「" + name + "」…";
          try {
            const data = await post("/api/start", { name: name });
            say(data);
            if (data.status === "started") load();
          } catch (e) {
            statusEl.textContent = e.message;
          } finally {
            node.disabled = false;
          }
        }
        async function runSimple(path, name) {
          try { say(await act(path, { name: name })); if (path === "/api/stop" || path === "/api/kill") load(); }
          catch (e) { statusEl.textContent = e.message; }
        }
        async function runLog(name) {
          const body = showPanel(name + " 的日志");
          const pre = document.createElement("pre");
          body.append(pre, button("刷新", "ghost", function () { fill(); }));
          async function fill() {
            const data = await post("/api/log", { name: name });
            pre.textContent = (data.data && data.data.text) || data.message || "";
            say(data);
          }
          try { await fill(); } catch (e) { statusEl.textContent = e.message; }
        }
        async function runCommand(name) {
          const text = window.prompt("发给「" + name + "」的指令", "list");
          if (!text) return;
          try { say(await post("/api/command", { name: name, command: text })); }
          catch (e) { statusEl.textContent = e.message; }
        }
        async function runBackup(name) {
          statusEl.textContent = "正在备份，世界很大时会多等一会儿…";
          try { say(await act("/api/backup", { name: name })); }
          catch (e) { statusEl.textContent = e.message; }
        }
        async function runWorld(name) {
          const world = window.prompt("新世界文件夹名。旧世界会保留。", "");
          if (!world) return;
          const seed = window.prompt("种子，留空则沿用当前种子。", "") || "";
          try { say(await post("/api/world", { name: name, world: world, seed: seed })); load(); }
          catch (e) { statusEl.textContent = e.message; }
        }
        async function runBackups(name) {
          const body = showPanel(name + " 的备份");
          try {
            const data = await post("/api/backups", { name: name });
            say(data);
            (data.data || []).forEach(function (file) {
              const line = document.createElement("p");
              line.textContent = file.time + "    " + file.size + "    " + file.name;
              const restore = button("恢复这份", "warn", async function () {
                if (!window.confirm("用这份备份替换当前世界？会先再备份一次。")) return;
                try { say(await post("/api/restore", { name: name, file: file.name })); }
                catch (e) { statusEl.textContent = e.message; }
              });
              body.append(line, restore);
            });
          } catch (e) { statusEl.textContent = e.message; }
        }
        async function runCrashes(name) {
          const body = showPanel(name + " 的崩溃记录");
          try {
            const data = await post("/api/crashes", { name: name });
            say(data);
            (data.data || []).forEach(function (file) {
              body.append(button(file.time + "  " + file.name, "ghost", async function () {
                const detail = await post("/api/crash", { name: name, id: file.name });
                const pre = document.createElement("pre");
                pre.textContent = (detail.data && detail.data.text) || "";
                body.append(pre);
              }));
            });
          } catch (e) { statusEl.textContent = e.message; }
        }
        async function runConfig(name) {
          const body = showPanel(name + " 的配置");
          try {
            const data = await post("/api/config", { name: name });
            say(data);
            const info = data.data || { readOnly: true, items: [] };
            const form = document.createElement("div");
            form.className = "config";
            const inputs = [];
            let group = "";
            (info.items || []).forEach(function (item) {
              if (item.group !== group) {
                group = item.group;
                const heading = document.createElement("h2");
                heading.textContent = group;
                form.append(heading);
              }
              const field = document.createElement("label");
              field.className = "field";
              const caption = document.createElement("span");
              caption.textContent = item.label + "    " + item.key;
              field.append(caption);
              let input;
              if (item.choices && item.choices.length && item.kind !== "text") {
                input = document.createElement("select");
                item.choices.forEach(function (choice) {
                  const option = document.createElement("option");
                  option.value = choice.value;
                  option.textContent = choice.label;
                  if (choice.value === item.value) option.selected = true;
                  input.append(option);
                });
                if (item.kind === "choice" && !item.choices.some(function (choice) { return choice.value === item.value; })) {
                  const option = document.createElement("option");
                  option.value = item.value;
                  option.textContent = item.value;
                  option.selected = true;
                  input.append(option);
                }
              } else {
                input = document.createElement("input");
                input.value = item.value;
              }
              input.disabled = !!info.readOnly;
              input.dataset.key = item.key;
              input.dataset.origin = item.value;
              field.title = item.description || "";
              field.append(input);
              inputs.push(input);
              form.append(field);
            });
            body.append(form);
            if (!info.readOnly) {
              body.append(button("保存配置", "", async function () {
                const changes = inputs.filter(function (input) { return input.value !== input.dataset.origin; })
                  .map(function (input) { return { key: input.dataset.key, value: input.value }; });
                try { say(await act("/api/config/save", { name: name, changes: changes })); runConfig(name); }
                catch (e) { statusEl.textContent = e.message; }
              }));
            }
          } catch (e) { statusEl.textContent = e.message; }
        }
        document.querySelector("#save").onclick = async function () {
          const value = document.querySelector("#password").value;
          if (!value) {
            admin = false;
            sessionStorage.removeItem(key);
            statusEl.textContent = "不填口令只能启动。";
            load();
            return;
          }
          sessionStorage.setItem(key, value);
          try {
            const data = await post("/api/auth", {});
            admin = data.status === "ok";
            statusEl.dataset.lock = "1";
            say(data);
            delete statusEl.dataset.lock;
            load();
          } catch (e) {
            admin = false;
            sessionStorage.removeItem(key);
            statusEl.textContent = e.message;
            load();
          }
        };
        document.querySelector("#leave").onclick = function () {
          admin = false;
          sessionStorage.removeItem(key);
          document.querySelector("#password").value = "";
          panel.hidden = true;
          statusEl.textContent = "已回到只能启动。";
          load();
        };
        if (sessionStorage.getItem(key)) document.querySelector("#save").onclick();
        else load();
        </script>
        </body>
        </html>
        """;
}
