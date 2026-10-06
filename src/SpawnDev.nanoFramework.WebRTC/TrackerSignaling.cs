using System;
using System.Text;

namespace SpawnDev.nanoFramework.WebRTC
{
    /// <summary>
    /// WebTorrent-tracker signaling (the protocol SpawnDev.RTC's TrackerSignalingClient speaks): peers announce into a
    /// room (info_hash = 20-byte room key) and the tracker relays offers and answers between them. JSON over
    /// <see cref="WebSocketClient"/>. info_hash, peer_id and offer_id are binary strings, one latin1 char per byte, so
    /// every byte value (0..255) is escaped and unescaped explicitly.
    /// Origin: SpawnWear's SwTrackerSignaling (hard-wired to one server, printable-ASCII room keys only).
    /// </summary>
    public sealed class TrackerSignaling : IDisposable
    {
        readonly WebSocketClient _ws = new WebSocketClient();
        readonly string _url;
        readonly string _rootCaPem;
        readonly bool _allowUnverified;

        public TrackerSignaling(string url, string rootCaPem, bool allowUnverified = false)
        {
            _url = url;
            _rootCaPem = rootCaPem;
            _allowUnverified = allowUnverified;
        }

        public bool IsOpen => _ws.IsOpen;
        public string LastError => _ws.LastError;

        public bool Connect() => _ws.Connect(_url, _rootCaPem, _allowUnverified);

        /// <summary>Announces our offer into the room.</summary>
        public bool AnnounceOffer(byte[] roomKey, byte[] peerId, byte[] offerId, string offerSdp)
        {
            var sb = new StringBuilder();
            sb.Append("{\"action\":\"announce\",\"info_hash\":\"");
            AppendBinary(sb, roomKey);
            sb.Append("\",\"peer_id\":\"");
            AppendBinary(sb, peerId);
            sb.Append("\",\"uploaded\":0,\"downloaded\":0,\"left\":1,\"event\":\"started\",\"numwant\":1,\"offers\":[{\"offer\":{\"type\":\"offer\",\"sdp\":\"");
            AppendEscaped(sb, offerSdp);
            sb.Append("\"},\"offer_id\":\"");
            AppendBinary(sb, offerId);
            sb.Append("\"}]}");
            return _ws.SendText(sb.ToString());
        }

        /// <summary>Re-announces without offers (keeps us in the room's peer list).</summary>
        public bool Announce(byte[] roomKey, byte[] peerId)
        {
            var sb = new StringBuilder();
            sb.Append("{\"action\":\"announce\",\"info_hash\":\"");
            AppendBinary(sb, roomKey);
            sb.Append("\",\"peer_id\":\"");
            AppendBinary(sb, peerId);
            sb.Append("\",\"uploaded\":0,\"downloaded\":0,\"left\":1,\"numwant\":0}");
            return _ws.SendText(sb.ToString());
        }

        /// <summary>Waits for the answer to <paramref name="offerId"/>. Returns the answer SDP and the answering peer's
        /// id (latin1 string), or null on timeout / closed socket.</summary>
        public string WaitForAnswer(byte[] offerId, int timeoutMs, out string answererPeerId)
        {
            answererPeerId = null;
            string want = BinaryToString(offerId);
            long deadline = Environment.TickCount64 + timeoutMs;
            while (_ws.IsOpen)
            {
                long left = deadline - Environment.TickCount64;
                if (left <= 0) return null;
                string msg = _ws.ReceiveText((int)left);
                if (msg == null || msg.IndexOf("\"answer\"") < 0) continue;
                string oid = ExtractString(msg, "offer_id");
                if (oid != want) continue;
                string sdp = ExtractSdpAfter(msg, "\"answer\"");
                if (sdp == null) continue;
                answererPeerId = ExtractString(msg, "peer_id");
                return sdp;
            }
            return null;
        }

        /// <summary>Sends an answer to a peer's offer.</summary>
        public bool SendAnswer(byte[] roomKey, byte[] ourPeerId, string toPeerId, string offerId, string answerSdp)
        {
            var sb = new StringBuilder();
            sb.Append("{\"action\":\"announce\",\"info_hash\":\"");
            AppendBinary(sb, roomKey);
            sb.Append("\",\"peer_id\":\"");
            AppendBinary(sb, ourPeerId);
            sb.Append("\",\"to_peer_id\":\"");
            AppendEscaped(sb, toPeerId);
            sb.Append("\",\"answer\":{\"type\":\"answer\",\"sdp\":\"");
            AppendEscaped(sb, answerSdp);
            sb.Append("\"},\"offer_id\":\"");
            AppendEscaped(sb, offerId);
            sb.Append("\"}");
            return _ws.SendText(sb.ToString());
        }

        // ---- JSON helpers (string scanning: nanoFramework has no JSON parser in the base libraries) ----

        static string ExtractSdpAfter(string msg, string marker)
        {
            int m = msg.IndexOf(marker);
            if (m < 0) return null;
            int s = msg.IndexOf("\"sdp\"", m);
            return s < 0 ? null : StringValueAt(msg, s + 5);
        }

        /// <summary>Value of a top-level "field":"value" (unescaped). "peer_id" never matches "to_peer_id" because
        /// the opening quote is part of the search.</summary>
        static string ExtractString(string msg, string field)
        {
            int f = msg.IndexOf("\"" + field + "\"");
            return f < 0 ? null : StringValueAt(msg, f + field.Length + 2);
        }

        static string StringValueAt(string msg, int afterKey)
        {
            int colon = msg.IndexOf(':', afterKey);
            if (colon < 0) return null;
            int q = msg.IndexOf('"', colon + 1);
            return q < 0 ? null : Unescape(msg, q + 1);
        }

        static string Unescape(string msg, int start)
        {
            var sb = new StringBuilder();
            for (int i = start; i < msg.Length; i++)
            {
                char c = msg[i];
                if (c == '"') break;
                if (c != '\\' || i + 1 >= msg.Length)
                {
                    sb.Append(c);
                    continue;
                }
                char n = msg[++i];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 < msg.Length)
                        {
                            sb.Append((char)Convert.ToInt32(msg.Substring(i + 1, 4), 16));
                            i += 4;
                        }
                        break;
                    default: sb.Append(n); break; // \" \\ \/
                }
            }
            return sb.ToString();
        }

        static void AppendBinary(StringBuilder sb, byte[] bytes)
        {
            for (int i = 0; i < bytes.Length; i++) AppendEscapedChar(sb, (char)bytes[i]);
        }

        static void AppendEscaped(StringBuilder sb, string s)
        {
            for (int i = 0; i < s.Length; i++) AppendEscapedChar(sb, s[i]);
        }

        /// <summary>JSON-escapes one char. Control chars and every byte above 0x7E become \u00XX, so binary ids
        /// (one latin1 char per byte) survive any JSON parser on the other end.</summary>
        static void AppendEscapedChar(StringBuilder sb, char c)
        {
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c < 0x20 || c > 0x7E)
            {
                sb.Append("\\u");
                sb.Append(((int)c).ToString("x4"));
            }
            else sb.Append(c);
        }

        public static string BinaryToString(byte[] bytes)
        {
            var chars = new char[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) chars[i] = (char)bytes[i];
            return new string(chars);
        }

        public void Dispose() => _ws.Dispose();
    }
}
