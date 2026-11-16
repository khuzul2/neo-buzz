// neoplug sandbox: lanes, shared memory, process start (docs/specs/plugin-host.md H2; the wait strategy is H0 §8.4's).
#include "np_ipc.h"

#include <algorithm>
#include <cerrno>
#include <chrono>
#include <cstdio>
#include <cstdlib>

#if defined(_WIN32)
#include <avrt.h>
#else
#include <dlfcn.h>
#include <fcntl.h>
#include <pthread.h>
#include <sched.h>
#include <signal.h>
#include <spawn.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <unistd.h>
extern char** environ;
#endif
#if defined(__linux__)
#include <linux/futex.h>
#include <sys/syscall.h>
#include <time.h>
#endif
#if defined(__APPLE__)
#include <mach/mach.h>
#include <mach/mach_time.h>
#include <mach/semaphore.h>
#include <mach/thread_policy.h>
#include <servers/bootstrap.h>
#include <os/workgroup.h>
#endif

namespace np { namespace ipc {

using Clock = std::chrono::steady_clock;

static int64_t sinceNs(Clock::time_point t) { return std::chrono::duration_cast<std::chrono::nanoseconds>(Clock::now() - t).count(); }

// --- Sleeping and waking ---------------------------------------------------------------------------------------

static void kernelWait(Word& w, uint32_t seen, Wake& wake, int sliceMs)
{
#if defined(__linux__)
    (void)wake;
    timespec ts{sliceMs / 1000, (long)(sliceMs % 1000) * 1000000L};
    syscall(SYS_futex, (uint32_t*)&w.seq, FUTEX_WAIT, seen, &ts, nullptr, 0);   // shared (not PRIVATE): the other process wakes it
#elif defined(_WIN32)
    (void)w; (void)seen;
    WaitForSingleObject((HANDLE)wake.ev, (DWORD)sliceMs);
#elif defined(__APPLE__)
    (void)w; (void)seen;
    mach_timespec_t ts{(unsigned)(sliceMs / 1000), (clock_res_t)((sliceMs % 1000) * 1000000)};
    semaphore_timedwait((semaphore_t)wake.sem, ts);
#endif
}

static void kernelWake(Word& w, Wake& wake)
{
#if defined(__linux__)
    (void)wake;
    syscall(SYS_futex, (uint32_t*)&w.seq, FUTEX_WAKE, 1, nullptr, nullptr, 0);
#elif defined(_WIN32)
    (void)w;
    if (wake.ev) SetEvent((HANDLE)wake.ev);
#elif defined(__APPLE__)
    (void)w;
    if (wake.sem) semaphore_signal((semaphore_t)wake.sem);
#endif
}

void post(Word& w, uint32_t value, Wake& wake)
{
    w.seq.store(value, std::memory_order_seq_cst);
    if (w.sleeping.load(std::memory_order_seq_cst)) kernelWake(w, wake);
}

void wakeOnly(Word& w, Wake& wake) { kernelWake(w, wake); }

int wait(Word& w, uint32_t want, Wake& wake, int64_t spinNs, int timeoutMs, const std::atomic<int>* abort)
{
    if (w.seq.load(std::memory_order_acquire) == want) return WaitOk;
    auto start = Clock::now();
#if !defined(__APPLE__)
    // The hybrid: a short spin for a quick reply, never for ever. macOS sleeps at once (spinning competes with the server for the cores).
    if (spinNs > 0)
    {
        int n = 0;
        for (;;)
        {
            if (w.seq.load(std::memory_order_acquire) == want) return WaitOk;
            if (abort && abort->load(std::memory_order_relaxed)) return WaitAborted;
            cpuRelax();
            if (++n % 32 == 0 && sinceNs(start) > spinNs) break;
        }
    }
#else
    (void)spinNs;
#endif
    w.sleeping.store(1, std::memory_order_seq_cst);
    int result;
    for (;;)
    {
        uint32_t v = w.seq.load(std::memory_order_seq_cst);
        if (v == want) { result = WaitOk; break; }
        if (abort && abort->load(std::memory_order_relaxed)) { result = WaitAborted; break; }
        int remain = -1;
        if (timeoutMs >= 0)
        {
            remain = timeoutMs - (int)(sinceNs(start) / 1000000);
            if (remain <= 0) { result = WaitTimeout; break; }
        }
        int slice = remain < 0 ? 1000 : std::min(remain, 1000);
        if (abort) slice = std::min(slice, 50);
        kernelWait(w, v, wake, slice);
    }
    w.sleeping.store(0, std::memory_order_relaxed);
    return result;
}

// --- Shared memory ---------------------------------------------------------------------------------------------

std::string uniqueName(const char* kind)
{
    static std::atomic<uint32_t> counter{0};
    uint32_t n = counter.fetch_add(1);
    static const uint32_t salt = (uint32_t)(Clock::now().time_since_epoch().count() & 0xffff);
    char buf[64];
#if defined(_WIN32)
    std::snprintf(buf, sizeof buf, "Local\\np-%s-%lx-%x-%x", kind, (unsigned long)GetCurrentProcessId(), salt, n);
#else
    std::snprintf(buf, sizeof buf, "/np%s%x.%x.%x", kind, (unsigned)getpid(), salt, n);   // macOS limits a name to 31 characters
#endif
    return buf;
}

#if defined(_WIN32)
static std::wstring wide(const std::string& s)
{
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, nullptr, 0);
    std::wstring w((size_t)(n > 0 ? n : 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, w.data(), n);
    w.resize(n > 0 ? (size_t)n - 1 : 0);
    return w;
}

bool Shm::create(const std::string& n, size_t sz)
{
    HANDLE h = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, (DWORD)((uint64_t)sz >> 32), (DWORD)(sz & 0xffffffffu), wide(n).c_str());
    if (!h) return false;
    ptr = MapViewOfFile(h, FILE_MAP_ALL_ACCESS, 0, 0, sz);
    if (!ptr) { CloseHandle(h); return false; }
    handle = h; size = sz; name = n; owner = true;
    std::memset(ptr, 0, sz);
    return true;
}

bool Shm::open(const std::string& n, size_t sz)
{
    HANDLE h = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, wide(n).c_str());
    if (!h) return false;
    ptr = MapViewOfFile(h, FILE_MAP_ALL_ACCESS, 0, 0, sz);
    if (!ptr) { CloseHandle(h); return false; }
    handle = h; size = sz; name = n; owner = false;
    return true;
}

