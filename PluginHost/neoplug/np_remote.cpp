// neoplug sandbox, the host's side (docs/specs/plugin-host.md H2): a Plugin that lives in a neoplug-server process.
// The same operations the in-process backends have, as messages: every call but the audio block goes over the server's control lane
// (answered on its main thread, which is the plug-in's main thread there), the audio block over the instance's own lane.
#include "np_internal.h"
#include "np_proto.h"

#include <algorithm>
#include <chrono>
#include <map>
#include <memory>
#include <mutex>

#if !defined(_WIN32)
#include <unistd.h>
#endif
#if defined(__APPLE__)
#include <mach/mach.h>
#include <os/workgroup.h>
namespace np { namespace ipc { extern uint32_t g_np_spawn_bootstrap; } }
#endif

using namespace np;
using namespace np::proto;

namespace {

int envInt(const char* name, int def)
{
    const char* v = std::getenv(name);
    return v && *v ? std::atoi(v) : def;
}

int ctrlTimeoutMs() { static int v = envInt("NEOPLUG_CTRL_TIMEOUT_MS", 60000); return v; }
int openTimeoutMs() { static int v = envInt("NEOPLUG_OPEN_TIMEOUT_MS", 180000); return v; }
int processTimeoutMs() { static int v = envInt("NEOPLUG_PROCESS_TIMEOUT_MS", 500); return v; }
int64_t spinNs()
{
#if defined(__APPLE__)
    return 0;
#else
    static int64_t v = envInt("NEOPLUG_SPIN_NS", 200000);
    return v;
#endif
}

std::string describeEnd(const ipc::Child& c)
{
    char buf[96];
    if (c.signal) { std::snprintf(buf, sizeof buf, "crashed (signal %d)", c.signal); return buf; }
    if (c.code != 0 && c.code != -1) { std::snprintf(buf, sizeof buf, "crashed (exit code %ld / 0x%lx)", c.code, (unsigned long)c.code); return buf; }
    return c.code == 0 ? "ended" : "ended unexpectedly";
}

} // namespace

namespace np {

class RemotePlugin;

/// One server process: its control lane, the thread that watches it, and the plug-ins in it.
struct Link
{
    std::string key;
    ipc::Shm shm;
    Ctrl* c = nullptr;
    ipc::Wake reqW, respW;
    uint32_t reqCount = 0;
    std::mutex mu;                       // one control call at a time
    ipc::Child child;
    std::thread monitor;
    std::atomic<int> dead{0};
    std::atomic<int> closing{0};
    std::atomic<int> instances{0};
    std::mutex listMu;
    std::vector<RemotePlugin*> plugs;
    std::string how;                     // how the server ended
#if defined(__APPLE__)
    ipc::MachHost mach;
#endif

    ~Link();
    bool start(std::string& err);
    bool startImpl(std::string& err);
    void stop();
    void markDead(const char* why, bool killIt);
    bool call(uint32_t op, int inst, const uint8_t* req, size_t reqLen, int timeoutMs, int32_t& rc, std::vector<uint8_t>* resp, const uint32_t* sems = nullptr);
    void wakeAll();
};

static std::mutex registryMu;
static std::map<std::string, std::shared_ptr<Link>> registry;
static std::atomic<uint32_t> workgroupPort{0};
static std::atomic<int> workgroupPeriodUs{0};

class RemotePlugin final : public Plugin
{
public:
    std::shared_ptr<Link> link;
    int kind = 1;                       // NP placement
    int inst = 0;
    ipc::Shm ashm;
    Audio* a = nullptr;
    ipc::Wake reqW, respW;
    uint32_t seq = 0;
    std::vector<np_param_info> params;
    std::map<uint32_t, int> indexById;
    uint32_t tableGen = 0;
    bool tableOk = false;

    RemotePlugin(std::shared_ptr<Link> l, int placementKind) : link(std::move(l)), kind(placementKind) {}

