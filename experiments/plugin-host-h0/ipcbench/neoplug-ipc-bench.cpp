// neoplug-ipc-bench (H0 spike, docs/specs/plugin-host.md §3.4): what a sandboxed plugin costs per
// block. A toy server process shares memory with the client (stereo audio in and out, 256 bytes of
// events); per block the client writes its input, wakes the server, the server applies a gain and
// wakes the client back. Measured against the same copies done in-process.
//
// Wake mechanisms:  spin (both sides busy-wait) · kernel (both sides sleep on an OS primitive) ·
// hybrid (the server sleeps between blocks; the client spins for the reply, then sleeps).
// OS primitives:    Linux futex, POSIX semaphore · macOS os_sync_wait_on_address ("ulock"), POSIX
// named semaphore, Mach semaphore · Windows auto-reset events.
// Pacing:           back-to-back (next block at once) and paced (one block per period, as an
// audio callback runs: the server is idle between blocks, which is when kernel wake-ups are slow).
//
//   neoplug-ipc-bench [--frames 64,128,256] [--rate 48000] [--blocks 20000] [--paced-seconds 4]
//                     [--mechs all|spin,futex,...] [--json out.json] [--no-rt]
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

#if defined(_WIN32)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <avrt.h>
#else
#include <fcntl.h>
#include <pthread.h>
#include <sched.h>
#include <semaphore.h>
#include <signal.h>
#include <sys/mman.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>
#endif
#if defined(__linux__)
#include <linux/futex.h>
#include <sys/syscall.h>
#endif
#if defined(__APPLE__)
#include <dlfcn.h>
#include <mach/mach.h>
#include <mach/mach_time.h>
#include <mach/semaphore.h>
#include <mach/thread_policy.h>
#include <sys/sysctl.h>
#endif
#if defined(__x86_64__) || defined(_M_X64)
#include <immintrin.h>
#define CPU_RELAX() _mm_pause()
#elif defined(__aarch64__) || defined(_M_ARM64)
#define CPU_RELAX() __asm__ __volatile__("yield")
#else
#define CPU_RELAX()
#endif

