using System;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace SpawnDev.nanoFramework.WebRTC
{
    /// <summary>
    /// Minimal RFC 6455 WebSocket client (text messages) for nanoFramework, enough for tracker signaling: TCP, TLS with
    /// server certificate verification, the HTTP Upgrade handshake, masked sends, fragmented-message reassembly,
    /// ping/pong. Single-threaded and blocking (with timeouts): use it from one thread.
    /// Origin: SpawnWear's SwWebSocket (which skipped certificate checks).
    /// </summary>
    public sealed class WebSocketClient : IDisposable
    {
        Socket _socket;
        SslStream _tls;
        readonly Random _rng = new Random();
        readonly byte[] _one = new byte[1];
        bool _open;

        public bool IsOpen => _open;

        /// <summary>Why the last Connect failed (for diagnostics).</summary>
        public string LastError { get; private set; } = "";

        /// <summary>
        /// Connects to a ws:// or wss:// URL. For wss://, <paramref name="rootCaPem"/> is the PEM of the root certificate
        /// the server's chain must end at (see <see cref="RootCertificates"/>); pass null together with
        /// <paramref name="allowUnverified"/> = true only for development against a self-signed server.
        /// The device clock must be right for certificate validation (nanoFramework sets it by SNTP once WiFi is up).
        /// </summary>
        public bool Connect(string url, string rootCaPem, bool allowUnverified = false, string origin = null)
        {
            LastError = "";
            try
            {
                bool secure;
                string host, path;
                int port;
                ParseUrl(url, out secure, out host, out port, out path);

                IPAddress ip = Dns.GetHostEntry(host).AddressList[0];
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                _socket.Connect(new IPEndPoint(ip, port));

                if (secure)
                {
                    _tls = new SslStream(_socket);
                    if (rootCaPem != null)
                    {
                        _tls.SslVerification = SslVerification.VerifyPeer;
                        _tls.AuthenticateAsClient(host, null, new X509Certificate(rootCaPem), SslProtocols.Tls12);
                    }
                    else if (allowUnverified)
                    {
                        _tls.SslVerification = SslVerification.NoVerification;
                        _tls.AuthenticateAsClient(host, SslProtocols.Tls12);
                    }
                    else
                    {
                        throw new ArgumentException("wss:// needs a root CA certificate (or allowUnverified for development)");
                    }
                }

                byte[] keyBytes = new byte[16];
                _rng.NextBytes(keyBytes);
                string request =
                    "GET " + path + " HTTP/1.1\r\n" +
                    "Host: " + host + ":" + port + "\r\n" +
                    "Upgrade: websocket\r\n" +
                    "Connection: Upgrade\r\n" +
                    "Sec-WebSocket-Key: " + Convert.ToBase64String(keyBytes) + "\r\n" +
                    "Sec-WebSocket-Version: 13\r\n" +
                    (origin != null ? "Origin: " + origin + "\r\n" : "") +
                    "\r\n";
                byte[] req = Encoding.UTF8.GetBytes(request);
                Write(req, req.Length);

                string head = ReadHttpHead();
                if (head == null || head.IndexOf(" 101 ") < 0)
                {
                    LastError = "upgrade refused: " + (head == null ? "no response" : head.Split('\r')[0]);
                    Dispose();
                    return false;
                }
                _open = true;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Dispose();
                return false;
            }
        }

        public bool SendText(string text)
        {
            if (!_open) return false;
            try
            {
                SendFrame(0x81, Encoding.UTF8.GetBytes(text));
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _open = false;
                return false;
            }
        }

        /// <summary>Waits up to <paramref name="timeoutMs"/> for one complete text message (fragments are joined). Returns
        /// null on timeout or close (check <see cref="IsOpen"/>). Pings are answered transparently.</summary>
        public string ReceiveText(int timeoutMs)
        {
            if (!_open) return null;
            byte[] message = null;
            try
            {
                _socket.ReceiveTimeout = timeoutMs;
                while (true)
                {
                    int b0 = ReadByte();
                    if (b0 < 0) return null;
                    bool fin = (b0 & 0x80) != 0;
                    int opcode = b0 & 0x0F;
                    int b1 = ReadByte();
                    if (b1 < 0) return null;
                    bool masked = (b1 & 0x80) != 0;
                    long len = b1 & 0x7F;
                    if (len == 126)
                    {
                        len = (ReadByte() << 8) | ReadByte();
                    }
                    else if (len == 127)
                    {
                        len = 0;
                        for (int i = 0; i < 8; i++) len = (len << 8) | (uint)ReadByte();
                    }
                    if (len > 256 * 1024) throw new Exception("websocket frame too large: " + len);
                    byte[] mask = null;
                    if (masked)
                    {
                        mask = new byte[4];
                        ReadFull(mask, 4);
                    }
                    byte[] payload = new byte[(int)len];
                    ReadFull(payload, (int)len);
                    if (masked)
                    {
                        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(payload[i] ^ mask[i & 3]);
                    }

                    if (opcode == 0x8)
                    {
                        _open = false;
                        return null;
                    }
                    if (opcode == 0x9)
                    {
                        SendFrame(0x8A, payload); // pong
                        continue;
                    }
                    if (opcode == 0xA) continue;

                    // 0x1 text / 0x2 binary start a message, 0x0 continues it.
                    message = message == null ? payload : Concat(message, payload);
                    if (fin)
                    {
                        return new string(Encoding.UTF8.GetChars(message));
                    }
                }
            }
            catch (Exception ex)
            {
                // A receive timeout lands here too; only a real failure closes the socket.
                if (!(ex is SocketException) || message != null)
                {
                    LastError = ex.Message;
                }
                return null;
            }
        }

        void SendFrame(byte first, byte[] payload)
        {
            int len = payload.Length;
            int headerLen = len <= 125 ? 2 : (len <= 0xFFFF ? 4 : 10);
            byte[] frame = new byte[headerLen + 4 + len];
            frame[0] = first;
            if (len <= 125)
            {
                frame[1] = (byte)(0x80 | len);
            }
            else if (len <= 0xFFFF)
            {
                frame[1] = 0x80 | 126;
                frame[2] = (byte)(len >> 8);
                frame[3] = (byte)len;
            }
            else
            {
                frame[1] = 0x80 | 127;
                for (int i = 0; i < 8; i++) frame[9 - i] = (byte)((long)len >> (8 * i));
            }
            byte[] mask = new byte[4];
            _rng.NextBytes(mask);
            Array.Copy(mask, 0, frame, headerLen, 4);
            for (int i = 0; i < len; i++) frame[headerLen + 4 + i] = (byte)(payload[i] ^ mask[i & 3]);
            Write(frame, frame.Length);
        }

        static byte[] Concat(byte[] a, byte[] b)
        {
            byte[] r = new byte[a.Length + b.Length];
            Array.Copy(a, 0, r, 0, a.Length);
            Array.Copy(b, 0, r, a.Length, b.Length);
            return r;
        }

        static void ParseUrl(string url, out bool secure, out string host, out int port, out string path)
        {
            if (url.StartsWith("wss://")) { secure = true; url = url.Substring(6); }
            else if (url.StartsWith("ws://")) { secure = false; url = url.Substring(5); }
            else throw new ArgumentException("url must start with ws:// or wss://");
            int slash = url.IndexOf('/');
            string authority = slash >= 0 ? url.Substring(0, slash) : url;
            path = slash >= 0 ? url.Substring(slash) : "/";
            int colon = authority.IndexOf(':');
            if (colon >= 0)
            {
                host = authority.Substring(0, colon);
                port = int.Parse(authority.Substring(colon + 1));
            }
            else
            {
                host = authority;
                port = secure ? 443 : 80;
            }
        }

        void Write(byte[] data, int count)
        {
            if (_tls != null) _tls.Write(data, 0, count);
            else _socket.Send(data, 0, count, SocketFlags.None);
        }

        int ReadByte()
        {
            int n = _tls != null ? _tls.Read(_one, 0, 1) : _socket.Receive(_one, 0, 1, SocketFlags.None);
            return n <= 0 ? -1 : _one[0];
        }

        void ReadFull(byte[] buffer, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = _tls != null ? _tls.Read(buffer, got, count - got) : _socket.Receive(buffer, got, count - got, SocketFlags.None);
                if (n <= 0) throw new Exception("websocket closed mid-frame");
                got += n;
            }
        }

        string ReadHttpHead()
        {
            _socket.ReceiveTimeout = 10000;
            var sb = new StringBuilder();
            int matched = 0;
            for (int i = 0; i < 8192; i++)
            {
                int c = ReadByte();
                if (c < 0) return null;
                sb.Append((char)c);
                bool advance = (matched == 0 || matched == 2) ? c == '\r' : c == '\n';
                matched = advance ? matched + 1 : (c == '\r' ? 1 : 0);
                if (matched == 4) return sb.ToString();
            }
            return null;
        }

        public void Dispose()
        {
            _open = false;
            try { _tls?.Close(); } catch { }
            try { _socket?.Close(); } catch { }
            _tls = null;
            _socket = null;
        }
    }
}
