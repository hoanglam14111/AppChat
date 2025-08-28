using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.IO;

class Program
{
	static TcpListener listener;
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
				TcpClient tcp = listener.AcceptTcpClient();
				Thread t = new Thread(() => HandleClient(tcp)) { IsBackground = true };
				t.Start();
			}
			catch { break; }
		}
	}
