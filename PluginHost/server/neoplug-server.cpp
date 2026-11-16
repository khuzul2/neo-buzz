// neoplug-server: hosts plug-ins for the host in a process of its own (docs/specs/plugin-host.md H2). Started by the library
// (np_open_ex with a sandbox placement), never by hand: neoplug-server <control lane> <host pid>.
#include "../neoplug/np_proto.h"

#include <cstdio>
#if !defined(_WIN32)
#include <unistd.h>
#endif

int main(int argc, char** argv)
{
    int rc = np::proto::serve(argc, argv);
    std::fflush(stdout);
    std::fflush(stderr);
    // End at once: the plug-ins are closed; their modules' static destructors have nothing to add (and some hang).
#if defined(_WIN32)
    TerminateProcess(GetCurrentProcess(), (UINT)rc);
#else
    _exit(rc);
#endif
    return rc;
}
