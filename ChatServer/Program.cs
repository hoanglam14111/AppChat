using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.IO;

class ClientInfo
{
    public string? Username { get; set; }
    public TcpClient? Tcp { get; set; }
    public BinaryReader? Reader { get; set; }
    public BinaryWriter? Writer { get; set; }
}

class ChatServerProgram
{
    static TcpListener? listener;
    static readonly Dictionary<string, ClientInfo> clients = new Dictionary<string, ClientInfo>();
    static readonly object clientsLock = new object();
    static bool isRunning = true;

    static void Main(string[] args)
    {
        int port = 9000;
        Console.WriteLine("Chat Server khoi dong tren port " + port);
        listener = new TcpListener(IPAddress.Any, port);
        listener.Start();

        Thread acceptThread = new Thread(AcceptLoop) { IsBackground = true };
        acceptThread.Start();

        Console.WriteLine("Nhan ENTER de tat server.");
        Console.ReadLine();
        isRunning = false;
        listener.Stop();

        lock (clientsLock)
        {
            foreach (var c in clients.Values)
            {
                try { c.Writer?.Close(); c.Reader?.Close(); c.Tcp?.Close(); } catch { }
            }
            clients.Clear();
        }
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
                    Thread t = new Thread(() => HandleClient(tcp)) { IsBackground = true };
                    t.Start();
                }
            }
            catch { break; }
        }
    }

    static void HandleClient(TcpClient tcp)
    {
        NetworkStream ns = tcp.GetStream();
        var reader = new BinaryReader(ns, Encoding.UTF8, leaveOpen: true);
        var writer = new BinaryWriter(ns, Encoding.UTF8, leaveOpen: true);

        string? username = null;
        try
        {
            while (isRunning && tcp.Connected)
            {
                string header = ReadHeader(reader);
                if (string.IsNullOrEmpty(header)) break;

                var parts = header.Split('|');
                var type = parts[0];

                if (type == "CONNECT")
                {
                    username = parts[1];
                    var client = new ClientInfo { Username = username, Tcp = tcp, Reader = reader, Writer = writer };
                    lock (clientsLock) clients[username] = client;
                    Broadcast($"MSG|Server|{username} da tham gia.", username);
                    Console.WriteLine($"{username} connected.");
                }
                else if (type == "MSG")
                {
                    string sender = parts[1];
                    string msg = parts[2];
                    Broadcast($"MSG|{sender}|{msg}");
                }
                else if (type == "CMD" && parts.Length >= 3 && parts[2] == "LIST")
                {
                    lock (clientsLock)
                    {
                        string list = string.Join(", ", clients.Keys);
                        SendHeader(writer, $"MSG|Server|USERS|{list}");
                    }
                }
                else if (type == "PM" && parts.Length >= 4)
                {
                    string sender = parts[1];
                    string target = parts[2];
                    string msg = parts[3];
                    SendPrivate(sender, target, msg);
                }
                else if (type == "FILE" && parts.Length >= 5)
                {
                    string sender = parts[1];
                    string target = parts[2];
                    string filename = parts[3];
                    long filesize = long.Parse(parts[4]);

                    byte[] bytes = ReadBytesExact(reader, filesize);

                    if (target == "ALL")
                    {
                        lock (clientsLock)
                        {
                            foreach (var kv in clients)
                            {
                                if (kv.Key == sender) continue;
                                if (kv.Value.Writer != null)
                                {
                                    SendHeader(kv.Value.Writer, $"FILE|{sender}|{kv.Key}|{filename}|{filesize}");
                                    kv.Value.Writer.BaseStream.Write(bytes, 0, bytes.Length);
                                    kv.Value.Writer.Flush();
                                }
                            }
                        }
                    }
                    else
                    {
                        lock (clientsLock)
                        {
                            if (clients.ContainsKey(target))
                            {
                                var w = clients[target].Writer;
                                if (w != null)
                                {
                                    SendHeader(w, $"FILE|{sender}|{target}|{filename}|{filesize}");
                                    w.BaseStream.Write(bytes, 0, bytes.Length);
                                    w.Flush();
                                }
                            }
                        }
                    }
                    Console.WriteLine($"[FILE] {sender} gui file {filename} ({filesize} bytes) toi {target}");
                }
                else if (type == "EXIT")
                {
                    string uname = parts[1];
                    lock (clientsLock) clients.Remove(uname);
                    Broadcast($"MSG|Server|{uname} da thoat.");
                    Console.WriteLine($"{uname} disconnected.");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Loi client: " + ex.Message);
        }
        finally
        {
            if (username != null)
            {
                lock (clientsLock) clients.Remove(username);
                Broadcast($"MSG|Server|{username} mat ket noi.");
            }
            try { writer?.Close(); reader?.Close(); tcp?.Close(); } catch { }
        }
    }

    static void Broadcast(string header, string? except = null)
    {
        lock (clientsLock)
        {
            foreach (var kv in clients)
            {
                if (kv.Key == except) continue;
                if (kv.Value.Writer != null)
                {
                    try { SendHeader(kv.Value.Writer, header); } catch { }
                }
            }
        }
    }

    static void SendPrivate(string sender, string target, string msg)
    {
        lock (clientsLock)
        {
            if (clients.ContainsKey(target))
            {
                var writer = clients[target].Writer;
                if (writer != null)
                {
                    SendHeader(writer, $"PM|{sender}|{target}|{msg}");
                }
            }
        }
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
        try { len = reader.ReadInt32(); } catch { return ""; }
        if (len <= 0) return "";
        var b = reader.ReadBytes(len);
        return Encoding.UTF8.GetString(b);
    }

    static byte[] ReadBytesExact(BinaryReader reader, long count)
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = reader.BaseStream.Read(buffer, offset, (int)Math.Min(8192, count - offset));
            if (read <= 0) throw new IOException("Unexpected end of stream.");
            offset += read;
        }
        return buffer;
    }
}
