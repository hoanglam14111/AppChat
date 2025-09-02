using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.IO;
using System.Collections.Generic;

class Program
{
	static TcpClient tcp;
	static BinaryReader reader;
	static BinaryWriter writer;
	static string username;
	static bool running = true;
	static string pmTarget = null;

	// Bảng màu cho user
	static Dictionary<string, ConsoleColor> userColors = new Dictionary<string, ConsoleColor>();
	static ConsoleColor[] colorPool = new ConsoleColor[]
	{
		ConsoleColor.Cyan, ConsoleColor.Green, ConsoleColor.Yellow,
		ConsoleColor.Magenta, ConsoleColor.Blue, ConsoleColor.White
	};
	static int colorIndex = 0;

	static ConsoleColor GetUserColor(string user)
	{
		if (!userColors.ContainsKey(user))
		{
			userColors[user] = colorPool[colorIndex % colorPool.Length];
			colorIndex++;
		}
		return userColors[user];
	}

	static void Main(string[] args)
	{
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
			tcp.Connect(ip, port);
			var ns = tcp.GetStream();
			reader = new BinaryReader(ns, Encoding.UTF8, leaveOpen: true);
			writer = new BinaryWriter(ns, Encoding.UTF8, leaveOpen: true);

			SendHeader($"CONNECT|{username}");

			Thread listenThread = new Thread(ListenLoop) { IsBackground = true };
			listenThread.Start();

			Console.WriteLine("Danh sach lenh:");
			Console.WriteLine("  /pm <user> <msg>      - Gui tin nhan rieng");
			Console.WriteLine("  /exitpm               - Thoat che do nhan rieng");
			Console.WriteLine("  /file <path> <user>   - Gui file");
			Console.WriteLine("  /exit                 - Thoat ung dung");


			while (running)
			{
				string line = Console.ReadLine();
				if (string.IsNullOrEmpty(line)) continue;

				if (line.StartsWith("/users"))
				{
					SendHeader($"CMD|{username}|LIST");
				}
				else if (line.StartsWith("/pm "))
				{
					var sp = line.Split(' ', 3);
					if (sp.Length < 3) { Console.WriteLine("Dung: /pm <user> <message>"); continue; }
					pmTarget = sp[1];
					var msg = sp[2];
					SendHeader($"PM|{username}|{pmTarget}|{msg}");
					Console.WriteLine($"[Che do rieng] Ban dang chat voi {pmTarget}. Go /exitpm de quay lai chat chung.");
				}
				else if (line == "/exitpm")
				{
					pmTarget = null;
					Console.WriteLine("Da thoat che do rieng. Dang o chat chung.");
				}
				else if (line.StartsWith("/file "))
				{
					var sp = line.Split(' ', 3);
					var path = sp.Length >= 2 ? sp[1].Trim('"') : null;
					var target = sp.Length >= 3 ? sp[2] : "ALL";
					if (string.IsNullOrEmpty(path) || !File.Exists(path)) { Console.WriteLine("File khong ton tai."); continue; }
					SendFile(path, target);
				}
				else if (line == "/exit")
				{
					SendHeader($"EXIT|{username}");
					running = false;
					break;
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

		}
		catch (Exception ex)
		{
			Console.WriteLine("Loi ket noi: " + ex.Message);
		}
		finally
		{
			try { writer?.Close(); reader?.Close(); tcp?.Close(); } catch { }
		}
	}

	static void ListenLoop()
	{
		try
		{
			while (running && tcp.Connected)
			{
				string header = ReadHeader();
				if (string.IsNullOrEmpty(header)) break;
				var parts = header.Split('|');
				var type = parts[0];
				if (type == "MSG")
				{
					if (parts.Length >= 4 && parts[2] == "USERS")
					{
						Console.WriteLine($"[Server] Online: {parts[3]}");
					}
					else
					{
						var sender = parts.Length >= 2 ? parts[1] : "unknown";
						var text = parts.Length >= 3 ? parts[2] : "";
						var timestamp = parts.Length >= 4 ? parts[3] : "";

						// In timestamp trước
						Console.Write($"[{timestamp}] ");

						// In tên user với màu riêng
						Console.ForegroundColor = GetUserColor(sender);
						Console.Write(sender);
						Console.ResetColor();

						// In nội dung tin nhắn màu trắng
						Console.WriteLine($": {text}");
					}
				}
				else if (type == "PM")
				{
					var sender = parts.Length >= 2 ? parts[1] : "unknown";
					var text = parts.Length >= 4 ? parts[3] : "";
					var timestamp = parts.Length >= 5 ? parts[4] : "";

					Console.Write($"(PM) [{timestamp}] ");
					Console.ForegroundColor = GetUserColor(sender);
					Console.Write(sender);
					Console.ResetColor();
					Console.WriteLine($": {text}");
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

	static void SendHeader(string header)
	{
		var b = Encoding.UTF8.GetBytes(header);
		writer.Write(b.Length);
		writer.Write(b);
		writer.Flush();
	}

	static string ReadHeader()
	{
		int len;
		try { len = reader.ReadInt32(); } catch { return ""; }
		if (len <= 0) return "";
		var b = reader.ReadBytes(len);
		return Encoding.UTF8.GetString(b);
	}

	static byte[] ReadBytesExact(long count)
	{
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
		SendHeader($"FILE|{username}|{target}|{filename}|{filesize}");
		writer.BaseStream.Write(fileBytes, 0, fileBytes.Length);
		writer.Flush();
		Console.WriteLine($"[FILE] Da gui file {filename} ({filesize} bytes) toi {target}");
	}
}