    ~RemotePlugin() override
    {
        if (inst > 0 && !link->dead.load())
        {
            if (a) a->quit.store(1);
            int32_t rc;
            link->call(OpClose, inst, nullptr, 0, 10000, rc, nullptr);
        }
        {
            std::lock_guard<std::mutex> lk(link->listMu);
            link->plugs.erase(std::remove(link->plugs.begin(), link->plugs.end(), this), link->plugs.end());
        }
#if defined(_WIN32)
        ipc::closeEvent(reqW);
        ipc::closeEvent(respW);
#elif defined(__APPLE__)
        ipc::freeSemaphore(reqW.sem);
        ipc::freeSemaphore(respW.sem);
#endif
        ashm.close();
        if (inst > 0 && link->instances.fetch_sub(1) == 1)
        {
            std::lock_guard<std::mutex> lk(registryMu);
            auto it = registry.find(link->key);
            if (it != registry.end() && it->second == link && link->instances.load() == 0) registry.erase(it);
        }
    }

    bool isDead() const { return link->dead.load() != 0; }

    bool rpc(uint32_t op, const Wr* w, int32_t& rc, std::vector<uint8_t>* resp = nullptr, int timeoutMs = 0)
    {
        return link->call(op, inst, w ? w->p : nullptr, w ? w->n : 0, timeoutMs > 0 ? timeoutMs : ctrlTimeoutMs(), rc, resp);
    }

    // --- opening ------------------------------------------------------------------------------------------

    bool open(const std::string& path, const std::string& id, std::string& err)
    {
        std::string name = ipc::uniqueName("a");
        if (!ashm.create(name, sizeof(Audio))) { err = "could not make shared memory for the sandbox"; return false; }
        a = (Audio*)ashm.ptr;
        uint32_t sems[2] = {0, 0};
#if defined(_WIN32)
        if (!ipc::makeEvent(reqW, name + "-rq", true) || !ipc::makeEvent(respW, name + "-rs", true)) { err = "could not make the sandbox's events"; return false; }
#elif defined(__APPLE__)
        reqW.sem = ipc::newSemaphore();
        respW.sem = ipc::newSemaphore();
        if (!reqW.sem || !respW.sem) { err = "could not make the sandbox's semaphores"; return false; }
        sems[0] = reqW.sem; sems[1] = respW.sem;
#endif
        std::vector<uint8_t> buf(path.size() + id.size() + name.size() + 64);
        Wr w(buf.data(), buf.size());
        w.str(path); w.str(id); w.str(name);
        w.f64(workgroupPeriodUs.load() / 1e6);
        std::vector<uint8_t> resp;
        int32_t rc = -1;
        bool ok = link->call(OpOpen, 0, w.p, w.n, openTimeoutMs(), rc, &resp, sems);
        if (!ok)
        {
            err = link->dead.load() ? "the plug-in crashed while it was loading in its sandbox (" + link->how + ")" : "the sandbox did not answer while the plug-in loaded";
            return false;
        }
        Rd r(resp.data(), resp.size());
        if (rc <= 0) { err = r.str(); if (err.empty()) err = "could not load"; return false; }
        inst = rc;
        r.raw(&info, sizeof info);
        link->instances.fetch_add(1);
        {
            std::lock_guard<std::mutex> lk(link->listMu);
            link->plugs.push_back(this);
        }
        refreshParams();
        return true;
    }

    void refreshParams()
    {
        params.clear();
        indexById.clear();
        int32_t count = 0;
        if (!rpc(OpParamCount, nullptr, count) || count <= 0) { tableOk = false; return; }
        params.reserve((size_t)count);
        const int per = (int)((CtrlData - 64) / sizeof(np_param_info));
        while ((int)params.size() < count)
        {
            uint8_t b[16];
            Wr w(b, sizeof b);
            w.i32((int)params.size()); w.i32(std::min(per, count - (int)params.size()));
            int32_t got = 0;
            std::vector<uint8_t> resp;
            if (!rpc(OpParamInfo, &w, got, &resp) || got <= 0 || resp.size() < (size_t)got * sizeof(np_param_info)) break;
            size_t old = params.size();
            params.resize(old + (size_t)got);
            std::memcpy(&params[old], resp.data(), (size_t)got * sizeof(np_param_info));
        }
        for (size_t i = 0; i < params.size(); i++) indexById[params[i].id] = (int)i;
        tableGen = a->tableGen.load();
        tableOk = params.size() <= (size_t)MaxTable;
    }

    // --- Plugin -----------------------------------------------------------------------------------------------

