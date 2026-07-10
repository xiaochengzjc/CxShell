#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct CxMacFilePromiseDescriptor
{
    const char* file_name_utf8;
    void* context;
} CxMacFilePromiseDescriptor;

typedef int (*CxMacWritePromiseCallback)(void* context, const char* destination_path_utf8);
typedef void (*CxMacPromiseCallback)(void* context);

int cxmac_drag_bridge_version(void);

int cxmac_begin_file_promise_drag(
    void* native_window_or_view,
    const CxMacFilePromiseDescriptor* descriptors,
    int descriptor_count,
    CxMacWritePromiseCallback write_callback,
    CxMacPromiseCallback cancel_callback,
    CxMacPromiseCallback release_callback,
    char* error_buffer,
    int error_buffer_size);

#ifdef __cplusplus
}
#endif