void Shm::close()
{
    if (ptr) UnmapViewOfFile(ptr);
    if (handle) CloseHandle((HANDLE)handle);
    ptr = nullptr; handle = nullptr;
}

bool makeEvent(Wake& wake, const std::string& name, bool create)
{
    std::wstring w = wide(name);
    wake.ev = create ? CreateEventW(nullptr, FALSE, FALSE, w.c_str()) : OpenEventW(EVENT_ALL_ACCESS, FALSE, w.c_str());
    return wake.ev != nullptr;
}

void closeEvent(Wake& wake)
{
    if (wake.ev) CloseHandle((HANDLE)wake.ev);
    wake.ev = nullptr;
}
#else
bool Shm::create(const std::string& n, size_t sz)
{
    int f = shm_open(n.c_str(), O_CREAT | O_EXCL | O_RDWR, 0600);
    if (f < 0) return false;
    if (ftruncate(f, (off_t)sz) != 0) { ::close(f); shm_unlink(n.c_str()); return false; }
    void* p = mmap(nullptr, sz, PROT_READ | PROT_WRITE, MAP_SHARED, f, 0);
    if (p == MAP_FAILED) { ::close(f); shm_unlink(n.c_str()); return false; }
    ptr = p; fd = f; size = sz; name = n; owner = true;
    std::memset(ptr, 0, sz);
    return true;
}

bool Shm::open(const std::string& n, size_t sz)
{
    int f = shm_open(n.c_str(), O_RDWR, 0600);
    if (f < 0) return false;
    void* p = mmap(nullptr, sz, PROT_READ | PROT_WRITE, MAP_SHARED, f, 0);
    if (p == MAP_FAILED) { ::close(f); return false; }
    ptr = p; fd = f; size = sz; name = n; owner = false;
    return true;
}

void Shm::close()
{
    if (ptr) munmap(ptr, size);
    if (fd >= 0) ::close(fd);
    if (owner && !name.empty()) shm_unlink(name.c_str());
    ptr = nullptr; fd = -1;
}
#endif

// --- Processes ---------------------------------------------------------------------------------------------------

std::string serverPath()
{
    if (const char* env = std::getenv("NEOPLUG_SERVER"))
        if (*env) return env;
#if defined(_WIN32)
    HMODULE m = nullptr;
    wchar_t buf[MAX_PATH * 2];
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)&serverPath, &m)) return "";
    DWORD n = GetModuleFileNameW(m, buf, MAX_PATH * 2);
    std::wstring dir(buf, n);
    size_t slash = dir.find_last_of(L"\\/");
    if (slash == std::wstring::npos) return "";
    dir.resize(slash + 1);
    std::wstring exe = dir + L"neoplug-server.exe";
    if (GetFileAttributesW(exe.c_str()) == INVALID_FILE_ATTRIBUTES) return "";
    int len = WideCharToMultiByte(CP_UTF8, 0, exe.c_str(), -1, nullptr, 0, nullptr, nullptr);
    std::string out((size_t)len, '\0');
    WideCharToMultiByte(CP_UTF8, 0, exe.c_str(), -1, out.data(), len, nullptr, nullptr);
    out.resize((size_t)len - 1);
    return out;