namespace {

constexpr int MaxFrames = 1024;
constexpr int EventBytes = 256;

enum Mech { MSpin, MFutex, MPosixSem, MUlock, MMachSem, MWinEvent };
enum Policy { PSpin, PKernel, PHybrid };

const char* mechName(Mech m)
{
    switch (m)
    {
    case MSpin: return "spin";
    case MFutex: return "futex";
    case MPosixSem: return "posix-sem";
    case MUlock: return "os_sync_wait_on_address";
    case MMachSem: return "mach-semaphore";
    case MWinEvent: return "win-event";
    }
    return "?";
}

struct alignas(64) Shared
{
    alignas(64) std::atomic<uint32_t> reqSeq;
    alignas(64) std::atomic<uint32_t> respSeq;
    alignas(64) std::atomic<uint32_t> serverSleeping;
    alignas(64) std::atomic<uint32_t> clientSleeping;
    alignas(64) std::atomic<uint32_t> quit;
    std::atomic<uint32_t> ready;
    std::atomic<uint32_t> generation;   // a server exits when this changes (no stale servers)
    uint32_t replyTag;                  // the block number the server last answered
    uint32_t frames;
    int32_t mech, policy;
    int32_t rtServer;
    float gain;
#if defined(__linux__)
    sem_t semReq, semResp;
#endif
    alignas(64) uint8_t events[EventBytes];
    alignas(64) float in[2][MaxFrames];
    alignas(64) float out[2][MaxFrames];
};

// --- OS primitives --------------------------------------------------------------------------------

struct Prims
{
    Shared* sh = nullptr;
#if defined(__APPLE__)
    sem_t* psReq = nullptr;
    sem_t* psResp = nullptr;
    semaphore_t msReq = 0, msResp = 0;
    int (*waitOnAddress)(void*, uint64_t, size_t, uint32_t) = nullptr;
    int (*wakeAny)(void*, size_t, uint32_t) = nullptr;
#endif
#if defined(_WIN32)
    HANDLE evReq = nullptr, evResp = nullptr;
#endif
};

Prims P;

#if defined(__APPLE__)
constexpr uint32_t OS_SYNC_SHARED_WAIT = 0x1;   // OS_SYNC_WAIT_ON_ADDRESS_SHARED
constexpr uint32_t OS_SYNC_SHARED_WAKE = 0x1;   // OS_SYNC_WAKE_BY_ADDRESS_SHARED
#endif

bool mechAvailable(Mech m)
{
    switch (m)
    {
    case MSpin: return true;
#if defined(__linux__)
    case MFutex: return true;
    case MPosixSem: return true;
#endif
#if defined(__APPLE__)
    case MPosixSem: return true;
    case MUlock: return P.waitOnAddress && P.wakeAny;
    case MMachSem: return true;
#endif
#if defined(_WIN32)
    case MWinEvent: return true;
#endif
    default: return false;
    }
}

// Sleeps until *seq changes from 'seen' (or a post arrives), direction: req (server waits) or resp.
void kernelWait(Mech m, bool req, std::atomic<uint32_t>& seq, uint32_t seen)
{
    switch (m)
    {
#if defined(__linux__)
    case MFutex: syscall(SYS_futex, (uint32_t*)&seq, FUTEX_WAIT, seen, nullptr, nullptr, 0); break;
    case MPosixSem: while (sem_wait(req ? &P.sh->semReq : &P.sh->semResp) != 0) {} break;
#endif
#if defined(__APPLE__)
    case MUlock: P.waitOnAddress((void*)&seq, seen, 4, OS_SYNC_SHARED_WAIT); break;
    case MPosixSem: while (sem_wait(req ? P.psReq : P.psResp) != 0) {} break;
    case MMachSem: semaphore_wait(req ? P.msReq : P.msResp); break;
#endif
#if defined(_WIN32)
    case MWinEvent: WaitForSingleObject(req ? P.evReq : P.evResp, INFINITE); break;
#endif
    default: (void)req; (void)seq; (void)seen; break;
    }
}

void kernelWake(Mech m, bool req, std::atomic<uint32_t>& seq)
{
    switch (m)
    {
#if defined(__linux__)
    case MFutex: syscall(SYS_futex, (uint32_t*)&seq, FUTEX_WAKE, 1, nullptr, nullptr, 0); break;
    case MPosixSem: sem_post(req ? &P.sh->semReq : &P.sh->semResp); break;
#endif
#if defined(__APPLE__)
    case MUlock: P.wakeAny((void*)&seq, 4, OS_SYNC_SHARED_WAKE); break;
    case MPosixSem: sem_post(req ? P.psReq : P.psResp); break;
    case MMachSem: semaphore_signal(req ? P.msReq : P.msResp); break;
#endif
#if defined(_WIN32)
    case MWinEvent: SetEvent(req ? P.evReq : P.evResp); break;
#endif
    default: (void)req; (void)seq; break;
    }
}

using Clock = std::chrono::steady_clock;

// Waits until seq == want: spins up to spinNs (−1 = forever), then sleeps on the primitive.
void waitFor(std::atomic<uint32_t>& seq, uint32_t want, std::atomic<uint32_t>& sleeping, Mech m, bool req, int64_t spinNs)
{
    if (seq.load(std::memory_order_acquire) == want) return;
    if (spinNs != 0)
    {
        auto start = Clock::now();
        int n = 0;
        while (true)
        {
            if (seq.load(std::memory_order_acquire) == want) return;
            CPU_RELAX();
            if (spinNs > 0 && ++n % 64 == 0 && std::chrono::duration_cast<std::chrono::nanoseconds>(Clock::now() - start).count() > spinNs) break;
            if (spinNs < 0 && P.sh->quit.load(std::memory_order_relaxed)) return;
        }
    }
    sleeping.store(1, std::memory_order_seq_cst);
    while (true)
    {
        uint32_t v = seq.load(std::memory_order_seq_cst);
        if (v == want || P.sh->quit.load(std::memory_order_relaxed)) break;
        kernelWait(m, req, seq, v);
    }
    sleeping.store(0, std::memory_order_relaxed);
}

void post(std::atomic<uint32_t>& seq, uint32_t value, std::atomic<uint32_t>& sleeping, Mech m, bool req)
{
    seq.store(value, std::memory_order_seq_cst);
    if (m != MSpin && sleeping.load(std::memory_order_seq_cst)) kernelWake(m, req, seq);
}

// --- Real-time priority -------------------------------------------------------------------------

std::string rtNote;

bool makeRealtime(double periodSec)
{
#if defined(__linux__)
    sched_param sp{};
    sp.sched_priority = 80;
    if (pthread_setschedparam(pthread_self(), SCHED_FIFO, &sp) == 0) return true;
    return false;
#elif defined(__APPLE__)
    mach_timebase_info_data_t tb;
    mach_timebase_info(&tb);
    double toAbs = (double)tb.denom / tb.numer * 1e9;
    thread_time_constraint_policy_data_t pol;
    pol.period = (uint32_t)(periodSec * toAbs);
    pol.computation = (uint32_t)(std::min(periodSec * 0.5, 0.0005) * toAbs);
    pol.constraint = (uint32_t)(periodSec * toAbs);
    pol.preemptible = 1;
    return thread_policy_set(mach_thread_self(), THREAD_TIME_CONSTRAINT_POLICY, (thread_policy_t)&pol, THREAD_TIME_CONSTRAINT_POLICY_COUNT) == KERN_SUCCESS;
#elif defined(_WIN32)
    (void)periodSec;
    DWORD task = 0;
    bool mm = AvSetMmThreadCharacteristicsW(L"Pro Audio", &task) != nullptr;
    bool tp = SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL) != 0;
    return mm || tp;
#else
    return false;
#endif
}