    int placement() override { return kind; }
    int status() override { return isDead() ? 2 : 1; }
    long serverPid() override { return link->child.id(); }
    std::string statusText() override { return isDead() ? link->how : "running"; }

    bool activate(double rate, int maxFrames, bool offline, std::string& err) override
    {
        uint8_t b[32];
        Wr w(b, sizeof b);
        w.f64(rate); w.i32(maxFrames); w.i32(offline ? 1 : 0);
        int32_t rc;
        std::vector<uint8_t> resp;
        if (!rpc(OpActivate, &w, rc, &resp)) { err = "the sandbox is gone"; return false; }
        if (rc != 0) { Rd r(resp.data(), resp.size()); err = r.str(); return false; }
        return true;
    }
    void deactivate() override { int32_t rc; rpc(OpDeactivate, nullptr, rc); }
    int channels(bool input) override
    {
        uint8_t b[8]; Wr w(b, sizeof b); w.i32(input ? 1 : 0);
        int32_t rc = 0;
        return rpc(OpChannels, &w, rc) ? rc : 0;
    }
    int paramCount() override { return (int)params.size(); }
    bool paramInfo(int index, np_param_info& out) override
    {
        if (index < 0 || index >= (int)params.size()) return false;
        out = params[(size_t)index];
        return true;
    }
    double paramGet(uint32_t id) override
    {
        if (tableOk && a->tableGen.load(std::memory_order_acquire) == tableGen)
        {
            auto it = indexById.find(id);
            if (it != indexById.end() && it->second < MaxTable) return a->values[it->second];
        }
        uint8_t b[8]; Wr w(b, sizeof b); w.u32(id);
        int32_t rc; std::vector<uint8_t> resp;
        if (!rpc(OpParamGet, &w, rc, &resp) || resp.size() < 8) return 0;
        double v; std::memcpy(&v, resp.data(), 8);
        return v;
    }
    bool paramText(uint32_t id, double v, std::string& out) override
    {
        uint8_t b[16]; Wr w(b, sizeof b); w.u32(id); w.f64(v);
        int32_t rc; std::vector<uint8_t> resp;
        if (!rpc(OpParamText, &w, rc, &resp) || rc != 0) return false;
        Rd r(resp.data(), resp.size());
        out = r.str();
        return true;
    }
    bool paramFromText(uint32_t id, const char* text, double& out) override
    {
        std::string t = text ? text : "";
        std::vector<uint8_t> b(t.size() + 16); Wr w(b.data(), b.size()); w.u32(id); w.str(t);
        int32_t rc; std::vector<uint8_t> resp;
        if (!rpc(OpParamFromText, &w, rc, &resp) || rc != 0 || resp.size() < 8) return false;
        std::memcpy(&out, resp.data(), 8);
        return true;
    }
    bool paramSet(uint32_t id, double v) override
    {
        uint8_t b[16]; Wr w(b, sizeof b); w.u32(id); w.f64(v);
        int32_t rc;
        return rpc(OpParamSet, &w, rc) && rc == 0;
    }
    double paramPlain(uint32_t id, double v) override
    {
        uint8_t b[16]; Wr w(b, sizeof b); w.u32(id); w.f64(v);
        int32_t rc; std::vector<uint8_t> resp;
        if (!rpc(OpParamPlain, &w, rc, &resp) || resp.size() < 8) return v;
        double out; std::memcpy(&out, resp.data(), 8);
        return out;
    }
    int auxChannels() override { int32_t rc = 0; return rpc(OpAuxChannels, nullptr, rc) ? rc : 0; }
    int poll() override
    {
        uint32_t f = a->pollFlags.exchange(0);
        if (f & NP_POLL_PARAMS) refreshParams();
        return (int)f;
    }
    void reset() override { int32_t rc; rpc(OpReset, nullptr, rc); }
    int latency() override { int32_t rc = 0; return rpc(OpLatency, nullptr, rc) ? rc : 0; }
    int tail() override { int32_t rc = 0; return rpc(OpTail, nullptr, rc) ? rc : 0; }

