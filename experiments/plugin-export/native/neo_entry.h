#pragma once
#include <stdbool.h>
#ifdef __cplusplus
extern "C" {
#endif
bool neo_entry_init(const char* plugin_path);
void neo_entry_deinit(void);
const void* neo_entry_get_factory(const char* factory_id);
#ifdef __cplusplus
}
#endif
