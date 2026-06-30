#pragma once

#include <stdint.h>

#if defined(_WIN32)
#if defined(CX_RDP_BRIDGE_EXPORTS)
#define CX_RDP_API __declspec(dllexport)
#else
#define CX_RDP_API __declspec(dllimport)
#endif
#else
#define CX_RDP_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef void (*cxrdp_frame_callback)(void* user_data, int width, int height, int stride, const uint8_t* bgra_pixels);
typedef void (*cxrdp_status_callback)(void* user_data, const char* message);
typedef void (*cxrdp_disconnect_callback)(void* user_data);
typedef void (*cxrdp_clipboard_text_callback)(void* user_data, const char* text);

CX_RDP_API void* cxrdp_create(void);
CX_RDP_API void cxrdp_destroy(void* handle);
CX_RDP_API void cxrdp_set_callbacks(
    void* handle,
    cxrdp_frame_callback frame_callback,
    cxrdp_status_callback status_callback,
    cxrdp_disconnect_callback disconnect_callback,
    void* user_data);
CX_RDP_API int cxrdp_connect(
    void* handle,
    const char* host,
    int port,
    const char* username,
    const char* password,
    int width,
    int height);
CX_RDP_API void cxrdp_disconnect(void* handle);
CX_RDP_API void cxrdp_send_pointer(void* handle, uint16_t flags, uint16_t x, uint16_t y);
CX_RDP_API void cxrdp_send_key(void* handle, uint32_t key, int down);
CX_RDP_API void cxrdp_send_unicode_key(void* handle, uint16_t code, int down);
CX_RDP_API void cxrdp_set_clipboard_callback(void* handle, cxrdp_clipboard_text_callback clipboard_text_callback);
CX_RDP_API void cxrdp_set_clipboard_text(void* handle, const char* text);
CX_RDP_API const char* cxrdp_get_last_error(void* handle);

#ifdef __cplusplus
}
#endif