    bool saveState(std::vector<uint8_t>& out) override
    {
        int32_t size = 0;
        if (!rpc(OpStateSave, nullptr, size) || size < 0) return false;
        out.resize((size_t)size);
        const size_t chunk = CtrlData - 64;
        for (size_t off = 0; off < (size_t)size; off += chunk)
        {
            uint8_t b[16]; Wr w(b, sizeof b);
            w.u32((uint32_t)off); w.u32((uint32_t)std::min(chunk, (size_t)size - off));
            int32_t rc; std::vector<uint8_t> resp;
            if (!rpc(OpStateGet, &w, rc, &resp) || resp.size() < std::min(chunk, (size_t)size - off)) return false;
            std::memcpy(out.data() + off, resp.data(), std::min(chunk, (size_t)size - off));
        }
        return true;
    }
    bool loadState(const uint8_t* data, size_t size, std::string& err) override
    {
        const size_t chunk = CtrlData - 64;
        std::vector<uint8_t> b(chunk + 16);
        for (size_t off = 0; off < size; off += chunk)
        {
            size_t n = std::min(chunk, size - off);
            Wr w(b.data(), b.size());
            w.u32((uint32_t)off); w.bytes(data + off, n);
            int32_t rc;
            if (!rpc(OpStatePut, &w, rc) || rc != 0) { err = "the sandbox is gone"; return false; }
        }
        uint8_t h[8]; Wr w(h, sizeof h); w.u32((uint32_t)size);
        int32_t rc; std::vector<uint8_t> resp;
        if (!rpc(OpStateLoad, &w, rc, &resp, 120000)) { err = "the sandbox is gone"; return false; }
        if (rc != 0) { Rd r(resp.data(), resp.size()); err = r.str(); return false; }
        return true;
    }

    /// The server collects the plug-in's own callbacks itself (it idles every few ms); this hands the GUI's to the host's thread.
    void idle() override
    {
        uint32_t g = a->guiFlags.exchange(0);
        if (g & GuiResize)
        {
            int w = a->guiW.load(), h = a->guiH.load();
            guiRequestResize(w, h);
            uint8_t b[16]; Wr wr(b, sizeof b); wr.i32(w); wr.i32(h);
            int32_t rc;
            rpc(OpGuiResizeDone, &wr, rc);
        }
        if (g & GuiClosed) guiRequestClose();
    }

    int guiSupport(const char* api) override
    {
        std::string s = api ? api : "";
        std::vector<uint8_t> b(s.size() + 16); Wr w(b.data(), b.size()); w.str(s);
        int32_t rc = 0;
        if (!rpc(OpGuiSupport, &w, rc)) return 0;
        // A view of another process can't be put in our window on macOS. On Windows it can be tried (NEOPLUG_SANDBOX_EMBED=1), but the first
        // CI evidence was that IPlugView::attached() with a parent HWND of another process did not come back (the app's UI thread waits for the
        // server while the child window needs it to answer): the server was ended as hung. So a sandboxed editor floats, in a window the server owns.
        if ((s == "nsview" || s == "hwnd") && !(s == "hwnd" && envInt("NEOPLUG_SANDBOX_EMBED", 0)))
        {
            if (rc & 1) rc |= 2;   // a plug-in that can be embedded floats in a window the server makes and embeds it in itself
            rc &= ~1;
        }
        return rc;
    }
    bool guiAttach(void* parent, const char* api, int& wOut, int& hOut, std::string& err) override
    {
        std::string s = api ? api : "";
        std::vector<uint8_t> b(s.size() + 32); Wr w(b.data(), b.size());
        w.u64((uint64_t)(uintptr_t)parent); w.str(s);
        int32_t rc; std::vector<uint8_t> resp;
        if (!rpc(OpGuiAttach, &w, rc, &resp, 60000)) { err = "the sandbox is gone"; return false; }
        Rd r(resp.data(), resp.size());
        wOut = r.i32(); hOut = r.i32();
        std::string e = r.str();
        if (rc != 0) { err = e.empty() ? "could not attach" : e; return false; }
        return true;
    }
    void guiDetach() override { int32_t rc; rpc(OpGuiDetach, nullptr, rc); }
    bool guiSize(int& w, int& h) override
    {
        int32_t rc; std::vector<uint8_t> resp;
        if (!rpc(OpGuiSize, nullptr, rc, &resp) || rc != 0 || resp.size() < 8) return false;
        Rd r(resp.data(), resp.size()); w = r.i32(); h = r.i32();
        return true;
    }
    bool guiCanResize() override { int32_t rc = 0; return rpc(OpGuiCanResize, nullptr, rc) && rc == 1; }
    bool guiSetSize(int& w, int& h) override
    {
        uint8_t b[16]; Wr wr(b, sizeof b); wr.i32(w); wr.i32(h);
        int32_t rc; std::vector<uint8_t> resp;
        if (!rpc(OpGuiSetSize, &wr, rc, &resp) || rc != 0 || resp.size() < 8) return false;
        Rd r(resp.data(), resp.size()); w = r.i32(); h = r.i32();
        return true;
    }
    void guiSetScale(double scale) override
    {
        guiScale = scale > 0 ? scale : 1.0;
        uint8_t b[16]; Wr w(b, sizeof b); w.f64(guiScale);
        int32_t rc; rpc(OpGuiSetScale, &w, rc);
    }
    bool guiOpenWindow(const char* title, int& wOut, int& hOut, std::string& err) override
    {
        std::string s = title ? title : "";
        std::vector<uint8_t> b(s.size() + 16); Wr w(b.data(), b.size()); w.str(s);
        int32_t rc; std::vector<uint8_t> resp;
        if (!rpc(OpGuiOpenWindow, &w, rc, &resp, 60000)) { err = "the sandbox is gone"; return false; }
        Rd r(resp.data(), resp.size());
        wOut = r.i32(); hOut = r.i32();
        std::string e = r.str();
        if (rc != 0) { err = e.empty() ? "could not open the window" : e; return false; }
        return true;
    }
    void guiCloseWindow() override { int32_t rc; rpc(OpGuiCloseWindow, nullptr, rc); }

