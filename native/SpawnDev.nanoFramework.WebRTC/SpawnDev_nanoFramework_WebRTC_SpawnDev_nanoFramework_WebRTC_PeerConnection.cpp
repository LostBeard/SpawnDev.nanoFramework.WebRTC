//
// Copyright (c) 2026 Todd Tanner (LostBeard) and SpawnDev.nanoFramework.WebRTC contributors. MIT License.
//
// SpawnDev.nanoFramework.WebRTC native implementation: libpeer data channels for nanoFramework.
// (Signatures come from the MetadataProcessor stubs; *_mshl.cpp and the *.h files are generated - do not edit them.)
//
// Design, inherited from SpawnWear's proven SpawnDev.WebRTC (2026):
// - nanoFramework's CLR is cooperatively scheduled: a managed call that blocks freezes every managed thread. So ONE
//   FreeRTOS task (s_pump) runs peer_connection_loop for every handle under s_mutex, and every managed entry point that
//   can be called while a connection is live is lock-free (SPSC rings, volatile state) or takes only s_txLock, which is
//   never held across I/O.
// - libpeer callbacks fire inside peer_connection_loop (on the pump task) with user_data = the slot.
// Extended for MiniRover (2026-10):
// - several data channels per connection (sid allocation by DTLS role), each with its own reliability;
// - a 64 KB PSRAM transmit queue of variable-size messages (was 8 x 512 B);
// - a "latest frame wins" PSRAM slot fed by other native code (camera) through spawndev_nf_webrtc.h, so frames never
//   enter the managed heap and never queue up behind each other;
// - receive ring in PSRAM, each message tagged with its stream id;
// - configurable ICE servers.
//

#include "SpawnDev_nanoFramework_WebRTC.h"
#include "SpawnDev_nanoFramework_WebRTC_SpawnDev_nanoFramework_WebRTC_PeerConnection.h"
#include "spawndev_nf_webrtc.h"

// libpeer's public type is also named PeerConnection, which collides with this interop class: rename it while
// parsing peer.h so the unqualified name stays the nanoFramework class.
#define PeerConnection LpPeerConn
extern "C"
{
#include "peer.h"
}
#undef PeerConnection
typedef LpPeerConn LpPeer;

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/semphr.h"
#include "esp_heap_caps.h"

// libpeer send-path counters (libpeer socket.c; socket.h has no extern "C" guard, so declared here).
extern "C" volatile uint32_t g_udp_send_errors;
extern "C" volatile uint32_t g_udp_send_retries;
#include <string.h>

using namespace SpawnDev_nanoFramework_WebRTC::SpawnDev_nanoFramework_WebRTC;

#define SD_MAX_PEERS 2
#define SD_MAX_SDP 4096
#define SD_MAX_ICE 3
#define SD_ICE_URL_MAX 96
#define SD_MAX_CHANNELS 4
#define SD_RX_SLOTS 16
#define SD_RX_MSG_MAX 2048
#define SD_TX_RING_BYTES (64 * 1024)
#define SD_TX_HEADER 6 // [u16 sid][u32 length]
#define SD_TX_WRAP_SID 0xFFFF
#define SD_PUMP_STACK 16384 // 8192 overflowed in the DTLS handshake on SpawnWear; 32768 failed to allocate

struct SdSlot
{
    bool inUse;
    LpPeer *pc;
    PeerConfiguration config; // libpeer copies it, but user_data points back here
    char iceUrls[SD_MAX_ICE][SD_ICE_URL_MAX];

    char localSdp[SD_MAX_SDP];
    volatile int localSdpLen; // written LAST by the pump, so non-zero means complete
    volatile int state;
    uint16_t nextSid;

    // receive ring: single producer (pump, sd_on_message) / single consumer (managed TryReceive)
    uint8_t *rx;                   // SD_RX_SLOTS x (2 + SD_RX_MSG_MAX)
    volatile int rxLen[SD_RX_SLOTS]; // bytes incl. the 2-byte sid header
    volatile int rxHead;
    volatile int rxTail;
    volatile int rxDropped;

