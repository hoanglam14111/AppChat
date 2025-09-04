using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.IO;
using System.Collections.Generic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates; // Để dùng X509CertificateLoader

class ClientChat
{
    static HashSet<string> onlineUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    static TcpClient? tcp;
    static SslStream? sslStream;
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
        if (string.IsNullOrEmpty(username))
        {
            Console.WriteLine("Ten khong hop le.");
            return;
        }

        Console.Write("Nhap server IP (Enter=localhost): ");
        var ip = Console.ReadLine();
        if (string.IsNullOrEmpty(ip)) ip = "127.0.0.1";
        int port = 9000;

        try
        {
            tcp = new TcpClient();
            tcp.NoDelay = true;
            tcp.Connect(ip, port);

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("Dang thiet lap SSL...");
            Console.ResetColor();

            sslStream = new SslStream(tcp.GetStream(), false, ValidateServerCertificate);
            sslStream.AuthenticateAsClient("MyChatServer");

            // Dùng BinaryReader/Writer qua SslStream
            reader = new BinaryReader(sslStream, Encoding.UTF8, leaveOpen: true);
            writer = new BinaryWriter(sslStream, Encoding.UTF8, leaveOpen: true);

            // Gửi CONNECT
            SendHeader($"CONNECT|{username}");

            Thread listenThread = new Thread(ListenLoop) { IsBackground = true };
            listenThread.Start();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Da ket noi server qua SSL.");
            Console.ResetColor();

            Console.WriteLine("\nLenh ho tro:");
            Console.WriteLine("  users                - xem danh sach user online");
            Console.WriteLine("  pm <user> <message>  - nhan tin rieng");
            Console.WriteLine("  exitpm               - thoat che do nhan tin rieng");
            Console.WriteLine("  file <path> [target] - gui file (target=ALL hoac ten user)");
            Console.WriteLine("  exit                 - thoat");
            Console.WriteLine("Hoac go tin nhan de chat chung.\n");

            while (running)
            {
                string? line = Console.ReadLine();
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                ProcessCommand(line);
            }
        }
        finally
        {
            running = false;
            try
            {
                writer?.Close();
                reader?.Close();
                sslStream?.Close();
                tcp?.Close();
            }
            catch { }

            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("Da thoat client.");
            Console.ResetColor();
        }
    }

    // Hàm kiểm tra chứng chỉ server
    private static bool ValidateServerCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        // Ở môi trường test, bỏ qua lỗi để dễ thử nghiệm
        return true;
    }

    // Xử lý các lệnh gửi lên server
    static void ProcessCommand(string line)
    {
        try
        {
            if (line.StartsWith("users", StringComparison.OrdinalIgnoreCase))
            {
                SendHeader($"CMD|{username}|LIST");
            }
            else if (line.StartsWith("pm ", StringComparison.OrdinalIgnoreCase))
            {
                var sp = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (sp.Length < 3)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Dung: pm <user> <message>");
                    Console.ResetColor();
                    return;
                }

                var targetUser = sp[1].Trim();

                if (!onlineUsers.Contains(targetUser))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Nguoi dung '{targetUser}' khong ton tai hoac khong online.");
                    Console.ResetColor();
                    return;
                }

                pmTarget = targetUser;
                var msg = sp[2];
                SendHeader($"PM|{username}|{pmTarget}|{msg}");

                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine($"[Che do rieng] Dang chat voi {pmTarget}. Go 'exitpm' de quay lai chat chung.");
                Console.ResetColor();
            }
            else if (string.Equals(line, "exitpm", StringComparison.OrdinalIgnoreCase))
            {
                pmTarget = null;
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine("Da thoat che do rieng. Dang o chat chung.");
                Console.ResetColor();
            }
            else if (line.StartsWith("file ", StringComparison.OrdinalIgnoreCase))
            {
                string commandBody = line.Substring(5).Trim();
                string path;
                string target = "ALL";

                if (commandBody.StartsWith("\""))
                {
                    int closingQuoteIndex = commandBody.IndexOf('"', 1);
                    if (closingQuoteIndex == -1)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("Sai cu phap: thieu dau ngoac kep dong.");
                        Console.ResetColor();
                        return;
                    }

                    path = commandBody.Substring(1, closingQuoteIndex - 1).Trim();
                    string remaining = commandBody.Substring(closingQuoteIndex + 1).Trim();
                    if (!string.IsNullOrEmpty(remaining))
                        target = remaining.Trim();
                }
                else
                {
                    var parts = commandBody.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    path = parts[0].Trim();
                    if (parts.Length > 1)
                        target = parts[1].Trim();
                }

                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"File khong ton tai: {path}");
                    Console.ResetColor();
                    return;
                }

                if (!string.Equals(target, "ALL", StringComparison.OrdinalIgnoreCase))
                {
                    if (!onlineUsers.Contains(target))
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"Nguoi dung '{target}' khong ton tai hoac khong online.");
                        Console.ResetColor();
                        return;
                    }
                }

                SendFile(path, target);
            }
            else if (string.Equals(line, "exit", StringComparison.OrdinalIgnoreCase))
            {
                SendHeader($"EXIT|{username}");
                running = false;
            }
            else
            {
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
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Loi xu ly lenh: " + ex.Message);
            Console.ResetColor();
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
                    if (parts.Length >= 4 && parts[1] == "Server" && parts[2] == "USERS")
                    {
                        string[] users = parts[3].Split(',', StringSplitOptions.RemoveEmptyEntries);

                        lock (onlineUsers)
                        {
                            onlineUsers.Clear();
                            foreach (var user in users)
                                onlineUsers.Add(user.Trim());
                        }

                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine("[Server] Online: " + string.Join(", ", onlineUsers));
                        Console.ResetColor();
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
					var sender = parts.Length >= 2 ? parts[1] : "unknown";
					var text = parts.Length >= 4 ? parts[3] : "";
					Console.WriteLine($"[Rieng tu {sender}] {text}");
				}
				else if (type == "FILE")
				{
					var sender = parts.Length >= 2 ? parts[1] : "unknown";
					var filename = parts.Length >= 4 ? parts[3] : "file.bin";
					var filesize = parts.Length >= 5 ? long.Parse(parts[4]) : 0L;

					byte[] bytes = ReadBytesExact(filesize);
					string saveName = $"{DateTime.Now:yyyyMMddHHmmss}_{filename}";
					File.WriteAllBytes(saveName, bytes);
					Console.WriteLine($"[FILE] Nhan file tu {sender} -> luu: {saveName} ({filesize} bytes)");
				}
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
		running = false;
	}
