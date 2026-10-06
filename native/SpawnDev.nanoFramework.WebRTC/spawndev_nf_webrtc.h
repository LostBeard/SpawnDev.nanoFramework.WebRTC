//
// Copyright (c) 2026 Todd Tanner (LostBeard) and SpawnDev.nanoFramework.WebRTC contributors. MIT License.
//
// C API for OTHER native code in the same firmware (e.g. a camera task) to send on a SpawnDev.nanoFramework.WebRTC
// peer connection without passing data through the managed heap. Handles and stream ids are the ones managed code
// gets from PeerConnection.Create / CreateDataChannel.
//
#ifndef SPAWNDEV_NF_WEBRTC_H
#define SPAWNDEV_NF_WEBRTC_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C"
{
#endif

    // Largest frame sdnf_webrtc_offer_frame accepts.
#define SDNF_WEBRTC_FRAME_MAX (96 * 1024)

    // Offers the newest frame (e.g. a JPEG) for sending on stream `sid`. "Latest wins": if the previous frame has not
    // been handed to the transport yet, this one is DROPPED (counted) so video never builds up delay. The data is copied
    // once into a PSRAM slot; the caller may reuse its buffer on return. Thread safe, never blocks.
    // Returns 1 = accepted, 0 = dropped (slot busy), -1 = invalid handle / too large / not connected.
    int sdnf_webrtc_offer_frame(int handle, uint16_t sid, const uint8_t *data, size_t len);

    // 1 when the connection is up and data channels can carry data.
    int sdnf_webrtc_is_connected(int handle);

#ifdef __cplusplus
}
#endif

#endif // SPAWNDEV_NF_WEBRTC_H
