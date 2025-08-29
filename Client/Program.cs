using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.IO;

class Program
{
	static TcpClient tcp;
	static BinaryReader reader;
	static BinaryWriter writer;
	static string username;
	static bool running = true;
	static string pmTarget = null;

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

			Console.WriteLine("Commands: users  pm <user> <msg>  exitpm  file <path> [target]  exit");

			while (running)
			{
				string line = Console.ReadLine();
				if (string.IsNullOrEmpty(line)) continue;

				if (line.StartsWith("users"))
				{
					SendHeader($"CMD|{username}|LIST");
				}
				else if (line.StartsWith("pm "))
				{
					var sp = line.Split(' ', 3);
					if (sp.Length < 3) { Console.WriteLine("Dung: pm <user> <message>"); continue; }
					pmTarget = sp[1];
					var msg = sp[2];
					SendHeader($"PM|{username}|{pmTarget}|{msg}");
					Console.WriteLine($"[Che do rieng] Ban dang chat voi {pmTarget}. Go exitpm de quay lai chat chung.");
				}
				else if (line == "exitpm")
				{
					pmTarget = null;
					Console.WriteLine("Da thoat che do rieng. Dang o chat chung.");
				}
				else if (line.StartsWith("file "))
				{
					var sp = line.Split(' ', 3);
					var path = sp.Length >= 2 ? sp[1].Trim('"') : null;
					var target = sp.Length >= 3 ? sp[2] : "ALL";
					if (string.IsNullOrEmpty(path) || !File.Exists(path)) { Console.WriteLine("File khong ton tai."); continue; }
					SendFile(path, target);
				}
				else if (line == "exit")
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

