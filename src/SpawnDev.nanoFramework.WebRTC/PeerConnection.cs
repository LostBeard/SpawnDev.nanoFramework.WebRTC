using System.Runtime.CompilerServices;

namespace SpawnDev.nanoFramework.WebRTC
{
    /// <summary>
    /// WebRTC data channels for .NET nanoFramework on ESP32, over libpeer (native interop). Handle based: nanoFramework
    /// interop cannot carry rich objects, so <see cref="Create"/> returns an int handle and every call takes it.
    ///
    /// <para>Threading model (learned on SpawnWear): nanoFramework's CLR is cooperatively scheduled, so no call here may
    /// block. One native FreeRTOS task pumps libpeer (ICE, DTLS, SCTP) for every handle; managed calls only touch
    /// queues and lock-free state.</para>
    ///
    /// <para>Flow (this device as the offerer, the usual case behind NAT):
    /// Create -> CreateOffer -> poll GetLocalSdpLength / GetLocalSdp -> send the offer through signaling ->
    /// SetRemoteDescription(answer) -> poll GetState until <see cref="StateCompleted"/> -> DataChannel.Open for each
    /// channel -> Send / TryReceive.</para>
    /// </summary>
    public static class PeerConnection
    {
        // libpeer PeerConnectionState values.
        public const int StateClosed = 0;
        public const int StateNew = 1;
        public const int StateChecking = 2;
        public const int StateConnected = 3;
        public const int StateCompleted = 4;
        public const int StateFailed = 5;
        public const int StateDisconnected = 6;

        // RFC 8832 data channel types.
        /// <summary>Reliable, ordered (the browser default).</summary>
        public const int ChannelReliable = 0x00;
        /// <summary>Reliable, unordered.</summary>
        public const int ChannelReliableUnordered = 0x80;
        /// <summary>Unordered with at most <c>reliabilityParameter</c> retransmissions (0 = never retransmit): video.</summary>
        public const int ChannelPartialRetransmitUnordered = 0x81;

        /// <summary><see cref="TryReceive"/> prefixes every message with its 2-byte little-endian stream id.</summary>
        public const int ReceiveHeaderBytes = 2;

        // GetStat ids.
        public const int StatTxQueuedBytes = 0;
        public const int StatTxDropped = 1;
        public const int StatRxDropped = 2;
        public const int StatFreeInternalBytes = 3;
        public const int StatFreePsramBytes = 4;
        public const int StatLargestInternalBlock = 5;
        public const int StatFramesSent = 6;
        public const int StatFramesDropped = 7;
        /// <summary>UDP datagrams the network stack refused even after short retries (lost; whole device, any handle).</summary>
        public const int StatUdpSendErrors = 8;
        /// <summary>Times a datagram had to be retried because network buffers were momentarily full (whole device).</summary>
        public const int StatUdpSendRetries = 9;
        /// <summary>SCTP chunks retransmitted because the peer did not acknowledge them in time (this connection).</summary>
        public const int StatSctpRetransmits = 10;
        /// <summary>Chunks given up on: no-retransmit (video) chunks unacknowledged after 500 ms, or reliable ones after 10 tries.</summary>
        public const int StatSctpAbandoned = 11;
        /// <summary>FORWARD-TSN chunks sent to move the peer past abandoned chunks.</summary>
        public const int StatSctpForwardTsn = 12;
        /// <summary>1 if the peer supports FORWARD-TSN (Chrome does); without it a lost video chunk leaves a gap.</summary>
        public const int StatSctpPeerForwardTsn = 13;
        /// <summary>Reliable chunks sent while the retransmission store was full (not protected).</summary>
        public const int StatSctpUnprotected = 14;
        /// <summary>Datagrams dropped on purpose by <see cref="SetTestLoss"/>.</summary>
        public const int StatTestDropped = 15;

        /// <summary>Creates a peer connection. <paramref name="iceServers"/>: space-separated STUN URLs, up to 3
        /// (e.g. "stun:stun.l.google.com:19302"). Returns a handle (>= 0) or -1.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int Create(string iceServers);

        /// <summary>Generates the SDP offer (ICE candidates embedded). Read it with GetLocalSdp* once
        /// <see cref="GetLocalSdpLength"/> is non-zero.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void CreateOffer(int handle);

        /// <summary>Applies the peer's SDP (offer or answer; libpeer infers which).</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void SetRemoteDescription(int handle, string sdp);

        /// <summary>Adds a trickled remote ICE candidate (optional when the SDP already carries them).</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void AddIceCandidate(int handle, string candidate);

        /// <summary>UTF-8 byte length of the local SDP, 0 until it has been generated.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int GetLocalSdpLength(int handle);

        /// <summary>Copies the local SDP (UTF-8) into <paramref name="buffer"/>.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void GetLocalSdp(int handle, byte[] buffer);

        /// <summary>
        /// Opens a data channel (call once <see cref="GetState"/> is <see cref="StateCompleted"/>). Returns the stream id
        /// used with <see cref="Send"/>, or -1. <paramref name="channelType"/> is one of the Channel* constants;
        /// <paramref name="reliabilityParameter"/> is the retransmit count for partial reliability.
        /// </summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int CreateDataChannel(int handle, string label, int channelType, int reliabilityParameter);

        /// <summary>Stream id of a channel by label (ours, or one the peer opened), or -1.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int GetChannelId(int handle, string label);

        /// <summary>Queues a message on a channel without blocking. Returns <paramref name="length"/>, or -1 when the queue
        /// is full (the caller decides whether to retry or drop) or the connection is not up.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int Send(int handle, int streamId, byte[] data, int length);

        /// <summary>Takes one received message: writes [u16 LE stream id][payload] into <paramref name="buffer"/> and returns
        /// the total byte count (payload + <see cref="ReceiveHeaderBytes"/>), or 0 when nothing is waiting. A message larger
        /// than the buffer is truncated (and counted in <see cref="StatRxDropped"/>).</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int TryReceive(int handle, byte[] buffer);

        /// <summary>Connection state (State* constants).</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int GetState(int handle);

        /// <summary>Diagnostics (Stat* constants). Heap stats ignore the handle.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int GetStat(int handle, int stat);

        /// <summary>
        /// Test hook: drops <paramref name="permille"/> of this connection's outgoing data datagrams on purpose (0 = off), to
        /// prove retransmission and FORWARD-TSN on a real link. Never leave it on.
        /// </summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void SetTestLoss(int handle, int permille);

        /// <summary>Closes the connection and frees the handle.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void Close(int handle);
    }
}