// --- The block ------------------------------------------------------------------------------------

void serverProcess(Shared* sh)
{
    uint32_t n = sh->frames;
    float g = sh->gain;
    volatile uint8_t sink = 0;
    for (int i = 0; i < EventBytes; i += 16) sink ^= sh->events[i];
    for (uint32_t i = 0; i < n; i++)
    {
        sh->out[0][i] = sh->in[0][i] * g;
        sh->out[1][i] = sh->in[1][i] * g;
    }
}

void serverLoop(Shared* sh)
{
    Mech m = (Mech)sh->mech;
    Policy pol = (Policy)sh->policy;
    double period = (double)sh->frames / 48000.0;
    sh->rtServer = makeRealtime(period) ? 1 : 0;
    sh->ready.store(1);
    uint32_t last = sh->reqSeq.load();
    uint32_t gen = sh->generation.load();
    int64_t spin = pol == PSpin ? -1 : 0;   // the server sleeps between blocks unless spinning
    while (!sh->quit.load(std::memory_order_relaxed) && sh->generation.load(std::memory_order_relaxed) == gen)
    {
        waitFor(sh->reqSeq, last + 1, sh->serverSleeping, m, true, spin);
        if (sh->quit.load(std::memory_order_relaxed) || sh->generation.load(std::memory_order_relaxed) != gen) break;
        last++;
        serverProcess(sh);
        sh->replyTag = last;
        post(sh->respSeq, last, sh->clientSleeping, m, false);
    }
}

// --- Process management -------------------------------------------------------------------------

#if !defined(_WIN32)
pid_t serverPid = 0;
#endif
#if defined(_WIN32)
HANDLE serverProcessHandle = nullptr;
std::wstring sharedName;

Shared* mapShared(bool create, const std::wstring& name)
{
    HANDLE h = create ? CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(Shared), name.c_str())
                      : OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, name.c_str());
    if (!h) return nullptr;
    return (Shared*)MapViewOfFile(h, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(Shared));
}

void openEvents(bool create, const std::wstring& base)
{
    std::wstring a = base + L"-req", b = base + L"-resp";
    P.evReq = create ? CreateEventW(nullptr, FALSE, FALSE, a.c_str()) : OpenEventW(EVENT_ALL_ACCESS, FALSE, a.c_str());
    P.evResp = create ? CreateEventW(nullptr, FALSE, FALSE, b.c_str()) : OpenEventW(EVENT_ALL_ACCESS, FALSE, b.c_str());
}
#endif