#else
    Dl_info info;
    if (!dladdr((void*)&serverPath, &info) || !info.dli_fname) return "";
    std::string dir = info.dli_fname;
    size_t slash = dir.find_last_of('/');
    dir = slash == std::string::npos ? "./" : dir.substr(0, slash + 1);
    std::string exe = dir + "neoplug-server";
    return access(exe.c_str(), X_OK) == 0 ? exe : "";
#endif
}

#if defined(_WIN32)
static HANDLE killJob()
{
    static HANDLE job = []() -> HANDLE {
        HANDLE j = CreateJobObjectW(nullptr, nullptr);
        if (!j) return nullptr;
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION li{};
        li.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        SetInformationJobObject(j, JobObjectExtendedLimitInformation, &li, sizeof li);
        return j;
    }();
    return job;
}

bool spawn(const std::string& exe, const std::vector<std::string>& args, Child& out, std::string& err)
{
    std::wstring cmd = L"\"" + wide(exe) + L"\"";
    for (auto& a : args) cmd += L" \"" + wide(a) + L"\"";
    STARTUPINFOW si{};
    si.cb = sizeof si;
    PROCESS_INFORMATION pi{};
    if (!CreateProcessW(nullptr, cmd.data(), nullptr, nullptr, FALSE, CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi))
    {
        err = "could not start " + exe + " (error " + std::to_string(GetLastError()) + ")";
        return false;
    }
    CloseHandle(pi.hThread);
    if (HANDLE job = killJob()) AssignProcessToJobObject(job, pi.hProcess);   // the server dies with the host
    out.process = pi.hProcess;
    out.pid = pi.dwProcessId;
    return true;
}

void waitExit(Child& c)
{
    if (!c.process) return;
    WaitForSingleObject((HANDLE)c.process, INFINITE);
    DWORD code = 0;
    if (GetExitCodeProcess((HANDLE)c.process, &code)) c.code = (long)code;
}
bool isAlive(Child& c) { return c.process && WaitForSingleObject((HANDLE)c.process, 0) == WAIT_TIMEOUT; }
void kill(Child& c) { if (c.process) TerminateProcess((HANDLE)c.process, 1); }
void closeChild(Child& c) { if (c.process) CloseHandle((HANDLE)c.process); c.process = nullptr; }

void audioThread(double)
{
    DWORD idx = 0;
    AvSetMmThreadCharacteristicsW(L"Pro Audio", &idx);
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
}
#else
bool spawn(const std::string& exe, const std::vector<std::string>& args, Child& out, std::string& err)
{
    std::vector<char*> argv;
    std::string e = exe;
    argv.push_back(e.data());
    std::vector<std::string> copy = args;
    for (auto& a : copy) argv.push_back(a.data());
    argv.push_back(nullptr);
    posix_spawnattr_t attr;
    posix_spawnattr_init(&attr);
#if defined(__APPLE__)
    // The special port: the child's bootstrap port is the host's rendezvous port (MachHost::handshake needs it to be set by the caller).
    extern uint32_t g_np_spawn_bootstrap;
    if (g_np_spawn_bootstrap) posix_spawnattr_setspecialport_np(&attr, (mach_port_t)g_np_spawn_bootstrap, TASK_BOOTSTRAP_PORT);
#endif
    pid_t pid = -1;
    int rc = posix_spawn(&pid, exe.c_str(), nullptr, &attr, argv.data(), environ);
    posix_spawnattr_destroy(&attr);
    if (rc != 0)
    {
        err = "could not start " + exe + " (" + std::strerror(rc) + ")";
        return false;
    }
    out.pid = (int)pid;
    return true;
}

void waitExit(Child& c)
{
    int st = 0;
    pid_t r = -1;
    while (c.pid > 0 && (r = waitpid(c.pid, &st, 0)) < 0 && errno == EINTR) { }
    if (c.pid > 0 && r == c.pid)
    {
        if (WIFEXITED(st)) c.code = WEXITSTATUS(st);
        if (WIFSIGNALED(st)) c.signal = WTERMSIG(st);
    }
}

