using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

namespace CloudpunkVR.Coo;

// The link to the Cortico World (cortico-world-cloudpunk): a TCP server on the loopback address only (nothing outside
// the PC can reach it, and Windows does not ask about the firewall), one JSON object per line in both directions.
// Protocol in the World's docs/protocol.md. Network I/O runs on background threads; every message is handled on
// Unity's main thread from Pump().
internal sealed class CooBridge : IDisposable
{
    public const int ProtocolVersion = 2;

    readonly int port;
    readonly ConcurrentQueue<string> inbox = new();
    readonly object writeLock = new();
    TcpListener listener;
    Thread acceptThread;
    TcpClient client;
    StreamWriter writer;
    volatile bool running;
    int connectionId;
    int lastSeenConnection;

    public bool Connected => client != null && client.Connected;
    public event Action OnConnected;

    // Request handler: method, args, request id -> result (throw to reply with an error; return Later to reply
    // yourself afterwards with that id). Runs on the main thread.
    public Func<string, JsonObject, long, JsonNode> Request;
    public static readonly JsonObject Later = new();
    // Messages that need no reply ("say", "danmaku", "config", ...).
    public Action<string, JsonObject> Message;

    public CooBridge(int port) => this.port = port;

    public void Start()
    {
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        running = true;
        acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "CooBridge accept" };
        acceptThread.Start();
        Plugin.L.LogInfo($"coo: bridge listening on 127.0.0.1:{port}");
    }

    void AcceptLoop()
    {
        while (running)
        {
            TcpClient c;
            try { c = listener.AcceptTcpClient(); }
            catch { if (!running) return; Thread.Sleep(200); continue; }
            c.NoDelay = true;
            lock (writeLock)
            {
                try { client?.Close(); } catch { }
                client = c;
                writer = new StreamWriter(c.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                connectionId++;
            }
            var t = new Thread(() => ReadLoop(c)) { IsBackground = true, Name = "CooBridge read" };
            t.Start();
        }
    }

    void ReadLoop(TcpClient c)
    {
        try
        {
            using var reader = new StreamReader(c.GetStream(), Encoding.UTF8);
            string line;
            while (running && (line = reader.ReadLine()) != null)
                if (line.Length > 0) inbox.Enqueue(line);
        }
        catch { }
        lock (writeLock)
        {
            if (client == c) { client = null; writer = null; }
        }
        try { c.Close(); } catch { }
    }

    public void Send(JsonObject msg)
    {
        string line = msg.ToJsonString();
        lock (writeLock)
        {
            if (writer == null) return;
            try { writer.WriteLine(line); }
            catch { try { client?.Close(); } catch { } client = null; writer = null; }
        }
    }

    public void SendEvent(string name, JsonObject fields)
    {
        fields ??= new JsonObject();
        fields["type"] = "event";
        fields["name"] = name;
        Send(fields);
    }

    // Main thread, once per frame.
    public void Pump()
    {
        int conn;
        lock (writeLock) conn = connectionId;
        if (conn != lastSeenConnection && Connected)
        {
            lastSeenConnection = conn;
            Plugin.L.LogInfo("coo: World connected");
            OnConnected?.Invoke();
        }
        for (int n = 0; n < 64 && inbox.TryDequeue(out var line); n++)
        {
            JsonObject msg;
            try { msg = JsonNode.Parse(line) as JsonObject; }
            catch (Exception e) { Plugin.L.LogWarning("coo: bad message: " + e.Message); continue; }
            if (msg == null) continue;
            string type = (string)msg["type"] ?? "";
            if (type == "request")
            {
                long id = (long?)msg["id"] ?? 0;
                string method = (string)msg["method"] ?? "";
                var args = msg["args"] as JsonObject ?? new JsonObject();
                var reply = new JsonObject { ["type"] = "reply", ["id"] = id };
                try
                {
                    var result = Request?.Invoke(method, args, id);
                    if (ReferenceEquals(result, Later)) continue;
                    reply["ok"] = true;
                    reply["result"] = result;
                }
                catch (Exception e)
                {
                    reply["ok"] = false;
                    reply["error"] = e is CooError ? e.Message : $"{e.GetType().Name}: {e.Message}";
                    if (e is not CooError) Plugin.L.LogWarning($"coo: {method} failed: {e}");
                }
                Send(reply);
            }
            else
            {
                try { Message?.Invoke(type, msg); }
                catch (Exception e) { Plugin.L.LogWarning($"coo: '{type}' failed: {e}"); }
            }
        }
    }

    public void Dispose()
    {
        running = false;
        try { listener?.Stop(); } catch { }
        try { client?.Close(); } catch { }
    }
}

// An expected failure reported to the agent as is (no stack trace).
internal sealed class CooError : Exception
{
    public CooError(string message) : base(message) { }
}