bool startServer(Shared* sh)
{
    sh->ready.store(0);
#if defined(_WIN32)
    wchar_t exe[MAX_PATH];
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    std::wstring cmd = L"\"" + std::wstring(exe) + L"\" --server " + sharedName;
    STARTUPINFOW si{};
    si.cb = sizeof si;
    PROCESS_INFORMATION pi{};
    if (!CreateProcessW(nullptr, cmd.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &si, &pi)) return false;
    CloseHandle(pi.hThread);
    serverProcessHandle = pi.hProcess;
#else
    pid_t pid = fork();
    if (pid < 0) return false;
    if (pid == 0)
    {
#if defined(__APPLE__)
        if ((Mech)sh->mech == MMachSem)
        {
            mach_port_array_t ports = nullptr;
            mach_msg_type_number_t count = 0;
            if (mach_ports_lookup(mach_task_self(), &ports, &count) == KERN_SUCCESS && count >= 2)
            {
                P.msReq = ports[0];
                P.msResp = ports[1];
            }
        }
#endif
        serverLoop(sh);
        _exit(0);
    }
    serverPid = pid;
#endif
    auto t0 = Clock::now();
    while (!sh->ready.load())
    {
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
        if (Clock::now() - t0 > std::chrono::seconds(10)) return false;
    }
    return true;
}

void stopServer(Shared* sh)
{
    sh->quit.store(1);
    Mech m = (Mech)sh->mech;
    for (int i = 0; i < 4; i++) kernelWake(m, true, sh->reqSeq);
#if defined(_WIN32)
    if (WaitForSingleObject(serverProcessHandle, 5000) != WAIT_OBJECT_0) TerminateProcess(serverProcessHandle, 1);
    CloseHandle(serverProcessHandle);
#else
    int status = 0;
    bool reaped = false;
    for (int i = 0; i < 200 && !reaped; i++)
    {
        if (waitpid(serverPid, &status, WNOHANG) == serverPid) reaped = true;
        else
        {
            for (int k = 0; k < 2; k++) kernelWake(m, true, sh->reqSeq);
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
        }
    }
    if (!reaped)
    {
        fprintf(stderr, "server did not stop; killed\n");
        kill(serverPid, SIGKILL);
        waitpid(serverPid, &status, 0);
    }
#endif
    sh->generation.fetch_add(1);
}

// --- Measurement --------------------------------------------------------------------------------

struct Stats { double mean, p50, p90, p99, p999, max; size_t n; };

Stats summarize(std::vector<double>& v)
{
    Stats s{};
    s.n = v.size();
    if (v.empty()) return s;
    std::sort(v.begin(), v.end());
    double sum = 0;
    for (double x : v) sum += x;
    auto pct = [&](double p) { return v[std::min(v.size() - 1, (size_t)(p * (v.size() - 1) + 0.5))]; };
    s.mean = sum / v.size();
    s.p50 = pct(0.5);
    s.p90 = pct(0.9);
    s.p99 = pct(0.99);
    s.p999 = pct(0.999);
    s.max = v.back();
    return s;
}

void sleepUntil(Clock::time_point t)
{
#if defined(_WIN32)
    static HANDLE timer = CreateWaitableTimerExW(nullptr, nullptr, 0x2 /* CREATE_WAITABLE_TIMER_HIGH_RESOLUTION */, TIMER_ALL_ACCESS);
    auto now = Clock::now();
    if (t <= now) return;
    auto ns = std::chrono::duration_cast<std::chrono::nanoseconds>(t - now).count();
    if (timer && ns > 200000)
    {
        LARGE_INTEGER due;
        due.QuadPart = -(LONGLONG)((ns - 150000) / 100);
        SetWaitableTimer(timer, &due, 0, nullptr, nullptr, FALSE);
        WaitForSingleObject(timer, INFINITE);
    }
    while (Clock::now() < t) CPU_RELAX();
#else
    auto now = Clock::now();
    if (t <= now) return;
    auto ns = std::chrono::duration_cast<std::chrono::nanoseconds>(t - now).count();
    if (ns > 100000) std::this_thread::sleep_for(std::chrono::nanoseconds(ns - 60000));
    while (Clock::now() < t) CPU_RELAX();
#endif
}

// Client side of one block: copy in, wake, wait, copy out.
std::vector<float> localIn[2], localOut[2];
uint8_t localEvents[EventBytes];

uint32_t badReplies = 0;

