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

        /// <summary>Closes the connection and frees the handle.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void Close(int handle);
    }
}
