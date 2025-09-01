using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.IO;

class Program
{
    static TcpClient? tcp;
    static BinaryReader? reader;
    static BinaryWriter? writer;
    static string? username;
    static bool running = true;
    static string? pmTarget = null;

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        Console.Write("Nhap ten user: ");
        username = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(username)) { Console.WriteLine("Ten khong hop le."); return; }

        Console.Write("Nhap server IP (Enter=localhost): ");
        var ip = Console.ReadLine();
        if (string.IsNullOrEmpty(ip)) ip = "127.0.0.1";
        int port = 9000;

        try
        {
            tcp = new TcpClient();
            tcp.NoDelay = true;
            tcp.Connect(ip, port);
            var ns = tcp.GetStream();
            reader = new BinaryReader(ns, Encoding.UTF8, leaveOpen: true);
            writer = new BinaryWriter(ns, Encoding.UTF8, leaveOpen: true);

            // Gửi CONNECT
            SendHeader($"CONNECT|{username}");

            // Thread lắng nghe dữ liệu từ server
            Thread listenThread = new Thread(ListenLoop) { IsBackground = true };
            listenThread.Start();

            Console.WriteLine("Da ket noi server.");
            Console.WriteLine("Lenh ho tro:");
            Console.WriteLine("  users                - xem danh sach user online");
            Console.WriteLine("  pm <user> <message>  - nhan tin rieng");
            Console.WriteLine("  exitpm               - thoat che do nhan tin rieng");
            Console.WriteLine("  file <path> [target] - gui file (target=ALL hoac ten user)");
            Console.WriteLine("  exit                 - thoat");
            Console.WriteLine("Hoac go tin nhan de chat chung.");

            // Vòng lặp nhập lệnh/tin nhắn
            while (running)
            {
                string? line = Console.ReadLine();
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.StartsWith("users", StringComparison.OrdinalIgnoreCase))
                {
                    SendHeader($"CMD|{username}|LIST");
                }
                else if (line.StartsWith("pm ", StringComparison.OrdinalIgnoreCase))
                {
                    var sp = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                    if (sp.Length < 3) { Console.WriteLine("Dung: pm <user> <message>"); continue; }
                    pmTarget = sp[1];
                    var msg = sp[2];
                    SendHeader($"PM|{username}|{pmTarget}|{msg}");
                    Console.WriteLine($"[Che do rieng] Ban dang chat voi {pmTarget}. Go 'exitpm' de quay lai chat chung.");
                }
                else if (string.Equals(line, "exitpm", StringComparison.OrdinalIgnoreCase))
                {
                    pmTarget = null;
                    Console.WriteLine("Da thoat che do rieng. Dang o chat chung.");
                }
                else if (line.StartsWith("file ", StringComparison.OrdinalIgnoreCase))
                {
                    // file <path> [target]
                    var sp = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                    var path = sp.Length >= 2 ? sp[1].Trim().Trim('"') : null;
                    var target = sp.Length >= 3 ? sp[2].Trim() : "ALL";
                    if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    {
                        Console.WriteLine("File khong ton tai.");
                        continue;
                    }
                    try
                    {
                        SendFile(path, target);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Loi gui file: " + ex.Message);
                    }
                }
                else if (string.Equals(line, "exit", StringComparison.OrdinalIgnoreCase))
                {
                    try { SendHeader($"EXIT|{username}"); } catch { }
                    running = false;
                    break;
                }
                else
                {
                    // Tin nhắn thường
                    if (pmTarget != null)
                    {
                        SendHeader($"PM|{username}|{pmTarget}|{line}");
                    }
                    else
                    {
                        SendHeader($"MSG|{username}|{line}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Loi ket noi: " + ex.Message);
        }
        finally
        {
            running = false;
            try { writer?.Close(); reader?.Close(); tcp?.Close(); } catch { }
            Console.WriteLine("Da thoat client.");
        }
    }

    static void ListenLoop()
    {
        try
        {
            while (running && tcp != null && tcp.Connected)
            {
                string header = ReadHeader();
                if (string.IsNullOrEmpty(header)) break;

                var parts = header.Split('|');
                var type = parts[0];

                if (type == "MSG")
                {
                    // MSG|Server|USERS|a, b, c
                    if (parts.Length >= 4 && parts[1] == "Server" && parts[2] == "USERS")
                    {
                        Console.WriteLine($"[Server] Online: {parts[3]}");
                    }
                    else
                    {
                        var sender = parts.Length >= 2 ? parts[1] : "unknown";
                        var text = parts.Length >= 3 ? parts[2] : "";
                        Console.WriteLine($"[{sender}] {text}");
                    }
                }
                else if (type == "PM")
                {
                    // PM|sender|target|message
                    var sender = parts.Length >= 2 ? parts[1] : "unknown";
                    var text = parts.Length >= 4 ? parts[3] : "";
                    Console.WriteLine($"[Rieng tu {sender}] {text}");
                }
                else if (type == "FILE")
                {
                    // FILE|sender|recipient|filename|filesize  + bytes
                    var sender = parts.Length >= 2 ? parts[1] : "unknown";
                    var filename = parts.Length >= 4 ? parts[3] : "file.bin";
                    var filesize = parts.Length >= 5 ? long.Parse(parts[4]) : 0L;

                    byte[] bytes = ReadBytesExact(filesize);
                    string baseName = Path.GetFileName(filename);
                    string saveName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{baseName}";
                    File.WriteAllBytes(saveName, bytes);
                    Console.WriteLine($"[FILE] Nhan file tu {sender} -> luu: {saveName} ({filesize} bytes)");
                }
                // Co the mo rong cac loai thong diep khac tai day
            }
        }
        catch (IOException)
        {
            Console.WriteLine("Mat ket noi den server.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Loi listen: " + ex.Message);
        }
        finally
        {
            running = false;
        }
    }

    static void SendHeader(string header)
    {
        if (writer == null) throw new InvalidOperationException("Writer is not initialized.");
        var b = Encoding.UTF8.GetBytes(header);
        writer.Write(b.Length);
        writer.Write(b);
        writer.Flush();
    }

    static string ReadHeader()
    {
        if (reader == null) return "";
        int len;
        try { len = reader.ReadInt32(); } catch { return ""; }
        if (len <= 0) return "";
        var b = reader.ReadBytes(len);
        return Encoding.UTF8.GetString(b);
    }

    static byte[] ReadBytesExact(long count)
    {
        if (reader == null)
            throw new InvalidOperationException("Reader is not initialized.");
        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = reader.BaseStream.Read(buffer, offset, (int)Math.Min(8192, count - offset));
            if (read <= 0) throw new IOException("Unexpected end of stream while reading file bytes.");
            offset += read;
        }
        return buffer;
    }

    static void SendFile(string path, string target)
    {
        var fileBytes = File.ReadAllBytes(path);
        var filename = Path.GetFileName(path);
        long filesize = fileBytes.LongLength;

        // FILE|from|to|filename|filesize
        SendHeader($"FILE|{username}|{target}|{filename}|{filesize}");
        if (writer == null)
            throw new InvalidOperationException("Writer is not initialized.");
        writer.BaseStream.Write(fileBytes, 0, fileBytes.Length);
        writer.Flush();

        Console.WriteLine($"[FILE] Da gui file {filename} ({filesize} bytes) toi {target}");
    }
}
