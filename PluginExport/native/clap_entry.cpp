// The `clap_entry` data symbol, built into every format's module by clap-wrapper's make_clapfirst_plugins.
#include <clap/clap.h>
#include "neo_entry.h"

extern "C" {
#ifdef __GNUC__
#pragma GCC diagnostic push
#pragma GCC diagnostic ignored "-Wattributes"
#endif
const CLAP_EXPORT struct clap_plugin_entry clap_entry = { CLAP_VERSION, neo_entry_init, neo_entry_deinit, neo_entry_get_factory };
#ifdef __GNUC__
#pragma GCC diagnostic pop
#endif
}
