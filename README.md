# SpawnDev.nanoFramework.WebRTC

**WebRTC data channels for [.NET nanoFramework](https://www.nanoframework.net/) on ESP32**, with WebTorrent-tracker signaling, so a microcontroller can talk peer to peer with browsers (Web Bluetooth-free, server-free) and with .NET desktop apps using [SpawnDev.RTC](https://github.com/LostBeard/SpawnDev.RTC).

- Managed C# API (`PeerConnection`, `TrackerSignaling`, `WebSocketClient`) over a native interop to [libpeer](https://github.com/sepfy/libpeer) (DTLS via mbedTLS, SCTP data channels).
- Several data channels per connection, each reliable or unordered/no-retransmit (e.g. control + video).
- Large messages from a PSRAM queue; a "latest frame wins" slot that other native code (a camera task) can feed through a C API without passing frames through the managed heap.
- TLS-verified signaling (root CA bundled for Let's Encrypt), binary-safe room keys.

Used by [MiniRover](https://github.com/LostBeard/MiniRover) (Freenove 4WD car) and derived from [SpawnWear](https://github.com/LostBeard/SpawnWear)'s watch firmware, where the libpeer integration was first proven.

> **Status: early.** API may change before 1.0. The libpeer fixes below are new and being verified on hardware.

## Layout

```
src/SpawnDev.nanoFramework.WebRTC/   managed nfproj (nanoFramework 2.0): PeerConnection (interop), TrackerSignaling, WebSocketClient
native/SpawnDev.nanoFramework.WebRTC/ native interop (C++) + FindINTEROP module + spawndev_nf_webrtc.h (C API)
native/components/libpeer/            libpeer, LostBeard fork branch spawndev-nf (git submodule), built as an ESP-IDF component
```

## Building it into firmware

The native part is compiled into the nanoFramework firmware (nf-interpreter). It needs two build options that live on
[LostBeard/nf-interpreter](https://github.com/LostBeard/nf-interpreter) branch `minirover/esp32-wrover`:

```
-DNF_INTEROP_ASSEMBLIES="SpawnDev.nanoFramework.WebRTC"
-DNF_INTEROP_SEARCH_PATHS=<repo>/native/SpawnDev.nanoFramework.WebRTC
-DNF_EXTRA_IDF_COMPONENT_DIRS=<repo>/native/components/libpeer
```

The sdkconfig needs `CONFIG_LWIP_IPV6=y`, `CONFIG_MBEDTLS_SSL_PROTO_DTLS=y`, `CONFIG_MBEDTLS_SSL_PROTO_TLS1_3=n`, `CONFIG_MBEDTLS_PEM_WRITE_C=y` (see MiniRover's `sdkconfig.default_minirover.esp32`). Clone with submodules: `git clone --recursive` (libpeer's own third-party submodules are marked `update = none`: the ESP-IDF build does not use them).

## libpeer fixes (branch spawndev-nf)

On top of SpawnWear's patches:
- data channel open now carries the requested channel type (it was always reliable/ordered);
- SCTP receive: all chunks in a packet, fragment reassembly, correct SACKs with gap blocks, 64 KB window (was 2 bytes);
- buffered sends keep their stream id (a second channel could never receive); direct no-copy send; DTLS role accessor.
- libpeer's built-in signaling (WHIP/MQTT) is opt-in on ESP-IDF (`LIBPEER_SIGNALING=ON`); this library signals through `TrackerSignaling` instead.

## License

MIT. libpeer is MIT (sepfy).