bool isAlive(Child& c)
{
    if (c.pid <= 0) return false;
    int st = 0;
    pid_t r = waitpid(c.pid, &st, WNOHANG);
    if (r == 0) return true;
    c.pid = -1;
    return false;
}

void kill(Child& c) { if (c.pid > 0) ::kill(c.pid, SIGKILL); }
void closeChild(Child&) {}

#if defined(__APPLE__)
void audioThread(double periodSec)
{
    mach_timebase_info_data_t tb;
    mach_timebase_info(&tb);
    double toAbs = 1e9 * (double)tb.denom / (double)tb.numer;
    thread_time_constraint_policy_data_t pol{};
    pol.period = (uint32_t)(periodSec * toAbs);
    pol.computation = (uint32_t)(periodSec * 0.5 * toAbs);
    pol.constraint = (uint32_t)(periodSec * 0.9 * toAbs);
    pol.preemptible = 1;
    thread_policy_set(mach_thread_self(), THREAD_TIME_CONSTRAINT_POLICY, (thread_policy_t)&pol, THREAD_TIME_CONSTRAINT_POLICY_COUNT);
}
#else
void audioThread(double)
{
    sched_param sp{};
    sp.sched_priority = 20;
    pthread_setschedparam(pthread_self(), SCHED_FIFO, &sp);   // best effort: needs the right to (root, or an rtprio limit)
}
#endif
#endif

// --- macOS: semaphores and the Mach handover ---------------------------------------------------------------------

#if defined(__APPLE__)
uint32_t g_np_spawn_bootstrap = 0;

uint32_t newSemaphore()
{
    semaphore_t s = 0;
    return semaphore_create(mach_task_self(), &s, SYNC_POLICY_FIFO, 0) == KERN_SUCCESS ? (uint32_t)s : 0;
}

void freeSemaphore(uint32_t sem) { if (sem) semaphore_destroy(mach_task_self(), (semaphore_t)sem); }

namespace {
constexpr int MaxPorts = 4;
struct PortMsg
{
    mach_msg_header_t h;
    mach_msg_body_t body;
    mach_msg_port_descriptor_t d[MaxPorts];
};
struct RecvMsg
{
    PortMsg m;
    mach_msg_trailer_t t;
};

kern_return_t sendPorts(mach_port_t dest, uint32_t id, const uint32_t* ports, int n, int timeoutMs)
{
    PortMsg m{};
    m.h.msgh_remote_port = dest;
    m.h.msgh_local_port = MACH_PORT_NULL;
    m.h.msgh_id = (mach_msg_id_t)id;
    if (n > 0)
    {
        m.h.msgh_bits = MACH_MSGH_BITS(MACH_MSG_TYPE_COPY_SEND, 0) | MACH_MSGH_BITS_COMPLEX;
        m.h.msgh_size = (mach_msg_size_t)(sizeof(mach_msg_header_t) + sizeof(mach_msg_body_t) + (size_t)n * sizeof(mach_msg_port_descriptor_t));
        m.body.msgh_descriptor_count = (mach_msg_size_t)n;
        for (int i = 0; i < n; i++)
        {
            m.d[i].name = (mach_port_t)ports[i];
            m.d[i].disposition = MACH_MSG_TYPE_COPY_SEND;
            m.d[i].type = MACH_MSG_PORT_DESCRIPTOR;
        }
    }
    else
    {
        m.h.msgh_bits = MACH_MSGH_BITS(MACH_MSG_TYPE_COPY_SEND, 0);
        m.h.msgh_size = sizeof(mach_msg_header_t);
    }
    return mach_msg(&m.h, MACH_SEND_MSG | MACH_SEND_TIMEOUT, m.h.msgh_size, 0, MACH_PORT_NULL, (mach_msg_timeout_t)timeoutMs, MACH_PORT_NULL);
}

// Returns KERN_SUCCESS, MACH_RCV_TIMED_OUT, or another error.
kern_return_t recvPorts(mach_port_t from, int timeoutMs, uint32_t& id, uint32_t* ports, int& n)
{
    RecvMsg r{};
    mach_msg_option_t opt = MACH_RCV_MSG | (timeoutMs >= 0 ? MACH_RCV_TIMEOUT : 0);
    kern_return_t kr = mach_msg(&r.m.h, opt, 0, sizeof r, from, timeoutMs >= 0 ? (mach_msg_timeout_t)timeoutMs : MACH_MSG_TIMEOUT_NONE, MACH_PORT_NULL);
    if (kr != KERN_SUCCESS) return kr;
    id = (uint32_t)r.m.h.msgh_id;
    n = 0;
    if (r.m.h.msgh_bits & MACH_MSGH_BITS_COMPLEX)
    {
        int c = (int)r.m.body.msgh_descriptor_count;
        for (int i = 0; i < c && i < MaxPorts; i++) ports[n++] = (uint32_t)r.m.d[i].name;
    }
    return KERN_SUCCESS;
}
}