inline void clientBlock(Shared* sh, uint32_t seq, Mech m, int64_t clientSpinNs)
{
    uint32_t n = sh->frames;
    std::memcpy(sh->events, localEvents, EventBytes);
    std::memcpy(sh->in[0], localIn[0].data(), n * 4);
    std::memcpy(sh->in[1], localIn[1].data(), n * 4);
    post(sh->reqSeq, seq, sh->serverSleeping, m, true);
    waitFor(sh->respSeq, seq, sh->clientSleeping, m, false, clientSpinNs);
    if (sh->replyTag != seq) badReplies++;
    std::memcpy(localOut[0].data(), sh->out[0], n * 4);
    std::memcpy(localOut[1].data(), sh->out[1], n * 4);
}

// The in-process placement: the same copies into the plugin's buffers and back, as a call.
struct InProc { uint8_t events[EventBytes]; float in[2][MaxFrames], out[2][MaxFrames]; float gain; uint32_t frames; };
#if defined(_MSC_VER)
__declspec(noinline)
#else
__attribute__((noinline))
#endif
void inprocCall(InProc* s)
{
    volatile uint8_t sink = 0;
    for (int i = 0; i < EventBytes; i += 16) sink ^= s->events[i];
    for (uint32_t i = 0; i < s->frames; i++)
    {
        s->out[0][i] = s->in[0][i] * s->gain;
        s->out[1][i] = s->in[1][i] * s->gain;
    }
}

struct Result { std::string mech, policy, pacing; int frames; Stats st; bool rtClient, rtServer; uint32_t bad = 0; };

std::string json(const Result& r)
{
    char b[512];
    snprintf(b, sizeof b,
             "{\"mechanism\":\"%s\",\"policy\":\"%s\",\"pacing\":\"%s\",\"frames\":%d,\"n\":%zu,\"mean_us\":%.2f,\"p50_us\":%.2f,\"p90_us\":%.2f,"
             "\"p99_us\":%.2f,\"p999_us\":%.2f,\"max_us\":%.2f,\"rt_client\":%s,\"rt_server\":%s,\"bad_replies\":%u}",
             r.mech.c_str(), r.policy.c_str(), r.pacing.c_str(), r.frames, r.st.n, r.st.mean, r.st.p50, r.st.p90, r.st.p99, r.st.p999, r.st.max,
             r.rtClient ? "true" : "false", r.rtServer ? "true" : "false", r.bad);
    return b;
}

std::string cpuName()
{
#if defined(__APPLE__)
    char buf[256] = {};
    size_t len = sizeof buf;
    if (sysctlbyname("machdep.cpu.brand_string", buf, &len, nullptr, 0) == 0) return buf;
    return "?";
#elif defined(_WIN32)
    const char* p = getenv("PROCESSOR_IDENTIFIER");
    return p ? p : "?";
#else
    FILE* f = fopen("/proc/cpuinfo", "r");
    char line[512];
    std::string name = "?";
    while (f && fgets(line, sizeof line, f))
        if (!strncmp(line, "model name", 10))
        {
            const char* c = strchr(line, ':');
            if (c) name = std::string(c + 2);
            while (!name.empty() && (name.back() == '\n' || name.back() == '\r')) name.pop_back();
            break;
        }
    if (f) fclose(f);
    return name;
#endif
}

const char* osName()
{
#if defined(__APPLE__)
    return "macos";
#elif defined(_WIN32)
    return "windows";
#else
    return "linux";
#endif
}

} // namespace

