// neoplug-scan (docs/specs/plugin-host.md §3.7): lists what one plugin module holds, in a process of
// its own, so a plugin that crashes or hangs while it loads costs the scanner and not the app.
//
//   neoplug-scan <module>            one JSON object on stdout: {"path":..., "classes":[...]}
//   neoplug-scan --light <module>    the classes only: nothing is instantiated
//
// Each class is also opened, activated for one block and closed (unless --light), which is where
// plugins fail; its parameter and bus counts come from that. Exit code 0: scanned; 1: the module
// did not load (an error is in the JSON); a crash or a timeout is the caller's to see.
#include "neoplug.h"

#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

namespace {

std::string esc(const char* s)
{
    std::string o;
    for (const unsigned char* p = (const unsigned char*)s; *p; p++)
    {
        if (*p == '"' || *p == '\\') { o += '\\'; o += (char)*p; }
        else if (*p == '\n') o += "\\n";
        else if (*p < 0x20) { char b[8]; snprintf(b, sizeof b, "\\u%04x", *p); o += b; }
        else o += (char)*p;
    }
    return o;
}

} // namespace

int main(int argc, char** argv)
{
    bool light = false;
    const char* path = nullptr;
    for (int i = 1; i < argc; i++)
    {
        if (!strcmp(argv[i], "--light")) light = true;
        else path = argv[i];
    }
    if (!path)
    {
        fprintf(stderr, "usage: neoplug-scan [--light] <module>\n");
        return 2;
    }
    std::vector<np_class_info> classes(64);
    char err[512] = {};
    int n = np_list(path, classes.data(), (int)classes.size(), err, sizeof err);
    if (n < 0)
    {
        printf("{\"path\":\"%s\",\"error\":\"%s\",\"classes\":[]}\n", esc(path).c_str(), esc(err).c_str());
        return 1;
    }
    if (n > (int)classes.size())
    {
        classes.resize((size_t)n);
        n = np_list(path, classes.data(), n, err, sizeof err);
    }
    printf("{\"path\":\"%s\",\"classes\":[", esc(path).c_str());
    for (int i = 0; i < n; i++)
    {
        const np_class_info& c = classes[(size_t)i];
        int params = -1, ins = -1, outs = -1, aux = -1, latency = 0;
        std::string problem;
        if (!light)
        {
            char e[512] = {};
            np_plugin* p = np_open(path, c.id, e, sizeof e);
            if (!p) problem = e[0] ? e : "did not open";
            else
            {
                np_info(p, const_cast<np_class_info*>(&c));   // an instrument by its buses, too
                if (np_activate(p, 48000, 256, 0, e, sizeof e) != 0) problem = std::string("activate: ") + e;
                else
                {
                    params = np_param_count(p);
                    ins = np_channels(p, 1);
                    outs = np_channels(p, 0);
                    aux = np_aux_channels(p);
                    latency = np_latency(p);
                    np_deactivate(p);
                }
                np_close(p);
            }
        }
        printf("%s{\"format\":\"%s\",\"id\":\"%s\",\"name\":\"%s\",\"vendor\":\"%s\",\"version\":\"%s\",\"category\":\"%s\","
               "\"instrument\":%s,\"params\":%d,\"inputs\":%d,\"outputs\":%d,\"aux\":%d,\"latency\":%d,\"problem\":\"%s\"}",
               i ? "," : "", c.format == NP_FORMAT_CLAP ? "clap" : "vst3", esc(c.id).c_str(), esc(c.name).c_str(), esc(c.vendor).c_str(),
               esc(c.version).c_str(), esc(c.category).c_str(), c.is_instrument ? "true" : "false", params, ins, outs, aux, latency,
               esc(problem.c_str()).c_str());
    }
    printf("]}\n");
    fflush(stdout);
    return 0;
}