    int outCount() override { int32_t rc = 1; return rpc(OpOutCount, nullptr, rc) ? rc : 1; }
    bool outInfo(int index, std::string& name, int& ch) override
    {
        uint8_t b[8]; Wr w(b, sizeof b); w.i32(index);
        int32_t rc; std::vector<uint8_t> resp;
        if (!rpc(OpOutInfo, &w, rc, &resp) || rc != 0) return false;
        Rd r(resp.data(), resp.size()); ch = r.i32(); name = r.str();
        return true;
    }

    // --- The audio block ---------------------------------------------------------------------------------------

    static void silence(np_process_io& io)
    {
        for (int c = 0; c < io.out_ch; c++)
            if (io.out && io.out[c]) std::memset(io.out[c], 0, sizeof(float) * (size_t)io.frames);
        io.out_count = 0;
    }

    int process(np_process_io& io) override
    {
        io.out_count = 0;
        if (isDead() || !a) { silence(io); return -2; }
        int result = 0;
        int nevLeft = io.nevents;
        const np_event* ev = io.events;
        for (int done = 0; done < io.frames; done += MaxFrames)
        {
            int n = std::min(MaxFrames, io.frames - done);
            Audio* x = a;
            x->frames = (uint32_t)n;
            x->inCh = io.in && io.in_ch > 0 ? std::min(io.in_ch, 2) : 0;
            x->auxCh = io.aux && io.aux_ch > 0 ? std::min(io.aux_ch, 2) : 0;
            x->outCh = std::min(io.out_ch, 2);
            for (int c = 0; c < x->inCh; c++) std::memcpy(x->in[c], io.in[c] + done, sizeof(float) * (size_t)n);
            for (int c = 0; c < x->auxCh; c++) std::memcpy(x->aux[c], io.aux[c] + done, sizeof(float) * (size_t)n);
            int ne = 0;
            while (nevLeft > 0 && ev->frame < (uint32_t)(done + n))
            {
                if (ne < MaxEvents) { x->events[ne] = *ev; x->events[ne].frame = ev->frame >= (uint32_t)done ? ev->frame - (uint32_t)done : 0; ne++; }
                ev++; nevLeft--;
            }
            x->nev = ne;
            x->outCap = io.out_events ? std::min(io.out_cap - io.out_count, MaxOutEvents) : 0;
            x->tempo = io.tempo; x->playing = io.playing;
            x->beat = io.beat_pos;
            x->tsn = io.tsig_num; x->tsd = io.tsig_den;
            x->outSel.store(outSel.load(std::memory_order_relaxed), std::memory_order_relaxed);
            ipc::post(x->req, ++seq, reqW);
            int w = ipc::wait(x->resp, seq, respW, spinNs(), processTimeoutMs(), &link->dead);
            if (w != ipc::WaitOk)
            {
                if (w == ipc::WaitTimeout) link->markDead("hung: it did not answer an audio block", true);
                silence(io);
                return -2;
            }
            for (int c = 0; c < io.out_ch; c++)
                if (c < x->outCh) std::memcpy(io.out[c] + done, x->out[c], sizeof(float) * (size_t)n);
                else std::memset(io.out[c] + done, 0, sizeof(float) * (size_t)n);
            for (int k = 0; k < x->outCount && io.out_events && io.out_count < io.out_cap; k++)
            {
                io.out_events[io.out_count] = x->outEvents[k];
                io.out_events[io.out_count].frame += (uint32_t)done;
                io.out_count++;
            }
            if (x->rc < 0) result = x->rc;
            else if (result >= 0 && x->rc > result) result = x->rc;
        }
        return result;
    }
};

// --- The link ---------------------------------------------------------------------------------------------------

Link::~Link()
{
    stop();
    if (monitor.joinable()) monitor.join();
    shm.close();
#if defined(_WIN32)
    ipc::closeEvent(reqW);
    ipc::closeEvent(respW);
#elif defined(__APPLE__)
    ipc::freeSemaphore(respW.sem);
#endif
    ipc::closeChild(child);
}

void Link::wakeAll()
{
    if (c) ipc::wakeOnly(c->resp, respW);
    std::lock_guard<std::mutex> lk(listMu);
    for (auto* p : plugs)
        if (p->a) ipc::wakeOnly(p->a->resp, p->respW);
}

void Link::markDead(const char* why, bool killIt)
{
    if (dead.exchange(1) == 0)
    {
        if (how.empty()) how = why;
    }
    if (killIt) ipc::kill(child);
    wakeAll();
}

bool Link::call(uint32_t op, int inst, const uint8_t* req, size_t reqLen, int timeoutMs, int32_t& rc, std::vector<uint8_t>* resp, const uint32_t* sems)
{
    std::lock_guard<std::mutex> lk(mu);
    if (dead.load() || !c || reqLen > CtrlData) return false;
    if (reqLen) std::memcpy(c->data, req, reqLen);
    c->op = op; c->inst = inst; c->len = (uint32_t)reqLen;
    uint32_t n = ++reqCount;
#if defined(__APPLE__)
    if (sems && !mach.ring(MachSemaphores, sems, 2)) { markDead("the sandbox did not take its semaphores", true); return false; }
    c->req.seq.store(n, std::memory_order_seq_cst);
    if (!mach.ring(MachRing, nullptr, 0)) { markDead("the sandbox does not answer (its port is gone)", true); return false; }
#else
    (void)sems;
    ipc::post(c->req, n, reqW);
#endif
    int w = ipc::wait(c->resp, n, respW, 0, timeoutMs, &dead);
    if (w == ipc::WaitTimeout) { markDead("hung: it did not answer a request", true); return false; }
    if (w != ipc::WaitOk) return false;
    rc = c->rc;
    if (resp) resp->assign(c->rdata, c->rdata + std::min<size_t>(c->rlen, CtrlData));
    return true;
}

bool Link::start(std::string& err)
{
    bool ok = startImpl(err);
    if (!ok) dead.store(1);
    return ok;
}

bool Link::startImpl(std::string& err)
{
    std::string exe = ipc::serverPath();
    if (exe.empty()) { err = "the sandbox server (neoplug-server) is not installed beside the plug-in library"; return false; }
    std::string name = ipc::uniqueName("c");
    if (!shm.create(name, sizeof(Ctrl))) { err = "could not make shared memory for the sandbox"; return false; }
    c = (Ctrl*)shm.ptr;
#if defined(_WIN32)
    if (!ipc::makeEvent(reqW, name + "-rq", true) || !ipc::makeEvent(respW, name + "-rs", true)) { err = "could not make the sandbox's events"; return false; }
    unsigned long me = GetCurrentProcessId();
#else
    unsigned long me = (unsigned long)getpid();
#endif
#if defined(__APPLE__)
    respW.sem = ipc::newSemaphore();
    mach_port_t self = mach_task_self(), rendezvous = MACH_PORT_NULL;
    mach_port_allocate(self, MACH_PORT_RIGHT_RECEIVE, &rendezvous);
    mach_port_insert_right(self, rendezvous, rendezvous, MACH_MSG_TYPE_MAKE_SEND);
    mach.rendezvous = rendezvous;
    static std::mutex spawnMu;
    bool spawned;
    {
        std::lock_guard<std::mutex> lk(spawnMu);
        ipc::g_np_spawn_bootstrap = rendezvous;
        spawned = ipc::spawn(exe, {name, std::to_string(me)}, child, err);
        ipc::g_np_spawn_bootstrap = 0;
    }
    if (!spawned) { mach_port_mod_refs(self, rendezvous, MACH_PORT_RIGHT_RECEIVE, -1); return false; }
    bool shook = mach.handshake(child, respW.sem, err);
    mach_port_mod_refs(self, rendezvous, MACH_PORT_RIGHT_RECEIVE, -1);
    mach_port_deallocate(self, rendezvous);
    if (!shook) { ipc::kill(child); ipc::waitExit(child); return false; }
#else
    if (!ipc::spawn(exe, {name, std::to_string(me)}, child, err)) return false;
#endif
    auto t0 = std::chrono::steady_clock::now();
    while (c->ready.load() == 0)
    {
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
        if (std::chrono::steady_clock::now() - t0 > std::chrono::seconds(20)) { err = "the sandbox did not start in time"; ipc::kill(child); ipc::waitExit(child); return false; }
        if (!ipc::isAlive(child)) { err = "the sandbox ended while it started"; return false; }
    }
    if (c->ready.load() != 1) { err = "the sandbox could not start"; ipc::kill(child); ipc::waitExit(child); return false; }
#if defined(__APPLE__)
    if (uint32_t wg = workgroupPort.load()) mach.ring(MachWorkgroup, &wg, 1);
#endif
    monitor = std::thread([this] {
        ipc::waitExit(child);
        if (!closing.load()) { how = describeEnd(child); }
        dead.store(1);
        wakeAll();
    });
    return true;
}

void Link::stop()
{
    if (!c) return;
    closing.store(1);
    if (!dead.load())
    {
        c->quit.store(1);
        int32_t rc;
        call(OpQuit, 0, nullptr, 0, 2000, rc, nullptr);
        for (int i = 0; i < 300 && !dead.load(); i++) std::this_thread::sleep_for(std::chrono::milliseconds(10));
        if (!dead.load()) ipc::kill(child);
    }
}

namespace {
std::atomic<uint32_t> instanceCounter{0};
}

Plugin* remoteOpen(int placement, const std::string& path, const std::string& id, std::string& err)
{
    std::shared_ptr<Link> link;
    {
        std::lock_guard<std::mutex> lk(registryMu);
        std::string key = placement == 3 ? "shared" : placement == 2 ? "i" + std::to_string(instanceCounter.fetch_add(1)) : "p|" + path + "|" + id;
        auto it = registry.find(key);
        if (it != registry.end() && !it->second->dead.load()) link = it->second;
        else
        {
            if (it != registry.end()) registry.erase(it);
            link = std::make_shared<Link>();
            link->key = key;
            if (!link->start(err)) return nullptr;
            registry[key] = link;
        }
    }
    auto* rp = new RemotePlugin(link, placement);
    if (!rp->open(path, id, err))
    {
        delete rp;
        return nullptr;
    }
    return rp;
}

void remoteSetWorkgroup(void* workgroup, double periodSec)
{
#if defined(__APPLE__)
    mach_port_t port = MACH_PORT_NULL;
    if (workgroup && os_workgroup_copy_port((os_workgroup_t)workgroup, &port) != 0) port = MACH_PORT_NULL;
    workgroupPort.store(port);
    workgroupPeriodUs.store((int)(periodSec * 1e6));
    if (!port) return;
    std::lock_guard<std::mutex> lk(registryMu);
    for (auto& kv : registry)
        if (!kv.second->dead.load()) { std::lock_guard<std::mutex> l2(kv.second->mu); uint32_t p = port; kv.second->mach.ring(MachWorkgroup, &p, 1); }
#else
    (void)workgroup; (void)periodSec;
#endif
}

} // namespace np
