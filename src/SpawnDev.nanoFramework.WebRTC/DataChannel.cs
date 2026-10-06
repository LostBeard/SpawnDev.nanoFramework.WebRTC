using System;
using System.Threading;

namespace SpawnDev.nanoFramework.WebRTC
{
    /// <summary>Managed helpers over <see cref="PeerConnection"/> (kept out of that class so its native method table,
    /// and so the interop checksum, never changes for a managed-only addition).</summary>
    public static class DataChannel
    {
        /// <summary>
        /// Opens a data channel, waiting for the SCTP association. libpeer reports ICE <see cref="PeerConnection.StateCompleted"/>
        /// before SCTP is up, and <see cref="PeerConnection.CreateDataChannel"/> refuses (-1) until it is; calling it right
        /// after Completed failed six sessions in a row on a real car (MiniRover). Returns the stream id, or -1 if the
        /// connection drops or <paramref name="timeoutMs"/> passes first.
        /// </summary>
        public static int Open(int handle, string label, int channelType, int reliabilityParameter, int timeoutMs)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                int sid = PeerConnection.CreateDataChannel(handle, label, channelType, reliabilityParameter);
                if (sid >= 0) return sid;
                if (PeerConnection.GetState(handle) != PeerConnection.StateCompleted || Environment.TickCount64 > deadline) return -1;
                Thread.Sleep(20);
            }
        }
    }
}
