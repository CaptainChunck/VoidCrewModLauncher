using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace VoidCrewModLauncher.Bridge
{
    /// <summary>
    /// The connection to the launcher: a loopback TCP client on its own thread that keeps trying the launcher's ports
    /// while the game runs. Lines in and out go through queues; the game's main thread reads and writes them, so nothing
    /// here touches the game. If the launcher is closed, the bridge simply keeps trying every few seconds.
    /// </summary>
    internal sealed class Connection : IDisposable
    {
        private readonly int[] _ports;
        private readonly string _helloLine;
        private readonly ConcurrentQueue<string> _incoming = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> _outgoing = new ConcurrentQueue<string>();
        private readonly Thread _thread;
        private volatile bool _stop;
        private volatile bool _connected;
        private volatile bool _welcomed;
        private int _generation;

        public Connection(int[] ports, string helloLine)
        {
            _ports = ports;
            _helloLine = helloLine;
            _thread = new Thread(Run) { IsBackground = true, Name = "VoidCrewModLauncher bridge" };
        }

        /// <summary>Connected over TCP (the launcher may not have welcomed us yet).</summary>
        public bool Connected
        {
            get { return _connected; }
        }

        /// <summary>The launcher answered the hello with the right token; set from the main thread.</summary>
        public bool Welcomed
        {
            get { return _welcomed; }
            set { _welcomed = value; }
        }

        /// <summary>Goes up by one on every new connection, so the main thread knows to send everything again.</summary>
        public int Generation
        {
            get { return Volatile.Read(ref _generation); }
        }

        public void Start()
        {
            _thread.Start();
        }

        public void Send(string line)
        {
            if (_connected) _outgoing.Enqueue(line);
        }

        public bool TryReceive(out string line)
        {
            return _incoming.TryDequeue(out line);
        }

        public void Dispose()
        {
            _stop = true;
        }

        private void Run()
        {
            bool loggedWaiting = false;
            while (!_stop)
            {
                TcpClient client = TryConnect();
                if (client == null)
                {
                    if (!loggedWaiting)
                    {
                        Log.Info("Waiting for the Void Crew Mod Launcher (it is not running, or not listening yet).");
                        loggedWaiting = true;
                    }
                    Sleep(2000);
                    continue;
                }

                loggedWaiting = false;
                try
                {
                    Talk(client);
                }
                catch (Exception e)
                {
                    if (!_stop) Log.Info("The connection to the launcher closed (" + e.Message + ").");
                }
                finally
                {
                    _connected = false;
                    Welcomed = false;
                    try { client.Close(); } catch { }
                }
                Sleep(1000);
            }
        }

        private void Talk(TcpClient client)
        {
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();
            var utf8 = new UTF8Encoding(false);
            var writer = new StreamWriter(stream, utf8) { NewLine = "\n", AutoFlush = true };
            Decoder decoder = utf8.GetDecoder();
            var bytes = new byte[16384];
            var chars = new char[utf8.GetMaxCharCount(bytes.Length)];
            var line = new StringBuilder();

            string stale;
            while (_outgoing.TryDequeue(out stale)) { }
            writer.WriteLine(_helloLine);
            _connected = true;
            Interlocked.Increment(ref _generation);
            Log.Info("Connected to the Void Crew Mod Launcher.");

            while (!_stop)
            {
                string outLine;
                bool wrote = false;
                while (_outgoing.TryDequeue(out outLine))
                {
                    writer.WriteLine(outLine);
                    wrote = true;
                }

                if (stream.DataAvailable)
                {
                    int read = stream.Read(bytes, 0, bytes.Length);
                    if (read <= 0) return;
                    int count = decoder.GetChars(bytes, 0, read, chars, 0);
                    for (int i = 0; i < count; i++)
                    {
                        char c = chars[i];
                        if (c == '\n')
                        {
                            if (line.Length > 0) _incoming.Enqueue(line.ToString());
                            line.Length = 0;
                        }
                        else if (c != '\r')
                        {
                            line.Append(c);
                        }
                    }
                    continue;
                }

                // A readable socket with nothing to read means the launcher closed the connection.
                if (client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0) return;
                if (!wrote) Thread.Sleep(15);
            }
        }

        private TcpClient TryConnect()
        {
            foreach (int port in _ports)
            {
                var client = new TcpClient();
                try
                {
                    IAsyncResult pending = client.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (pending.AsyncWaitHandle.WaitOne(250) && client.Connected)
                    {
                        client.EndConnect(pending);
                        return client;
                    }
                }
                catch
                {
                    // Nothing listening on this port; try the next.
                }
                try { client.Close(); } catch { }
            }
            return null;
        }

        private void Sleep(int milliseconds)
        {
            for (int waited = 0; waited < milliseconds && !_stop; waited += 100) Thread.Sleep(100);
        }
    }
}
