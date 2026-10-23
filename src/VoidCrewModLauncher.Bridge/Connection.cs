using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Bridge
{
    /// <summary>
    /// The connection to the launcher: a loopback TCP client on its own thread that keeps trying the launcher's ports
    /// while the game runs. Lines in and out go through queues; the game's main thread reads and writes them, so nothing
    /// here touches the game. If the launcher is closed, the bridge simply keeps trying every few seconds.
    /// The launcher's welcome is checked here, before anything from it reaches the game: a program on a launcher port that
    /// can't prove it knows the token is dropped, and the next try starts at the next port.
    /// </summary>
    internal sealed class Connection : IDisposable
    {
        private const int WelcomeTimeout = 5000;
        private const int WriteTimeout = 5000;
        private const int MaxLine = 4 * 1024 * 1024;

        private readonly int[] _ports;
        private readonly string _token;
        private readonly HelloMessage _hello;
        private readonly ConcurrentQueue<string> _incoming = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> _outgoing = new ConcurrentQueue<string>();
        private readonly Thread _thread;
        private volatile bool _stop;
        private volatile bool _connected;
        private int _generation;
        private int _firstPort;

        public Connection(int[] ports, string token, HelloMessage hello)
        {
            _ports = ports;
            _token = token;
            _hello = hello;
            _thread = new Thread(Run) { IsBackground = true, Name = "VoidCrewModLauncher bridge" };
        }

        /// <summary>Connected, and the launcher proved it knows the token.</summary>
        public bool Connected
        {
            get { return _connected; }
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
                int port;
                TcpClient client = TryConnect(out port);
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
                    Talk(client, port);
                }
                catch (Exception e)
                {
                    if (!_stop) Log.Info("The connection to the launcher closed (" + e.Message + ").");
                }
                finally
                {
                    _connected = false;
                    try { client.Close(); } catch { }
                }
                Sleep(1000);
            }
        }

        private void Talk(TcpClient client, int port)
        {
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();
            stream.WriteTimeout = WriteTimeout;
            var utf8 = new UTF8Encoding(false);
            var writer = new StreamWriter(stream, utf8) { NewLine = "\n", AutoFlush = true };
            var reader = new LineReader(stream, client.Client, utf8);

            string nonce = BridgeAuth.NewNonce();
            _hello.Nonce = nonce;
            _hello.Proof = BridgeAuth.Proof(_token, BridgeAuth.HelloPurpose, nonce, port);
            writer.WriteLine(Json.Write(_hello));

            // Nothing else is accepted before the welcome, and it has to come soon.
            string first = null;
            for (int waited = 0; !_stop && waited < WelcomeTimeout && !reader.Closed; waited += 20)
            {
                if (reader.TryRead(out first)) break;
                Thread.Sleep(20);
            }
            if (!IsWelcome(first, nonce, port))
            {
                if (!_stop) Log.Warn("The program on port " + port + " isn't this game's launcher; trying the launcher's other ports.");
                _firstPort = (Array.IndexOf(_ports, port) + 1) % _ports.Length;
                return;
            }

            string stale;
            while (_outgoing.TryDequeue(out stale)) { }
            while (_incoming.TryDequeue(out stale)) { }
            Interlocked.Increment(ref _generation);
            _connected = true;
            Log.Info("Connected to the Void Crew Mod Launcher (" + (string)JObject.Parse(first)["launcherVersion"] + ").");

            while (!_stop)
            {
                string outLine;
                bool wrote = false;
                while (_outgoing.TryDequeue(out outLine))
                {
                    writer.WriteLine(outLine);
                    wrote = true;
                }

                string inLine;
                bool read = false;
                while (reader.TryRead(out inLine))
                {
                    _incoming.Enqueue(inLine);
                    read = true;
                }
                if (reader.Closed) return;
                if (!wrote && !read) Thread.Sleep(15);
            }
        }

        private bool IsWelcome(string line, string nonce, int port)
        {
            if (line == null) return false;
            try
            {
                JObject message = JObject.Parse(line);
                return (string)message["type"] == MessageTypes.Welcome &&
                       BridgeAuth.Check(_token, BridgeAuth.WelcomePurpose, nonce, port, (string)message["proof"]);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private TcpClient TryConnect(out int connectedPort)
        {
            for (int i = 0; i < _ports.Length; i++)
            {
                int port = _ports[(_firstPort + i) % _ports.Length];
                var client = new TcpClient();
                try
                {
                    IAsyncResult pending = client.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (pending.AsyncWaitHandle.WaitOne(250) && client.Connected)
                    {
                        client.EndConnect(pending);
                        connectedPort = port;
                        return client;
                    }
                }
                catch
                {
                    // Nothing listening on this port; try the next.
                }
                try { client.Close(); } catch { }
            }
            // Next round, start from the launcher's first port again.
            _firstPort = 0;
            connectedPort = 0;
            return null;
        }

        private void Sleep(int milliseconds)
        {
            for (int waited = 0; waited < milliseconds && !_stop; waited += 100) Thread.Sleep(100);
        }

        /// <summary>Splits what the socket has into lines without blocking; a line that never ends closes the connection.</summary>
        private sealed class LineReader
        {
            private readonly NetworkStream _stream;
            private readonly Socket _socket;
            private readonly Decoder _decoder;
            private readonly byte[] _bytes = new byte[16384];
            private readonly char[] _chars;
            private readonly StringBuilder _line = new StringBuilder();
            private readonly Queue<string> _lines = new Queue<string>();

            public LineReader(NetworkStream stream, Socket socket, Encoding encoding)
            {
                _stream = stream;
                _socket = socket;
                _decoder = encoding.GetDecoder();
                _chars = new char[encoding.GetMaxCharCount(_bytes.Length)];
            }

            public bool Closed { get; private set; }

            public bool TryRead(out string line)
            {
                if (_lines.Count == 0 && !Closed) Fill();
                if (_lines.Count > 0)
                {
                    line = _lines.Dequeue();
                    return true;
                }
                line = null;
                return false;
            }

            private void Fill()
            {
                if (!_stream.DataAvailable)
                {
                    // A readable socket with nothing to read means the other end closed the connection.
                    if (_socket.Poll(0, SelectMode.SelectRead) && _socket.Available == 0) Closed = true;
                    return;
                }
                int read = _stream.Read(_bytes, 0, _bytes.Length);
                if (read <= 0)
                {
                    Closed = true;
                    return;
                }
                int count = _decoder.GetChars(_bytes, 0, read, _chars, 0);
                for (int i = 0; i < count; i++)
                {
                    char c = _chars[i];
                    if (c == '\n')
                    {
                        if (_line.Length > 0) _lines.Enqueue(_line.ToString());
                        _line.Length = 0;
                    }
                    else if (c != '\r')
                    {
                        _line.Append(c);
                    }
                }
                if (_line.Length > MaxLine) throw new IOException("the launcher sent a line that never ends");
            }
        }
    }
}
