// neoplug-server's loop (docs/specs/plugin-host.md H2). It calls only the library's C ABI, in process: the plug-ins here are ordinary
// in-process plug-ins of this process, and this file is the other half of the protocol (np_remote.cpp is the host's).
// The main thread answers the control lane, idles the plug-ins and pumps the platform's events (so it is the plug-ins' main thread and
// their editors' thread); each instance has an audio thread that sleeps on its lane between blocks (it never spins).
#include "np_proto.h"

#include <algorithm>
#include <chrono>
#include <deque>
#include <map>
#include <memory>
#include <thread>

#if defined(_WIN32)
#else
#include <unistd.h>
#endif
#if defined(__linux__)
#include <sys/prctl.h>
#include <signal.h>
#endif

using namespace np;
using namespace np::proto;

namespace {

struct Inst
{
    int id = 0;
    np_plugin* p = nullptr;
    ipc::Shm shm;
    Audio* a = nullptr;
    ipc::Wake reqW, respW;
    std::thread audio;
    std::atomic<int> stop{0};
    std::vector<uint32_t> ids;
    uint32_t gen = 1;
    std::vector<uint8_t> staging;
    void* window = nullptr;
    double period = 0.0027;
};

std::map<int, std::unique_ptr<Inst>> insts;
int nextId = 1;
Ctrl* ctrl = nullptr;
ipc::Wake ctrlReq, ctrlResp;
std::atomic<uint32_t> workgroupPort{0};
#if defined(__APPLE__)
ipc::MachServer mach;
std::deque<std::pair<uint32_t, uint32_t>> pendingSems;
#endif

void audioLoop(Inst* in)
{
    ipc::audioThread(in->period);
    uint32_t seen = 0;
    while (!in->stop.load())
    {
#if defined(__APPLE__)
        if (uint32_t wg = workgroupPort.load()) ipc::joinWorkgroup(wg, in->period);
#endif
        int w = ipc::wait(in->a->req, seen + 1, in->reqW, 0, 200, &in->stop);
        if (w == ipc::WaitTimeout) continue;
        if (w == ipc::WaitAborted) break;
        seen++;
        Audio* a = in->a;
        const float* ins[2] = {a->in[0], a->in[1]};
        const float* auxs[2] = {a->aux[0], a->aux[1]};
        float* outs[2] = {a->out[0], a->out[1]};
        np_process_io io{};
        io.in = a->inCh > 0 ? ins : nullptr; io.in_ch = a->inCh;
        io.aux = a->auxCh > 0 ? auxs : nullptr; io.aux_ch = a->auxCh;
        io.out = outs; io.out_ch = a->outCh > 0 ? a->outCh : 2;
        io.frames = (int32_t)a->frames;
        io.events = a->events; io.nevents = a->nev;
        io.out_events = a->outCap > 0 ? a->outEvents : nullptr; io.out_cap = a->outCap;
        io.tempo = a->tempo; io.playing = a->playing; io.beat_pos = a->beat;
        io.tsig_num = a->tsn; io.tsig_den = a->tsd;
        np_out_select(in->p, a->outSel.load(std::memory_order_relaxed));
        a->rc = np_process_ex(in->p, &io);
        a->outCount = io.out_count;
        ipc::post(a->resp, seen, in->respW);
    }
}

void rebuildTable(Inst& in)
{
    int n = np_param_count(in.p);
    in.ids.clear();
    in.ids.reserve((size_t)n);
    for (int i = 0; i < n; i++)
    {
        np_param_info pi{};
        np_param_info_at(in.p, i, &pi);
        in.ids.push_back(pi.id);
    }
    in.a->nvalues.store((uint32_t)std::min<size_t>(in.ids.size(), MaxTable));
    in.a->tableGen.store(++in.gen, std::memory_order_release);
}

void publish(Inst& in)
{
    size_t n = std::min<size_t>(in.ids.size(), MaxTable);
    for (size_t i = 0; i < n; i++) in.a->values[i] = np_param_get(in.p, in.ids[i]);
}

void onResize(void* user, int w, int h)
{
    auto* in = (Inst*)user;
    in->a->guiW.store(w);
    in->a->guiH.store(h);
    in->a->guiFlags.fetch_or(GuiResize);
    if (in->window) np_platform_window_size(in->window, w, h);   // a floating window of ours follows its plug-in
}

void onClosed(void* user) { ((Inst*)user)->a->guiFlags.fetch_or(GuiClosed); }

void closeInst(Inst& in)
{
    if (in.window)
    {
        np_gui_detach(in.p);
        np_gui_set_callbacks(in.p, nullptr, nullptr, nullptr);
        np_platform_release(in.window);
        in.window = nullptr;
    }
    in.stop.store(1);
    in.a->quit.store(1);
    ipc::wakeOnly(in.a->req, in.reqW);
    if (in.audio.joinable()) in.audio.join();
    if (in.p)
    {
        np_deactivate(in.p);
        np_close(in.p);
        in.p = nullptr;
    }
#if defined(_WIN32)
    ipc::closeEvent(in.reqW);
    ipc::closeEvent(in.respW);
#endif
    in.shm.close();
}

const char* guiApi()
{
#if defined(_WIN32)
    return "hwnd";
#elif defined(__APPLE__)
    return "nsview";
#else
    return "x11";
#endif
}

void handle(uint32_t op, int instId, Rd& r, Wr& w, int32_t& rc, bool& quit)
{
    rc = 0;
    if (op == OpOpen)
    {
        std::string path = r.str(), id = r.str(), name = r.str();
        double period = r.f64();
        auto in = std::make_unique<Inst>();
        if (period > 0) in->period = period;
        if (!r.ok || !in->shm.open(name, sizeof(Audio))) { rc = -1; w.str("could not open the audio lane"); return; }
        in->a = (Audio*)in->shm.ptr;
#if defined(_WIN32)
        if (!ipc::makeEvent(in->reqW, name + "-rq", false) || !ipc::makeEvent(in->respW, name + "-rs", false)) { rc = -1; w.str("could not open the audio lane's events"); in->shm.close(); return; }
#elif defined(__APPLE__)
        if (pendingSems.empty()) { rc = -1; w.str("no semaphores for the audio lane"); in->shm.close(); return; }
        in->reqW.sem = pendingSems.front().first;
        in->respW.sem = pendingSems.front().second;
        pendingSems.pop_front();
#endif
        char err[1024] = {0};
        in->p = np_open(path.c_str(), id.c_str(), err, sizeof err);
        if (!in->p)
        {
            rc = -1;
            w.str(err);
            in->shm.close();
            return;
        }
        np_class_info ci{};
        np_info(in->p, &ci);
        w.raw(&ci, sizeof ci);
        rebuildTable(*in);
        publish(*in);
        in->id = nextId++;
        Inst* raw = in.get();
        raw->audio = std::thread(audioLoop, raw);
        rc = in->id;
        insts[in->id] = std::move(in);
        return;
    }
    if (op == OpQuit) { quit = true; return; }

    auto it = insts.find(instId);
    if (it == insts.end()) { rc = -1; return; }
    Inst& in = *it->second;
    np_plugin* p = in.p;
    char buf[1024];
    switch (op)
    {
    case OpClose:
        closeInst(in);
        insts.erase(it);
        break;
    case OpActivate:
    {
        double rate = r.f64();
        int maxFrames = r.i32(), offline = r.i32();
        char err[512] = {0};
        rc = np_activate(p, rate, maxFrames, offline, err, sizeof err);
        if (rc != 0) w.str(err);
        rebuildTable(in);
        publish(in);
        break;
    }
    case OpDeactivate: np_deactivate(p); break;
    case OpChannels: rc = np_channels(p, r.i32()); break;
    case OpParamCount: rc = np_param_count(p); break;
    case OpParamInfo:
    {
        int start = r.i32(), count = r.i32(), got = 0;
        for (int i = 0; i < count && w.n + sizeof(np_param_info) <= w.cap; i++)
        {
            np_param_info pi{};
            if (np_param_info_at(p, start + i, &pi) != 0) break;
            w.raw(&pi, sizeof pi);
            got++;
        }
        rc = got;
        break;
    }
    case OpParamGet: w.f64(np_param_get(p, r.u32())); break;
    case OpParamText:
    {
        uint32_t id = r.u32();
        double v = r.f64();
        int n = np_param_text(p, id, v, buf, sizeof buf);
        rc = n < 0 ? -1 : 0;
        w.str(n < 0 ? "" : buf);
        break;
    }
    case OpParamFromText:
    {
        uint32_t id = r.u32();
        std::string t = r.str();
        double v = 0;
        rc = np_param_from_text(p, id, t.c_str(), &v);
        w.f64(v);
        break;
    }
    case OpParamSet:
    {
        uint32_t id = r.u32();
        rc = np_param_set(p, id, r.f64());
        publish(in);
        break;
    }
    case OpParamPlain:
    {
        uint32_t id = r.u32();
        w.f64(np_param_plain(p, id, r.f64()));
        break;
    }
    case OpAuxChannels: rc = np_aux_channels(p); break;
    case OpReset: np_reset(p); publish(in); break;
    case OpFlush: np_flush(p); publish(in); break;   // the host holds its block gate: no block of this instance is running
    case OpStateSave:
    {
        int n = np_state_save(p, nullptr, 0);
        if (n < 0) { rc = -1; break; }
        in.staging.assign((size_t)n, 0);
        int got = np_state_save(p, in.staging.data(), n);
        rc = got < 0 ? -1 : got;
        break;
    }
    case OpStateGet:
    {
        uint32_t off = r.u32(), len = r.u32();
        if ((size_t)off + len > in.staging.size()) { rc = -1; break; }
        w.raw(in.staging.data() + off, len);
        break;
    }
    case OpStatePut:
    {
        uint32_t off = r.u32();
        size_t n = 0;
        const uint8_t* d = r.bytes(n);
        if (!r.ok) { rc = -1; break; }
        if (in.staging.size() < off + n) in.staging.resize(off + n);
        std::memcpy(in.staging.data() + off, d, n);
        break;
    }
    case OpStateLoad:
    {
        uint32_t size = r.u32();
        char err[512] = {0};
        rc = size <= in.staging.size() ? np_state_load(p, in.staging.data(), (int)size, err, sizeof err) : -1;
        if (rc != 0) w.str(err[0] ? err : "the state did not arrive whole");
        in.staging.clear();
        in.staging.shrink_to_fit();
        rebuildTable(in);
        publish(in);
        break;
    }
    case OpLatency: rc = np_latency(p); break;
    case OpTail: rc = np_tail(p); break;
    case OpGuiSupport: { std::string api = r.str(); rc = np_gui_support(p, api.c_str()); break; }
    case OpGuiAttach:
    {
        void* parent = (void*)(uintptr_t)r.u64();
        std::string api = r.str();
        int gw = 0, gh = 0;
        char err[512] = {0};
        np_gui_set_callbacks(p, onResize, onClosed, &in);
        rc = np_gui_attach(p, parent, api.c_str(), &gw, &gh, err, sizeof err);
        w.i32(gw); w.i32(gh); w.str(err);
        break;
    }
    case OpGuiDetach: np_gui_detach(p); np_gui_set_callbacks(p, nullptr, nullptr, nullptr); break;
    case OpGuiSize: { int gw = 0, gh = 0; rc = np_gui_size(p, &gw, &gh); w.i32(gw); w.i32(gh); break; }
    case OpGuiCanResize: rc = np_gui_can_resize(p); break;
    case OpGuiSetSize:
    {
        int gw = r.i32(), gh = r.i32();
        rc = np_gui_set_size(p, &gw, &gh);
        if (rc == 0 && in.window) np_platform_window_size(in.window, gw, gh);
        w.i32(gw); w.i32(gh);
        break;
    }
    case OpGuiSetScale: np_gui_set_scale(p, r.f64()); break;
    case OpGuiResizeDone: break;   // the library already told the plug-in; the host sized its container
    case OpGuiOpenWindow:
    {
        std::string title = r.str();
        int gw = 0, gh = 0;
        char err[512] = {0};
        void* content = in.window ? in.window : np_platform_window(title.c_str(), 640, 480);
        if (!content) { rc = -1; w.i32(0); w.i32(0); w.str("the window could not be made"); break; }
        if (!in.window)
        {
            np_gui_set_callbacks(p, onResize, onClosed, &in);
            rc = np_gui_attach(p, content, guiApi(), &gw, &gh, err, sizeof err);
            if (rc != 0) { np_platform_release(content); np_gui_set_callbacks(p, nullptr, nullptr, nullptr); w.i32(0); w.i32(0); w.str(err); break; }
            np_platform_window_size(content, gw, gh);
            np_platform_on_close(content, onClosed, &in);
            in.window = content;
        }
        else np_gui_size(p, &gw, &gh);
        w.i32(gw); w.i32(gh); w.str("");
        break;
    }
    case OpGuiCloseWindow:
        if (in.window)
        {
            np_gui_detach(p);
            np_gui_set_callbacks(p, nullptr, nullptr, nullptr);
            np_platform_release(in.window);
            in.window = nullptr;
        }
        break;
    case OpOutCount: rc = np_out_count(p); break;
    case OpOutInfo:
    {
        int ch = 0;
        rc = np_out_info(p, r.i32(), buf, sizeof buf, &ch);
        w.i32(ch); w.str(buf);
        break;
    }
    default: rc = -1; break;
    }
}

void tick()
{
    for (auto& kv : insts)
    {
        Inst& in = *kv.second;
        np_idle(in.p);
        int flags = np_poll(in.p);
        if (flags & NP_POLL_PARAMS) rebuildTable(in);
        if (flags) in.a->pollFlags.fetch_or((uint32_t)flags);
        publish(in);
    }
}

bool anyWindow()
{
    for (auto& kv : insts) if (kv.second->window) return true;
    return false;
}

void pumpPlatform()
{
#if defined(_WIN32)
    MSG m;
    while (PeekMessageW(&m, nullptr, 0, 0, PM_REMOVE))
    {
        TranslateMessage(&m);
        DispatchMessageW(&m);
    }
#elif defined(__APPLE__)
    np_platform_run(0.001);
#endif
}

} // namespace

