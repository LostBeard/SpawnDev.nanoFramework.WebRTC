//-----------------------------------------------------------------------------
//
//                   ** WARNING! ** 
//    This file was generated automatically by a tool.
//    Re-running the tool will overwrite this file.
//    You should copy this file to a custom location
//    before adding any customization in the copy to
//    prevent loss of your changes when the tool is
//    re-run.
//
//-----------------------------------------------------------------------------

#ifndef SPAWNDEV_NANOFRAMEWORK_WEBRTC_SPAWNDEV_NANOFRAMEWORK_WEBRTC_SPAWNDEV_NANOFRAMEWORK_WEBRTC_PEERCONNECTION_H
#define SPAWNDEV_NANOFRAMEWORK_WEBRTC_SPAWNDEV_NANOFRAMEWORK_WEBRTC_SPAWNDEV_NANOFRAMEWORK_WEBRTC_PEERCONNECTION_H

namespace SpawnDev_nanoFramework_WebRTC
{
    namespace SpawnDev_nanoFramework_WebRTC
    {
        struct PeerConnection
        {
            // Helper Functions to access fields of managed object
            // Declaration of stubs. These functions are implemented by Interop code developers

            static signed int Create( const char* param0, HRESULT &hr );

            static void CreateOffer( signed int param0, HRESULT &hr );

            static void SetRemoteDescription( signed int param0, const char* param1, HRESULT &hr );

            static void AddIceCandidate( signed int param0, const char* param1, HRESULT &hr );

            static signed int GetLocalSdpLength( signed int param0, HRESULT &hr );

            static void GetLocalSdp( signed int param0, CLR_RT_TypedArray_UINT8 param1, HRESULT &hr );

            static signed int CreateDataChannel( signed int param0, const char* param1, signed int param2, signed int param3, HRESULT &hr );

            static signed int GetChannelId( signed int param0, const char* param1, HRESULT &hr );

            static signed int Send( signed int param0, signed int param1, CLR_RT_TypedArray_UINT8 param2, signed int param3, HRESULT &hr );

            static signed int TryReceive( signed int param0, CLR_RT_TypedArray_UINT8 param1, HRESULT &hr );

            static signed int GetState( signed int param0, HRESULT &hr );

            static signed int GetStat( signed int param0, signed int param1, HRESULT &hr );

            static void SetTestLoss( signed int param0, signed int param1, HRESULT &hr );

            static void Close( signed int param0, HRESULT &hr );

        };
    }
}

#endif // SPAWNDEV_NANOFRAMEWORK_WEBRTC_SPAWNDEV_NANOFRAMEWORK_WEBRTC_SPAWNDEV_NANOFRAMEWORK_WEBRTC_PEERCONNECTION_H