bool MachHost::handshake(Child& c, uint32_t ctrlRespSemaphore, std::string& err)
{
    // The rendezvous port was made and given to spawn() by the caller; the server says hello there.
    uint32_t id = 0, ports[MaxPorts];
    int n = 0;
    kern_return_t kr = KERN_FAILURE;
    for (int waited = 0; waited < 20000; waited += 100)
    {
        kr = recvPorts((mach_port_t)rendezvous, 100, id, ports, n);
        if (kr == KERN_SUCCESS && id == 1 && n >= 1) break;       // (other messages are the child's own bootstrap lookups: ignored)
        if (kr != KERN_SUCCESS && kr != MACH_RCV_TIMED_OUT) break;
        kr = KERN_FAILURE;
        if (!isAlive(c)) { err = "the server ended before it said hello"; return false; }
    }
    if (kr != KERN_SUCCESS || n < 1) { err = "the server did not say hello on the rendezvous port"; return false; }
    serverPort = ports[0];
    mach_port_t realBoot = MACH_PORT_NULL;
    task_get_special_port(mach_task_self(), TASK_BOOTSTRAP_PORT, &realBoot);
    uint32_t setup[2] = {ctrlRespSemaphore, (uint32_t)realBoot};
    kr = sendPorts((mach_port_t)serverPort, 2, setup, 2, 5000);
    if (kr != KERN_SUCCESS) { err = "could not send the setup to the server (Mach error " + std::to_string((int)kr) + ")"; return false; }
    ctrlRespSem = ctrlRespSemaphore;
    return true;
}

bool MachHost::ring(uint32_t id, const uint32_t* ports, int nports)
{
    return sendPorts((mach_port_t)serverPort, id, ports, nports, 2000) == KERN_SUCCESS;
}

bool MachServer::hello(std::string& err)
{
    mach_port_t self = mach_task_self(), boot = MACH_PORT_NULL;
    if (task_get_special_port(self, TASK_BOOTSTRAP_PORT, &boot) != KERN_SUCCESS || boot == MACH_PORT_NULL) { err = "no bootstrap port"; return false; }
    mach_port_t q = MACH_PORT_NULL;
    if (mach_port_allocate(self, MACH_PORT_RIGHT_RECEIVE, &q) != KERN_SUCCESS) { err = "mach_port_allocate failed"; return false; }
    mach_port_insert_right(self, q, q, MACH_MSG_TYPE_MAKE_SEND);
    requestPort = q;
    uint32_t p[1] = {(uint32_t)q};
    kern_return_t kr = sendPorts(boot, 1, p, 1, 5000);
    if (kr != KERN_SUCCESS) { err = "hello failed (Mach error " + std::to_string((int)kr) + ")"; return false; }
    uint32_t id = 0, ports[MaxPorts];
    int n = 0;
    kr = recvPorts(q, 15000, id, ports, n);
    if (kr != KERN_SUCCESS || id != 2 || n < 2) { err = "no setup message (Mach error " + std::to_string((int)kr) + ")"; return false; }
    ctrlRespSem = ports[0];
    // Back to the real bootstrap port: the window server (Cocoa) and the rest of the system are reached through it.
    task_set_special_port(self, TASK_BOOTSTRAP_PORT, (mach_port_t)ports[1]);
    bootstrap_port = (mach_port_t)ports[1];
    return true;
}

int MachServer::waitDoorbell(int timeoutMs, uint32_t& id, uint32_t* ports, int& nports)
{
    kern_return_t kr = recvPorts((mach_port_t)requestPort, timeoutMs, id, ports, nports);
    if (kr == KERN_SUCCESS) return 0;
    return kr == MACH_RCV_TIMED_OUT ? 1 : -1;
}

bool joinWorkgroup(uint32_t port, double periodSec)
{
    if (!port) return false;
    static thread_local os_workgroup_t wg = nullptr;
    static thread_local uint32_t forPort = 0;
    static thread_local os_workgroup_join_token_s token;
    static thread_local bool joined = false;
    if (joined && forPort == port) return true;
    if (!wg || forPort != port) wg = os_workgroup_create_with_port("neoplug", (mach_port_t)port);
    if (!wg) return false;
    forPort = port;
    audioThread(periodSec);   // joining needs a real-time thread
    if (os_workgroup_join(wg, &token) != 0) return false;
    joined = true;
    return true;
}
#endif

}} // namespace np::ipc