namespace np { namespace proto {

int serve(int argc, char** argv)
{
    if (argc < 3) { std::fprintf(stderr, "neoplug-server: not meant to be started by hand (it is started by the host)\n"); return 2; }
    std::string ctrlName = argv[1];
    long parentPid = std::atol(argv[2]);
#if defined(_WIN32)
    SetErrorMode(SEM_NOGPFAULTERRORBOX | SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX);   // a crashing plug-in must end the server, not show a dialog
    HANDLE parent = OpenProcess(SYNCHRONIZE, FALSE, (DWORD)parentPid);
#endif
#if defined(__linux__)
    prctl(PR_SET_PDEATHSIG, SIGKILL);
#endif
    ipc::Shm cs;
    if (!cs.open(ctrlName, sizeof(Ctrl))) { std::fprintf(stderr, "neoplug-server: no control lane %s\n", ctrlName.c_str()); return 3; }
    ctrl = (Ctrl*)cs.ptr;
#if defined(_WIN32)
    if (!ipc::makeEvent(ctrlReq, ctrlName + "-rq", false) || !ipc::makeEvent(ctrlResp, ctrlName + "-rs", false)) { ctrl->ready.store(2); return 3; }
#elif defined(__APPLE__)
    std::string err;
    if (!mach.hello(err)) { std::fprintf(stderr, "neoplug-server: %s\n", err.c_str()); ctrl->ready.store(2); return 4; }
    ctrlResp.sem = mach.ctrlRespSem;
#endif
    ctrl->serverPid.store((uint32_t)
#if defined(_WIN32)
        GetCurrentProcessId()
#else
        getpid()
#endif
    );
    ctrl->ready.store(1);

    uint32_t seen = 0;
    bool quit = false;
    auto lastTick = std::chrono::steady_clock::now();
    while (!quit)
    {
        int got = 1;   // 0 a request, 1 nothing
#if defined(__APPLE__)
        uint32_t id = 0, ports[4];
        int nports = 0;
        int r = mach.waitDoorbell(anyWindow() ? 5 : 10, id, ports, nports);
        if (r == 0)
        {
            if (id == MachSemaphores && nports >= 2) { pendingSems.emplace_back(ports[0], ports[1]); continue; }
            if (id == MachWorkgroup && nports >= 1) { workgroupPort.store(ports[0]); continue; }
            if (id == MachRing) got = 0;
        }
        else if (r < 0) break;
#else
        got = ipc::wait(ctrl->req, seen + 1, ctrlReq, 0, 10, nullptr) == ipc::WaitOk ? 0 : 1;
#endif
        if (got == 0)
        {
            seen++;
            Rd r2(ctrl->data, ctrl->len);
            Wr w(ctrl->rdata, CtrlData);
            int32_t rc = 0;
            handle(ctrl->op, ctrl->inst, r2, w, rc, quit);
            ctrl->rc = rc;
            ctrl->rlen = (uint32_t)w.n;
            ipc::post(ctrl->resp, seen, ctrlResp);
        }
        auto now = std::chrono::steady_clock::now();
        if (now - lastTick > std::chrono::milliseconds(8) || got != 0)
        {
            lastTick = now;
            tick();
            pumpPlatform();
#if defined(_WIN32)
            if (parent && WaitForSingleObject(parent, 0) == WAIT_OBJECT_0) break;
#else
            if (getppid() != (pid_t)parentPid) break;
#endif
        }
    }
    for (auto& kv : insts) closeInst(*kv.second);
    insts.clear();
    return 0;
}

}} // namespace np::proto
