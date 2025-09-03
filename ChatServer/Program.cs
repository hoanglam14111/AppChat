using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.IO;

class ClientInfo
{
    public string Username { get; set; } = "";
    public TcpClient? Tcp { get; set; }
    public BinaryReader? Reader { get; set; }
    public BinaryWriter? Writer { get; set; }
}

class ChatServer
{
    private static TcpListener? listener;
    // Bỏ qua phân biệt hoa thường
    private static readonly Dictionary<string, ClientInfo> clients =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object clientsLock = new();
    private static bool isRunning = true;

    static void Main(string[] args)
    {
        int port = 9000;
        Console.Title = "Chat Server";
        Console.WriteLine($"🚀 Chat Server khởi động trên cổng {port} ...");

        listener = new TcpListener(IPAddress.Any, port);
        listener.Start();

        // Thread nhận kết nối client
        var acceptThread = new Thread(AcceptLoop) { IsBackground = true };
        acceptThread.Start();

        Console.WriteLine("Nhấn ENTER để tắt server.");
        Console.ReadLine();

        isRunning = false;
        listener.Stop();

        // Đóng tất cả client
        lock (clientsLock)
        {
            foreach (var c in clients.Values)
            {
                try
                {
                    c.Writer?.Close();
                    c.Reader?.Close();
                    c.Tcp?.Close();
                }
                catch { }
            }
            clients.Clear();
        }

        Console.WriteLine("🔴 Server đã tắt.");
    }

    static void AcceptLoop()
    {
        while (isRunning)
        {
            try
            {
                if (listener != null)
                {
                    TcpClient tcp = listener.AcceptTcpClient();
                    var t = new Thread(() => HandleClient(tcp)) { IsBackground = true };
                    t.Start();
                }
            }
            catch
            {
                break;
            }
        }
    }