int main(int argc, char** argv)
{
#if defined(__APPLE__)
    P.waitOnAddress = (int (*)(void*, uint64_t, size_t, uint32_t))dlsym(RTLD_DEFAULT, "os_sync_wait_on_address");
    P.wakeAny = (int (*)(void*, size_t, uint32_t))dlsym(RTLD_DEFAULT, "os_sync_wake_by_address_any");
#endif
#if defined(_WIN32)
    if (argc >= 3 && !strcmp(argv[1], "--server"))
    {
        std::wstring name;
        for (const char* c = argv[2]; *c; c++) name += (wchar_t)*c;
        Shared* sh = mapShared(false, name);
        if (!sh) return 3;
        P.sh = sh;
        openEvents(false, name);
        serverLoop(sh);
        return 0;
    }
#endif
    std::vector<int> framesList = {64, 128, 256};
    double rate = 48000, pacedSeconds = 4;
    int blocks = 20000;
    std::string jsonPath, mechArg = "all";
    bool rt = true;
    for (int i = 1; i < argc; i++)
    {
        std::string a = argv[i];
        auto next = [&]() -> std::string { return i + 1 < argc ? argv[++i] : ""; };
        if (a == "--frames")
        {
            framesList.clear();
            std::string s = next();
            for (size_t p = 0; p < s.size();)
            {
                size_t c = s.find(',', p);
                framesList.push_back(atoi(s.substr(p, c - p).c_str()));
                if (c == std::string::npos) break;
                p = c + 1;
            }
        }
        else if (a == "--rate") rate = atof(next().c_str());
        else if (a == "--blocks") blocks = atoi(next().c_str());
        else if (a == "--paced-seconds") pacedSeconds = atof(next().c_str());
        else if (a == "--json") jsonPath = next();
        else if (a == "--mechs") mechArg = next();
        else if (a == "--no-rt") rt = false;
    }

    // Shared memory, mapped before the server starts.
#if defined(_WIN32)
    sharedName = L"Local\\neoplug-bench-" + std::to_wstring(GetCurrentProcessId());
    Shared* sh = mapShared(true, sharedName);
    openEvents(true, sharedName);
#else
    Shared* sh = (Shared*)mmap(nullptr, sizeof(Shared), PROT_READ | PROT_WRITE, MAP_SHARED | MAP_ANONYMOUS, -1, 0);
    if (sh == MAP_FAILED) sh = nullptr;
#endif
    if (!sh) { fprintf(stderr, "no shared memory\n"); return 1; }
    new (sh) Shared();
    P.sh = sh;
#if defined(__linux__)
    sem_init(&sh->semReq, 1, 0);
    sem_init(&sh->semResp, 1, 0);
#endif
#if defined(__APPLE__)
    std::string base = "/npb" + std::to_string(getpid());
    sem_unlink((base + "q").c_str());
    sem_unlink((base + "p").c_str());
    P.psReq = sem_open((base + "q").c_str(), O_CREAT | O_EXCL, 0600, 0);
    P.psResp = sem_open((base + "p").c_str(), O_CREAT | O_EXCL, 0600, 0);
    sem_unlink((base + "q").c_str());   // stays open in both processes
    sem_unlink((base + "p").c_str());
    semaphore_create(mach_task_self(), &P.msReq, SYNC_POLICY_FIFO, 0);
    semaphore_create(mach_task_self(), &P.msResp, SYNC_POLICY_FIFO, 0);
    // Mach port rights are not inherited across fork, except the registered ports.
    mach_port_t reg[2] = {P.msReq, P.msResp};
    bool machOk = mach_ports_register(mach_task_self(), reg, 2) == KERN_SUCCESS;
    if (!machOk) fprintf(stderr, "mach_ports_register failed\n");
#endif

    for (int c = 0; c < 2; c++)
    {
        localIn[c].assign(MaxFrames, 0.f);
        localOut[c].assign(MaxFrames, 0.f);
        for (int i = 0; i < MaxFrames; i++) localIn[c][i] = (float)std::sin(i * 0.05 + c);
    }
    for (int i = 0; i < EventBytes; i++) localEvents[i] = (uint8_t)i;

    std::vector<Mech> mechs;
    std::vector<Mech> all = {MSpin, MFutex, MPosixSem, MUlock, MMachSem, MWinEvent};
    for (Mech m : all)
        if (mechAvailable(m) && (mechArg == "all" || mechArg.find(mechName(m)) != std::string::npos)) mechs.push_back(m);
    // The OS's best address-based primitive for the hybrid policy.
    Mech hybridMech = MSpin;
    for (Mech m : {MFutex, MUlock, MWinEvent})
        if (mechAvailable(m)) { hybridMech = m; break; }

    std::vector<Result> results;
    bool rtClient = false;
    for (int frames : framesList)
    {
        double period = frames / rate;
        if (rt) rtClient = makeRealtime(period);

        // In-process baseline.
        {
            InProc ip{};
            ip.gain = 0.5f;
            ip.frames = (uint32_t)frames;
            std::vector<double> t;
            for (int k = 0; k < blocks + 1000; k++)
            {
                auto t0 = Clock::now();
                std::memcpy(ip.events, localEvents, EventBytes);
                std::memcpy(ip.in[0], localIn[0].data(), frames * 4);
                std::memcpy(ip.in[1], localIn[1].data(), frames * 4);
                inprocCall(&ip);
                std::memcpy(localOut[0].data(), ip.out[0], frames * 4);
                std::memcpy(localOut[1].data(), ip.out[1], frames * 4);
                double us = std::chrono::duration<double, std::micro>(Clock::now() - t0).count();
                if (k >= 1000) t.push_back(us);
            }
            results.push_back({"in-process call", "-", "back-to-back", frames, summarize(t), rtClient, false});
        }

        struct Config { Mech m; Policy p; };
        std::vector<Config> configs;
        for (Mech m : mechs) configs.push_back({m, m == MSpin ? PSpin : PKernel});
        if (hybridMech != MSpin) configs.push_back({hybridMech, PHybrid});

        for (auto cfg : configs)
            for (int paced = 0; paced < 2; paced++)
            {
                sh->reqSeq.store(0);
                sh->respSeq.store(0);
                sh->serverSleeping.store(0);
                sh->clientSleeping.store(0);
                sh->quit.store(0);
                sh->frames = (uint32_t)frames;
                sh->mech = cfg.m;
                sh->policy = cfg.p;
                sh->gain = 0.5f;
                if (!startServer(sh))
                {
                    fprintf(stderr, "server for %s did not start\n", mechName(cfg.m));
                    continue;
                }
                int64_t clientSpin = cfg.p == PSpin ? -1 : cfg.p == PHybrid ? 200000 : 0;   // hybrid: spin up to 200 µs
                int n = paced ? std::max(200, (int)(pacedSeconds / period)) : blocks;
                badReplies = 0;
                int warm = paced ? 50 : 1000;
                std::vector<double> t;
                t.reserve(n);
                auto next = Clock::now();
                auto periodNs = std::chrono::nanoseconds((int64_t)(period * 1e9));
                for (int k = 0; k < n + warm; k++)
                {
                    if (paced)
                    {
                        next += periodNs;
                        sleepUntil(next);
                    }
                    auto t0 = Clock::now();
                    clientBlock(sh, (uint32_t)(k + 1), cfg.m, clientSpin);
                    double us = std::chrono::duration<double, std::micro>(Clock::now() - t0).count();
                    if (k >= warm) t.push_back(us);
                }
                // Check the server really processed: out = in * 0.5.
                if (std::fabs(localOut[0][frames - 1] - localIn[0][frames - 1] * 0.5f) > 1e-6f)
                    fprintf(stderr, "warning: %s returned wrong audio\n", mechName(cfg.m));
                bool rts = sh->rtServer != 0;
                stopServer(sh);
                const char* pol = cfg.p == PSpin ? "spin" : cfg.p == PKernel ? "kernel" : "hybrid";
                results.push_back({mechName(cfg.m), pol, paced ? "paced" : "back-to-back", frames, summarize(t), rtClient, rts, badReplies});
                if (badReplies) fprintf(stderr, "warning: %u replies were not for the block asked\n", badReplies);
                fprintf(stderr, "%-24s %-6s %-12s %4d frames: p50 %7.2f us  p99 %7.2f us  max %8.2f us\n", mechName(cfg.m), pol,
                        paced ? "paced" : "back-to-back", frames, results.back().st.p50, results.back().st.p99, results.back().st.max);
            }
    }

    std::string out = std::string("{\"os\":\"") + osName() + "\",\"cpu\":\"" + cpuName() + "\",\"cores\":" +
                      std::to_string(std::thread::hardware_concurrency()) + ",\"rate\":" + std::to_string((int)rate) + ",\"results\":[";
    for (size_t i = 0; i < results.size(); i++) out += (i ? ",\n" : "\n") + json(results[i]);
    out += "\n]}\n";
    printf("%s", out.c_str());
    if (!jsonPath.empty())
    {
        FILE* f = fopen(jsonPath.c_str(), "w");
        if (f) { fputs(out.c_str(), f); fclose(f); }
    }
    return 0;
}