    // transmit ring of [u16 sid][u32 len][payload] records: producers serialized by s_txLock, consumer = pump
    uint8_t *tx;
    volatile uint32_t txWrite; // monotonically increasing byte positions; index = pos % SD_TX_RING_BYTES
    volatile uint32_t txRead;
    volatile int txDropped;

    // latest-frame slot: producer = sdnf_webrtc_offer_frame (any task), consumer = pump
    uint8_t *frame;
    volatile int frameLen;
    volatile uint16_t frameSid;
    volatile int framePending;
    volatile int framesSent;
    volatile int framesDropped;
};

static SdSlot s_slots[SD_MAX_PEERS];
static SemaphoreHandle_t s_mutex = NULL;  // libpeer state: pump + slot lifecycle
static SemaphoreHandle_t s_txLock = NULL; // tx ring producers; held only for a memcpy
static TaskHandle_t s_pump = NULL;
static bool s_inited = false;

static void *sd_psram_alloc(size_t size)
{
    void *p = heap_caps_malloc(size, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
    if (p == NULL)
    {
        p = heap_caps_malloc(size, MALLOC_CAP_8BIT);
    }
    return p;
}

static int sd_strlen(const char *s)
{
    // nanoPAL.h poisons strlen() (forces the safe variants); count by hand.
    int n = 0;
    while (s[n] != 0)
    {
        n++;
    }
    return n;
}

static SdSlot *sd_slot(int handle)
{
    if (handle < 0 || handle >= SD_MAX_PEERS || !s_slots[handle].inUse)
    {
        return NULL;
    }
    return &s_slots[handle];
}

static bool sd_connected(SdSlot *s)
{
    return s != NULL && s->pc != NULL && s->state == (int)PEER_CONNECTION_COMPLETED;
}

// ---- libpeer callbacks (run on the pump task) ----

static void sd_on_icecandidate(char *sdp, void *ud)
{
    SdSlot *s = (SdSlot *)ud;
    if (s == NULL || sdp == NULL)
    {
        return;
    }
    int n = sd_strlen(sdp);
    if (n >= SD_MAX_SDP)
    {
        n = SD_MAX_SDP - 1;
    }
    memcpy(s->localSdp, sdp, n);
    s->localSdp[n] = 0;
    s->localSdpLen = n;
}

static void sd_on_state(PeerConnectionState st, void *ud)
{
    SdSlot *s = (SdSlot *)ud;
    if (s != NULL)
    {
        s->state = (int)st;
    }
}

static void sd_on_message(char *msg, size_t len, void *ud, uint16_t sid)
{
    SdSlot *s = (SdSlot *)ud;
    if (s == NULL || msg == NULL || s->rx == NULL)
    {
        return;
    }
    int next = (s->rxHead + 1) % SD_RX_SLOTS;
    if (next == s->rxTail || len > SD_RX_MSG_MAX)
    {
        s->rxDropped++; // ring full or message too large: drop, never block the pump
        return;
    }
    uint8_t *dst = s->rx + s->rxHead * (2 + SD_RX_MSG_MAX);
    dst[0] = (uint8_t)(sid & 0xff);
    dst[1] = (uint8_t)(sid >> 8);
    memcpy(dst + 2, msg, len);
    s->rxLen[s->rxHead] = (int)len + 2;
    s->rxHead = next;
}

// ---- transmit ring ----

static uint32_t sd_tx_used(SdSlot *s)
{
    return s->txWrite - s->txRead;
}

// Producer side; caller holds s_txLock. Records never straddle the end of the ring: a record that would is preceded
// by a wrap marker (or by a gap shorter than a header), and the consumer skips to the start.
static bool sd_tx_push(SdSlot *s, uint16_t sid, const uint8_t *data, uint32_t len)
{
    uint32_t need = SD_TX_HEADER + len;
    if (need > SD_TX_RING_BYTES / 2)
    {
        return false;
    }
    uint32_t idx = s->txWrite % SD_TX_RING_BYTES;
    uint32_t room = SD_TX_RING_BYTES - idx;
    uint32_t pad = need > room ? room : 0;
    if (pad + need > SD_TX_RING_BYTES - sd_tx_used(s))
    {
        return false;
    }
    if (pad)
    {
        if (room >= 2)
        {
            s->tx[idx] = 0xff;
            s->tx[idx + 1] = 0xff; // wrap marker
        }
        idx = 0;
    }
    uint8_t *rec = s->tx + idx;
    rec[0] = (uint8_t)(sid & 0xff);
    rec[1] = (uint8_t)(sid >> 8);
    rec[2] = (uint8_t)(len & 0xff);
    rec[3] = (uint8_t)((len >> 8) & 0xff);
    rec[4] = (uint8_t)((len >> 16) & 0xff);
    rec[5] = (uint8_t)((len >> 24) & 0xff);
    memcpy(rec + SD_TX_HEADER, data, len);
    __sync_synchronize(); // record complete before it is published
    s->txWrite = s->txWrite + pad + need;
    return true;
}

// Consumer side (pump, holding s_mutex).
static void sd_tx_drain(SdSlot *s)
{
    while (s->txRead != s->txWrite)
    {
        uint32_t idx = s->txRead % SD_TX_RING_BYTES;
        uint32_t room = SD_TX_RING_BYTES - idx;
        if (room < SD_TX_HEADER)
        {
            s->txRead += room;
            continue;
        }
        uint8_t *rec = s->tx + idx;
        uint16_t sid = (uint16_t)(rec[0] | (rec[1] << 8));
        if (sid == SD_TX_WRAP_SID)
        {
            s->txRead += room;
            continue;
        }
        uint32_t len = rec[2] | (rec[3] << 8) | (rec[4] << 16) | ((uint32_t)rec[5] << 24);
        peer_connection_datachannel_send_sid_direct(s->pc, (char *)(rec + SD_TX_HEADER), len, sid);
        s->txRead += SD_TX_HEADER + len;
    }
}

// ---- the single pump task ----

static void sd_pump_task(void *arg)
{
    (void)arg;
    for (;;)
    {
        xSemaphoreTake(s_mutex, portMAX_DELAY);
        for (int i = 0; i < SD_MAX_PEERS; i++)
        {
            SdSlot *s = &s_slots[i];
            if (!s->inUse || s->pc == NULL)
            {
                continue;
            }
            peer_connection_loop(s->pc);
            if (s->state == (int)PEER_CONNECTION_COMPLETED)
            {
                // Control messages first, then at most one video frame per pass so controls are never stuck behind
                // a burst of frames.
                sd_tx_drain(s);
                if (s->framePending)
                {
                    peer_connection_datachannel_send_sid_direct(s->pc, (char *)s->frame, s->frameLen, s->frameSid);
                    s->framesSent++;
                    __sync_synchronize();
                    s->framePending = 0;
                }
            }
        }
        xSemaphoreGive(s_mutex);
        vTaskDelay(pdMS_TO_TICKS(1));
    }
}

static void sd_soft_reboot();

static bool sd_ensure_init()
{
    // Deduped by the HAL, and its handler table survives soft reboots, so registering on every call is harmless.
    HAL_AddSoftRebootHandler(sd_soft_reboot);
    if (s_inited)
    {
        return true;
    }
    s_mutex = xSemaphoreCreateMutex();
    s_txLock = xSemaphoreCreateMutex();
    if (s_mutex == NULL || s_txLock == NULL)
    {
        return false;
    }
    peer_init();
    if (xTaskCreate(sd_pump_task, "sdnf_webrtc", SD_PUMP_STACK, NULL, 5, &s_pump) != pdPASS)
    {
        return false;
    }
    s_inited = true;
    return true;
}

static void sd_free_buffers(SdSlot *s)
{
    if (s->rx) heap_caps_free(s->rx);
    if (s->tx) heap_caps_free(s->tx);
    if (s->frame) heap_caps_free(s->frame);
    s->rx = NULL;
    s->tx = NULL;
    s->frame = NULL;
}

// ---- interop methods ----

signed int PeerConnection::Create(const char *param0, HRESULT &hr)
{
    (void)hr;
    if (!sd_ensure_init())
    {
        return -1;
    }

    xSemaphoreTake(s_mutex, portMAX_DELAY);
    int idx = -1;
    for (int i = 0; i < SD_MAX_PEERS; i++)
    {
        if (!s_slots[i].inUse)
        {
            idx = i;
            break;
        }
    }
    if (idx < 0)
    {
        xSemaphoreGive(s_mutex);
        return -1;
    }

    SdSlot *s = &s_slots[idx];
    memset(s, 0, sizeof(*s));
    s->rx = (uint8_t *)sd_psram_alloc(SD_RX_SLOTS * (2 + SD_RX_MSG_MAX));
    s->tx = (uint8_t *)sd_psram_alloc(SD_TX_RING_BYTES);
    s->frame = (uint8_t *)sd_psram_alloc(SDNF_WEBRTC_FRAME_MAX);
    if (s->rx == NULL || s->tx == NULL || s->frame == NULL)
    {
        sd_free_buffers(s);
        xSemaphoreGive(s_mutex);
        return -1;
    }

    // ICE servers: space-separated URLs.
    int n = 0;
    const char *p = param0 != NULL ? param0 : "";
    while (*p != 0 && n < SD_MAX_ICE)
    {
        while (*p == ' ')
        {
            p++;
        }
        int len = 0;
        while (p[len] != 0 && p[len] != ' ')
        {
            len++;
        }
        if (len > 0 && len < SD_ICE_URL_MAX)
        {
            memcpy(s->iceUrls[n], p, len);
            s->iceUrls[n][len] = 0;
            s->config.ice_servers[n].urls = s->iceUrls[n];
            n++;
        }
        p += len;
    }

    s->config.audio_codec = CODEC_NONE;
    s->config.video_codec = CODEC_NONE;
    s->config.datachannel = DATA_CHANNEL_BINARY;
    s->config.user_data = s;

    s->pc = peer_connection_create(&s->config);
    if (s->pc == NULL)
    {
        sd_free_buffers(s);
        memset(s, 0, sizeof(*s));
        xSemaphoreGive(s_mutex);
        return -1;
    }
    peer_connection_oniceconnectionstatechange(s->pc, sd_on_state);
    peer_connection_onicecandidate(s->pc, sd_on_icecandidate);
    peer_connection_ondatachannel(s->pc, sd_on_message, NULL, NULL);

    s->state = (int)PEER_CONNECTION_NEW;
    s->inUse = true;
    xSemaphoreGive(s_mutex);
    return idx;
}

void PeerConnection::CreateOffer(signed int param0, HRESULT &hr)
{
    (void)hr;
    xSemaphoreTake(s_mutex, portMAX_DELAY);
    SdSlot *s = sd_slot(param0);
    if (s != NULL)
    {
        s->localSdpLen = 0;
        peer_connection_create_offer(s->pc);
    }
    xSemaphoreGive(s_mutex);
}

void PeerConnection::SetRemoteDescription(signed int param0, const char *param1, HRESULT &hr)
{
    (void)hr;
    xSemaphoreTake(s_mutex, portMAX_DELAY);
    SdSlot *s = sd_slot(param0);
    if (s != NULL && param1 != NULL)
    {
        s->localSdpLen = 0; // an answer SDP may follow (answerer role)
        peer_connection_set_remote_description(s->pc, param1);
    }
    xSemaphoreGive(s_mutex);
}

void PeerConnection::AddIceCandidate(signed int param0, const char *param1, HRESULT &hr)
{
    (void)hr;
    xSemaphoreTake(s_mutex, portMAX_DELAY);
    SdSlot *s = sd_slot(param0);
    if (s != NULL && param1 != NULL)
    {
        peer_connection_add_ice_candidate(s->pc, (char *)param1);
    }
    xSemaphoreGive(s_mutex);
}

signed int PeerConnection::GetLocalSdpLength(signed int param0, HRESULT &hr)
{
    (void)hr;
    // Lock-free: the pump holds s_mutex across ICE gathering; waiting for it here would freeze the CLR.
    SdSlot *s = sd_slot(param0);
    return s != NULL ? s->localSdpLen : 0;
}

void PeerConnection::GetLocalSdp(signed int param0, CLR_RT_TypedArray_UINT8 param1, HRESULT &hr)
{
    (void)hr;
    SdSlot *s = sd_slot(param0);
    if (s == NULL)
    {
        return;
    }
    int n = s->localSdpLen;
    int cap = (int)param1.GetSize();
    if (n > cap)
    {
        n = cap;
    }
    if (n > 0)
    {
        memcpy(param1.GetBuffer(), s->localSdp, n);
    }
}

signed int PeerConnection::CreateDataChannel(signed int param0, const char *param1, signed int param2, signed int param3,
                                             HRESULT &hr)
{
    (void)hr;
    xSemaphoreTake(s_mutex, portMAX_DELAY);
    SdSlot *s = sd_slot(param0);
    int result = -1;
    if (sd_connected(s) && param1 != NULL && s->nextSid < 2 * SD_MAX_CHANNELS)
    {
        // RFC 8832 section 6: the DTLS client uses even stream ids, the server odd ones.
        uint16_t base = peer_connection_is_dtls_server(s->pc) ? 1 : 0;
        uint16_t sid = base + s->nextSid;
        if (peer_connection_create_datachannel_sid(s->pc, (DecpChannelType)param2, 0, (uint32_t)param3, (char *)param1,
                                                   (char *)"", sid) >= 0)
        {
            s->nextSid += 2;
            result = sid;
        }
    }
    xSemaphoreGive(s_mutex);
    return result;
}

signed int PeerConnection::GetChannelId(signed int param0, const char *param1, HRESULT &hr)
{
    (void)hr;
    xSemaphoreTake(s_mutex, portMAX_DELAY);
    SdSlot *s = sd_slot(param0);
    int result = -1;
    uint16_t sid;
    if (s != NULL && param1 != NULL && peer_connection_lookup_sid(s->pc, param1, &sid) == 0)
    {
        result = sid;
    }
    xSemaphoreGive(s_mutex);
    return result;
}

signed int PeerConnection::Send(signed int param0, signed int param1, CLR_RT_TypedArray_UINT8 param2, signed int param3,
                                HRESULT &hr)
{
    (void)hr;
    SdSlot *s = sd_slot(param0);
    if (!sd_connected(s) || param3 < 0 || param3 > (int)param2.GetSize())
    {
        return -1;
    }
    // Only s_txLock (held for a memcpy), never s_mutex: Send must not wait on the DTLS/SCTP pump.
    xSemaphoreTake(s_txLock, portMAX_DELAY);
    bool ok = sd_tx_push(s, (uint16_t)param1, param2.GetBuffer(), (uint32_t)param3);
    xSemaphoreGive(s_txLock);
    if (!ok)
    {
        s->txDropped++;
        return -1;
    }
    return param3;
}

signed int PeerConnection::TryReceive(signed int param0, CLR_RT_TypedArray_UINT8 param1, HRESULT &hr)
{
    (void)hr;
    // Lock-free SPSC read (see the threading note at the top).
    SdSlot *s = sd_slot(param0);
    if (s == NULL || s->rxTail == s->rxHead)
    {
        return 0;
    }
    int idx = s->rxTail;
    int n = s->rxLen[idx];
    int cap = (int)param1.GetSize();
    if (n > cap)
    {
        n = cap;
        s->rxDropped++; // truncated
    }
    memcpy(param1.GetBuffer(), s->rx + idx * (2 + SD_RX_MSG_MAX), n);
    s->rxTail = (idx + 1) % SD_RX_SLOTS;
    return n;
}

signed int PeerConnection::GetState(signed int param0, HRESULT &hr)
{
    (void)hr;
    SdSlot *s = sd_slot(param0);
    return s != NULL ? s->state : (int)PEER_CONNECTION_CLOSED;
}

signed int PeerConnection::GetStat(signed int param0, signed int param1, HRESULT &hr)
{
    (void)hr;
    switch (param1)
    {
        case 3:
            return (signed int)heap_caps_get_free_size(MALLOC_CAP_INTERNAL);
        case 4:
            return (signed int)heap_caps_get_free_size(MALLOC_CAP_SPIRAM);
        case 5:
            return (signed int)heap_caps_get_largest_free_block(MALLOC_CAP_INTERNAL);
        case 8:
            return (signed int)g_udp_send_errors;
        case 9:
            return (signed int)g_udp_send_retries;
        default:
            break;
    }
    SdSlot *s = sd_slot(param0);
    if (s == NULL)
    {
        return -1;
    }
    switch (param1)
    {
        case 0:
            return (signed int)sd_tx_used(s);
        case 1:
            return s->txDropped;
        case 2:
            return s->rxDropped;
        case 6:
            return s->framesSent;
        case 7:
            return s->framesDropped;
        default:
            return -1;
    }
}

// Caller holds s_mutex.
static void sd_close_slot(SdSlot *s)
{
    s->inUse = false; // stops the C API before the buffers go
    if (s->pc != NULL)
    {
        peer_connection_close(s->pc);
        peer_connection_destroy(s->pc);
    }
    xSemaphoreTake(s_txLock, portMAX_DELAY);
    sd_free_buffers(s);
    memset(s, 0, sizeof(*s));
    xSemaphoreGive(s_txLock);
}

void PeerConnection::Close(signed int param0, HRESULT &hr)
{
    (void)hr;
    xSemaphoreTake(s_mutex, portMAX_DELAY);
    SdSlot *s = sd_slot(param0);
    if (s != NULL)
    {
        sd_close_slot(s);
    }
    xSemaphoreGive(s_mutex);
}

// A CLR soft reboot (deploy, debugger restart, Power.RebootDevice's CLR-only path) restarts managed code but keeps
// native state. Without this, the old program's connections keep their slots and ~190 KB of buffers each, and the
// new program's Create fails ("no slot / out of memory").
static void sd_soft_reboot()
{
    if (!s_inited)
    {
        return;
    }
    xSemaphoreTake(s_mutex, portMAX_DELAY);
    for (int i = 0; i < SD_MAX_PEERS; i++)
    {
        if (s_slots[i].inUse)
        {
            sd_close_slot(&s_slots[i]);
        }
    }
    xSemaphoreGive(s_mutex);
}

// ---- C API for other native code (spawndev_nf_webrtc.h) ----

extern "C" int sdnf_webrtc_offer_frame(int handle, uint16_t sid, const uint8_t *data, size_t len)
{
    SdSlot *s = sd_slot(handle);
    if (!sd_connected(s) || data == NULL || len == 0 || len > SDNF_WEBRTC_FRAME_MAX)
    {
        return -1;
    }
    if (s->framePending)
    {
        s->framesDropped++; // the previous frame is still on its way: latest wins, never queue
        return 0;
    }
    // Only this producer writes the slot while framePending == 0; the pump reads it only after the flag is set.
    xSemaphoreTake(s_txLock, portMAX_DELAY); // serializes against Close freeing the buffer
    if (s->frame == NULL)
    {
        xSemaphoreGive(s_txLock);
        return -1;
    }
    memcpy(s->frame, data, len);
    s->frameLen = (int)len;
    s->frameSid = sid;
    __sync_synchronize();
    s->framePending = 1;
    xSemaphoreGive(s_txLock);
    return 1;
}

extern "C" int sdnf_webrtc_is_connected(int handle)
{
    return sd_connected(sd_slot(handle)) ? 1 : 0;
}