    static void HandleClient(TcpClient tcp)
    {
        var ns = tcp.GetStream();
        var reader = new BinaryReader(ns, Encoding.UTF8, leaveOpen: true);
        var writer = new BinaryWriter(ns, Encoding.UTF8, leaveOpen: true);

        string username = "";

        try
        {
            while (isRunning && tcp.Connected)
            {
                string header = ReadHeader(reader);
                if (string.IsNullOrEmpty(header)) break;

                var parts = header.Split('|');
                string type = parts[0];

                switch (type)
                {
                    case "CONNECT":
                        username = parts[1].Trim();
                        if (!RegisterClient(username, tcp, reader, writer))
                        {
                            SendHeader(writer, "MSG|Server|ERROR|Username đã tồn tại!");
                            return;
                        }
                        Console.WriteLine($"[JOIN] {username} connected.");
                        Broadcast($"MSG|Server|{username} đã tham gia.");
                        BroadcastUserList();
                        break;

                    case "MSG":
                        if (parts.Length >= 3)
                        {
                            string sender = parts[1].Trim();
                            string msg = parts[2];
                            Broadcast($"MSG|{sender}|{msg}");
                        }
                        break;

                    case "PM":
                        if (parts.Length >= 4)
                        {
                            string sender = parts[1].Trim();
                            string target = parts[2].Trim();

                            lock (clientsLock)
                            {
                                if (!clients.ContainsKey(target))
                                {
                                    if (clients.TryGetValue(sender, out var senderClient))
                                        SendHeader(senderClient.Writer!, $"MSG|Server|ERROR|Nguoi dung '{target}' khong ton tai hoac offline.");
                                }
                                else
                                {
                                    string msg = parts[3];
                                    SendHeader(clients[target].Writer!, $"PM|{sender}|{target}|{msg}");
                                }
                            }
                        }
                        break;

                    case "CMD":
                        if (parts.Length >= 3 && parts[2] == "LIST")
                            SendUserList(writer);
                        break;

                    case "FILE":
                        if (parts.Length >= 5)
                            HandleFileTransfer(parts, reader);
                        break;

                    case "EXIT":
                        Console.WriteLine($"[EXIT] {username} yêu cầu thoát.");
                        return;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Client {username}: {ex.Message}");
        }
        finally
        {
            DisconnectClient(username);
        }
    }

    static bool RegisterClient(string username, TcpClient tcp, BinaryReader reader, BinaryWriter writer)
    {
        lock (clientsLock)
        {
            if (clients.ContainsKey(username))
                return false;

            clients[username] = new ClientInfo
            {
                Username = username,
                Tcp = tcp,
                Reader = reader,
                Writer = writer
            };
            return true;
        }
    }

    static void SendUserList(BinaryWriter writer)
    {
        lock (clientsLock)
        {
            string list = string.Join(", ", clients.Keys);
            SendHeader(writer, $"MSG|Server|USERS|{list}");
        }
    }

    static void BroadcastUserList()
    {
        lock (clientsLock)
        {
            string list = string.Join(", ", clients.Keys);
            Broadcast($"MSG|Server|USERS|{list}");
        }
    }

    static void DisconnectClient(string username)
    {
        if (string.IsNullOrEmpty(username)) return;

        lock (clientsLock)
        {
            if (clients.Remove(username))
            {
                Console.WriteLine($"[DISCONNECT] {username} disconnected.");
                Broadcast($"MSG|Server|{username} đã thoát.");
                BroadcastUserList();
            }
        }
    }

    static void Broadcast(string header, string? except = null)
    {
        List<KeyValuePair<string, ClientInfo>> snapshot;
        lock (clientsLock)
            snapshot = new List<KeyValuePair<string, ClientInfo>>(clients);

        foreach (var kv in snapshot)
        {
            if (kv.Key == except) continue;
            try
            {
                SendHeader(kv.Value.Writer!, header);
            }
            catch
            {
                Console.WriteLine($"[WARN] Mất kết nối với {kv.Key}, xóa khỏi danh sách.");
                lock (clientsLock) clients.Remove(kv.Key);
            }
        }
    }

    static void HandleFileTransfer(string[] parts, BinaryReader reader)
    {
        string sender = parts[1].Trim();
        string target = parts[2].Trim();
        string filename = parts[3];
        long filesize = long.Parse(parts[4]);

        byte[] bytes = ReadBytesExact(reader, filesize);

        if (target.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            List<KeyValuePair<string, ClientInfo>> snapshot;
            lock (clientsLock)
                snapshot = new List<KeyValuePair<string, ClientInfo>>(clients);

            foreach (var kv in snapshot)
            {
                if (kv.Key.Equals(sender, StringComparison.OrdinalIgnoreCase)) continue;
                var w = kv.Value.Writer;
                if (w != null)
                {
                    SendHeader(w, $"FILE|{sender}|{kv.Key}|{filename}|{filesize}");
                    w.BaseStream.Write(bytes, 0, bytes.Length);
                    w.Flush();
                }
            }
        }
        else
        {
            lock (clientsLock)
            {
                if (!clients.TryGetValue(target, out var targetClient))
                {
                    if (clients.TryGetValue(sender, out var senderClient))
                        SendHeader(senderClient.Writer!, $"MSG|Server|ERROR|Nguoi dung '{target}' khong ton tai hoac offline.");
                    return;
                }

                var w = targetClient.Writer;
                if (w != null)
                {
                    SendHeader(w, $"FILE|{sender}|{target}|{filename}|{filesize}");
                    w.BaseStream.Write(bytes, 0, bytes.Length);
                    w.Flush();
                }
            }
        }

        Console.WriteLine($"[FILE] {sender} gửi file {filename} ({filesize} bytes) tới {target}");
    }

    static void SendHeader(BinaryWriter writer, string header)
    {
        var b = Encoding.UTF8.GetBytes(header);
        writer.Write(b.Length);
        writer.Write(b);
        writer.Flush();
    }

    static string ReadHeader(BinaryReader reader)
    {
        int len;
        try { len = reader.ReadInt32(); }
        catch { return ""; }

        if (len <= 0) return "";
        var b = reader.ReadBytes(len);
        return Encoding.UTF8.GetString(b);
    }

    static byte[] ReadBytesExact(BinaryReader reader, long count)
    {
        byte[] buffer = new byte[count];
        int offset = 0;

        