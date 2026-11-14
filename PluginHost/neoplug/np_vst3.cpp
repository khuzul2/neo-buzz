// neoplug: VST3 hosting on the SDK's hosting helpers (MIT since 3.8).
#include "np_internal.h"

#include "public.sdk/source/common/memorystream.h"
#include "public.sdk/source/vst/hosting/eventlist.h"
#include "public.sdk/source/vst/hosting/hostclasses.h"
#include "public.sdk/source/vst/hosting/module.h"
#include "public.sdk/source/vst/hosting/parameterchanges.h"
#include "public.sdk/source/vst/hosting/plugprovider.h"
#include "public.sdk/source/vst/hosting/processdata.h"
#include "public.sdk/source/vst/utility/stringconvert.h"
#include "pluginterfaces/base/funknownimpl.h"
#include "pluginterfaces/gui/iplugview.h"
#include "pluginterfaces/vst/ivstaudioprocessor.h"
#include "pluginterfaces/vst/ivsteditcontroller.h"
#include "pluginterfaces/vst/ivstevents.h"
#include "pluginterfaces/vst/ivstprocesscontext.h"
#include "pluginterfaces/vst/vstspeaker.h"
#include "pluginterfaces/vst/ivstmidicontrollers.h"
#include "pluginterfaces/vst/ivstnoteexpression.h"

#include <algorithm>
#include <mutex>

using namespace Steinberg;
using namespace Steinberg::Vst;

namespace np {

static HostApplication* hostContext()
{
    static std::once_flag once;
    static HostApplication* host = nullptr;
    std::call_once(once, [] {
        host = new HostApplication();
        PluginContextFactory::instance().setPluginContext(host);
    });
    return host;
}

static std::string utf8(const TChar* s) { return StringConvert::convert(s); }

static bool isAudioClass(const VST3::Hosting::ClassInfo& ci) { return ci.category() == kVstAudioEffectClass; }

static void fillInfo(const VST3::Hosting::ClassInfo& ci, const std::string& factoryVendor, np_class_info& out)
{
    out = np_class_info{};
    out.format = NP_FORMAT_VST3;
    copyStr(out.id, sizeof out.id, ci.ID().toString());
    copyStr(out.name, sizeof out.name, ci.name());
    copyStr(out.vendor, sizeof out.vendor, ci.vendor().empty() ? factoryVendor : ci.vendor());
    copyStr(out.version, sizeof out.version, ci.version());
    copyStr(out.category, sizeof out.category, ci.subCategoriesString());
    out.is_instrument = ci.subCategoriesString().find("Instrument") != std::string::npos ? 1 : 0;
}

int vst3List(const std::string& path, std::vector<np_class_info>& out, std::string& err)
{
    hostContext();
    auto module = VST3::Hosting::Module::create(path, err);
    if (!module) return -1;
    auto factory = module->getFactory();
    std::string vendor = factory.info().vendor();
    for (auto& ci : factory.classInfos())
    {
        if (!isAudioClass(ci)) continue;
        np_class_info info;
        fillInfo(ci, vendor, info);
        out.push_back(info);
    }
    return (int)out.size();
}

class Vst3Plugin;

class Handler : public U::Implements<U::Directly<IComponentHandler>>
{
public:
    explicit Handler(Vst3Plugin* o) : owner(o) {}
    tresult PLUGIN_API beginEdit(ParamID) override { return kResultOk; }
    tresult PLUGIN_API performEdit(ParamID id, ParamValue v) override;
    tresult PLUGIN_API endEdit(ParamID) override { return kResultOk; }
    tresult PLUGIN_API restartComponent(int32 flags) override;
    Vst3Plugin* owner;
};

class Frame : public U::Implements<U::Directly<IPlugFrame>>
{
public:
    tresult PLUGIN_API resizeView(IPlugView* view, ViewRect* r) override
    {
        if (!view || !r) return kInvalidArgument;
        return view->onSize(r);
    }
};

class Vst3Plugin : public Plugin
{
public:
    VST3::Hosting::Module::Ptr module;
    IPtr<PlugProvider> provider;
    IPtr<IComponent> component;
    IPtr<IEditController> controller;
    IPtr<IAudioProcessor> processor;
    IPtr<Handler> handler;
    IPtr<Frame> frame;
    IPtr<IPlugView> view;
    HostProcessData data;
    EventList events{1024}, eventsOut{256};
    ParameterChanges paramsIn{64}, paramsOut{64};
    IPtr<IMidiMapping> midiMapping;
    int32 midiMap[16][130];            // [channel][controller] -> parameter id, -1 = none
    NoteTracker notes;
    int mainIn = -1, auxIn = -1, mainOut = -1;   // bus indices
    int auxChannelsActive = 0;
    ProcessContext context{};
    ParamRing toProcessor, toController;
    std::atomic<int32> restartFlags{0};
    bool active = false, processing = false;
    int latencySamples = 0;

    ~Vst3Plugin() override
    {
        guiDetach();
        deactivate();
        if (controller) controller->setComponentHandler(nullptr);
        midiMapping = nullptr;   // every plugin interface goes before the module does
        view = nullptr;
        processor = nullptr;
        controller = nullptr;
        component = nullptr;
        provider = nullptr;   // terminates and disconnects the component and controller
        module = nullptr;
    }

    bool open(const std::string& path, const std::string& id, std::string& err)
    {
        hostContext();
        module = VST3::Hosting::Module::create(path, err);
        if (!module) return false;
        auto factory = module->getFactory();
        std::string vendor = factory.info().vendor();
        for (auto& ci : factory.classInfos())
        {
            if (!isAudioClass(ci)) continue;
            if (!id.empty() && ci.ID().toString() != id && ci.name() != id) continue;
            fillInfo(ci, vendor, info);
            provider = owned(new PlugProvider(factory, ci, true));
            if (!provider->initialize())
            {
                err = "the plugin failed to initialize (component or controller)";
                return false;
            }
            component = provider->getComponentPtr();
            controller = provider->getControllerPtr();
            processor = U::cast<IAudioProcessor>(component);
            if (!component || !processor)
            {
                err = "no audio processor";
                return false;
            }
            if (!controller)
            {
                err = "no edit controller";
                return false;
            }
            handler = owned(new Handler(this));
            controller->setComponentHandler(handler);
            // The controller starts from the component's state, as hosts do.
            MemoryStream s;
            if (component->getState(&s) == kResultOk)
            {
                s.seek(0, IBStream::kIBSeekSet, nullptr);
                controller->setComponentState(&s);
            }
            int n = controller->getParameterCount();
            paramsIn.setMaxParameters(std::max(64, std::min(n, 4096)));
            paramsOut.setMaxParameters(std::max(64, std::min(n, 4096)));
            // An instrument by its category; or a plugin with notes in and no audio in. (Instruments
            // may have audio inputs too: Surge XT has one.)
            if (!info.is_instrument && component->getBusCount(kAudio, kInput) == 0 && component->getBusCount(kEvent, kInput) > 0)
                info.is_instrument = 1;
            return true;
        }
        err = id.empty() ? "no audio class in the module" : "class not found: " + id;
        return false;
    }

    bool activate(double rate, int maxFrames, bool offline, std::string& err) override
    {
        deactivate();
        int nIn = component->getBusCount(kAudio, kInput);
        int nOut = component->getBusCount(kAudio, kOutput);
        std::vector<SpeakerArrangement> ins(nIn), outs(nOut);
        for (int i = 0; i < nIn; i++) processor->getBusArrangement(kInput, i, ins[i]);
        for (int i = 0; i < nOut; i++) processor->getBusArrangement(kOutput, i, outs[i]);
        // The main input and output, and the first auxiliary input (the sidechain), are used, in stereo.
        mainIn = auxIn = mainOut = -1;
        for (int i = 0; i < nIn; i++)
        {
            BusInfo bi{};
            if (component->getBusInfo(kAudio, kInput, i, bi) != kResultOk) continue;
            if (bi.busType == kMain && mainIn < 0) mainIn = i;
            else if (bi.busType == kAux && auxIn < 0) auxIn = i;
        }
        if (nOut > 0) mainOut = 0;
        if (mainIn >= 0) ins[mainIn] = SpeakerArr::kStereo;
        if (auxIn >= 0) ins[auxIn] = SpeakerArr::kStereo;
        if (mainOut >= 0) outs[mainOut] = SpeakerArr::kStereo;
        processor->setBusArrangements(nIn ? ins.data() : nullptr, nIn, nOut ? outs.data() : nullptr, nOut);
        if (mainIn >= 0) component->activateBus(kAudio, kInput, mainIn, true);
        if (auxIn >= 0) component->activateBus(kAudio, kInput, auxIn, true);
        if (mainOut >= 0) component->activateBus(kAudio, kOutput, mainOut, true);
        if (component->getBusCount(kEvent, kInput) > 0) component->activateBus(kEvent, kInput, 0, true);
        if (component->getBusCount(kEvent, kOutput) > 0) component->activateBus(kEvent, kOutput, 0, true);

        ProcessSetup setup{offline ? kOffline : kRealtime, kSample32, maxFrames, rate};
        if (processor->setupProcessing(setup) != kResultOk)
        {
            err = "setupProcessing refused";
            return false;
        }
        if (component->setActive(true) != kResultOk)
        {
            err = "setActive refused";
            return false;
        }
        data.prepare(*component, maxFrames, kSample32);
        data.processMode = setup.processMode;
        data.inputEvents = &events;
        data.outputEvents = &eventsOut;
        data.inputParameterChanges = &paramsIn;
        data.outputParameterChanges = &paramsOut;
        data.processContext = &context;
        auxChannelsActive = auxIn >= 0 && auxIn < data.numInputs ? data.inputs[auxIn].numChannels : 0;
        context = ProcessContext{};
        context.sampleRate = rate;
        context.tempo = 120;
        context.timeSigNumerator = 4;
        context.timeSigDenominator = 4;
        // MIDI controllers reach a VST3 plugin as parameter changes, through the controller's mapping.
        midiMapping = U::cast<IMidiMapping>(controller);
        for (int c = 0; c < 16; c++)
            for (int k = 0; k < 130; k++)
            {
                ParamID id = 0;
                midiMap[c][k] = midiMapping && midiMapping->getMidiControllerAssignment(0, (int16)c, (CtrlNumber)k, id) == kResultTrue ? (int32)id : -1;
            }
        notes.clear();
        processor->setProcessing(true);   // some plugins answer kNotImplemented
        processing = true;
        active = true;
        latencySamples = (int)processor->getLatencySamples();
        return true;
    }

    void deactivate() override
    {
        if (!active) return;
        if (processing) processor->setProcessing(false);
        processing = false;
        component->setActive(false);
        data.unprepare();
        active = false;
    }

    int channels(bool input) override
    {
        int bus = input ? mainIn : mainOut;
        if (active)
        {
            if (input) return bus >= 0 && bus < data.numInputs ? data.inputs[bus].numChannels : 0;
            return bus >= 0 && bus < data.numOutputs ? data.outputs[bus].numChannels : 0;
        }
        BusInfo bi{};
        int n = component->getBusCount(kAudio, input ? kInput : kOutput);
        for (int i = 0; i < n; i++)
            if (component->getBusInfo(kAudio, input ? kInput : kOutput, i, bi) == kResultOk && (!input || bi.busType == kMain))
                return bi.channelCount;
        return 0;
    }

    int auxChannels() override
    {
        if (active) return auxChannelsActive;
        int n = component->getBusCount(kAudio, kInput);
        for (int i = 0; i < n; i++)
        {
            BusInfo bi{};
            if (component->getBusInfo(kAudio, kInput, i, bi) == kResultOk && bi.busType == kAux) return bi.channelCount;
        }
        return 0;
    }

    int paramCount() override { return controller->getParameterCount(); }

    bool paramInfo(int index, np_param_info& out) override
    {
        ParameterInfo pi{};
        if (controller->getParameterInfo(index, pi) != kResultOk) return false;
        out.id = pi.id;
        out.steps = pi.stepCount;
        out.min = 0;
        out.max = 1;
        out.def = pi.defaultNormalizedValue;
        out.flags = 0;
        if (pi.flags & ParameterInfo::kCanAutomate) out.flags |= NP_PARAM_AUTOMATABLE;
        if (pi.stepCount > 0) out.flags |= NP_PARAM_STEPPED;
        if (pi.flags & ParameterInfo::kIsReadOnly) out.flags |= NP_PARAM_READONLY;
        if (pi.flags & ParameterInfo::kIsHidden) out.flags |= NP_PARAM_HIDDEN;
        if (pi.flags & ParameterInfo::kIsBypass) out.flags |= NP_PARAM_BYPASS;
        copyStr(out.name, sizeof out.name, utf8(pi.title));
        copyStr(out.units, sizeof out.units, utf8(pi.units));
        copyStr(out.module, sizeof out.module, std::to_string(pi.unitId));
        return true;
    }

    double paramGet(uint32_t id) override { return controller->getParamNormalized(id); }

    bool paramText(uint32_t id, double v, std::string& out) override
    {
        String128 s{};
        if (controller->getParamStringByValue(id, v, s) != kResultOk) return false;
        out = utf8(s);
        return true;
    }

    bool paramFromText(uint32_t id, const char* text, double& out) override
    {
        String128 s{};
        StringConvert::convert(std::string(text), s);
        ParamValue v = 0;
        if (controller->getParamValueByString(id, s, v) != kResultOk) return false;
        out = v;
        return true;
    }

    bool paramSet(uint32_t id, double v) override
    {
        controller->setParamNormalized(id, v);
        return toProcessor.push(id, v);
    }

    void addParam(ParamID id, int32 offset, ParamValue v)
    {
        int32 index = 0;
        if (auto* q = paramsIn.addParameterData(id, index))
        {
            int32 pi = 0;
            q->addPoint(offset, v, pi);
        }
    }

    static NoteExpressionValue exprNormalized(int type, double v)
    {
        switch (type)
        {
        case NP_EXPR_VOLUME: return std::min(1.0, std::max(0.0, v / 4.0));
        case NP_EXPR_TUNING: return std::min(1.0, std::max(0.0, (v + 120.0) / 240.0));
        default: return std::min(1.0, std::max(0.0, v));
        }
    }

    void addCc(int ch, int ctrl, int32 offset, double normalized)
    {
        int32 id = midiMap[ch & 15][ctrl];
        if (id >= 0) addParam((ParamID)id, offset, std::min(1.0, std::max(0.0, normalized)));
    }

    int process(np_process_io& io) override
    {
        const int frames = io.frames;
        float* const* out = io.out;
        const int outCh = io.out_ch;
        if (!active)
        {
            for (int c = 0; c < outCh; c++) std::memset(out[c], 0, sizeof(float) * frames);
            return 0;
        }
        for (int b = 0; b < data.numInputs; b++)
        {
            auto& bus = data.inputs[b];
            if (b == mainIn) foldIn(bus.channelBuffers32, bus.numChannels, io.in, io.in_ch, frames);
            else if (b == auxIn) foldIn(bus.channelBuffers32, bus.numChannels, io.aux, io.aux_ch, frames);
            else
                for (int c = 0; c < bus.numChannels; c++) std::memset(bus.channelBuffers32[c], 0, sizeof(float) * frames);
            bus.silenceFlags = 0;
        }
        events.clear();
        eventsOut.clear();
        paramsIn.clearQueue();
        paramsOut.clearQueue();
        ParamRing::Item it;
        while (toProcessor.pop(it)) addParam(it.id, 0, it.value);
        for (int i = 0; i < io.nevents; i++)
        {
            const np_event& e = io.events[i];
            Event x{};
            x.busIndex = 0;
            x.sampleOffset = (int32)std::min<uint32_t>(e.frame, (uint32_t)(frames - 1));
            switch (e.type)
            {
            case NP_EV_NOTE_ON:
                x.type = Event::kNoteOnEvent;
                x.noteOn.channel = (int16)e.channel;
                x.noteOn.pitch = (int16)e.key;
                x.noteOn.velocity = (float)e.value;
                x.noteOn.noteId = e.note_id;
                events.addEvent(x);
                notes.noteOn(e.channel, e.key, e.note_id);
                break;
            case NP_EV_NOTE_OFF:
                x.type = Event::kNoteOffEvent;
                x.noteOff.channel = (int16)e.channel;
                x.noteOff.pitch = (int16)e.key;
                x.noteOff.velocity = (float)e.value;
                x.noteOff.noteId = e.note_id >= 0 ? e.note_id : notes.idOf(e.channel, e.key);
                events.addEvent(x);
                notes.noteOff(e.channel, e.key);
                break;
            case NP_EV_PARAM:
                addParam(e.param_id, x.sampleOffset, e.value);
                toController.push(e.param_id, e.value);
                break;
            case NP_EV_CC: addCc(e.channel, e.key & 127, x.sampleOffset, e.value); break;
            case NP_EV_PITCH_BEND: addCc(e.channel, kPitchBend, x.sampleOffset, (e.value + 1.0) * 0.5); break;
            case NP_EV_CHANNEL_PRESSURE: addCc(e.channel, kAfterTouch, x.sampleOffset, e.value); break;
            case NP_EV_POLY_PRESSURE:
                x.type = Event::kPolyPressureEvent;
                x.polyPressure.channel = (int16)e.channel;
                x.polyPressure.pitch = (int16)e.key;
                x.polyPressure.pressure = (float)e.value;
                x.polyPressure.noteId = e.note_id >= 0 ? e.note_id : notes.idOf(e.channel, e.key);
                events.addEvent(x);
                break;
            case NP_EV_NOTE_EXPR:
                x.type = Event::kNoteExpressionValueEvent;
                x.noteExpressionValue.typeId = (NoteExpressionTypeID)(e.param_id == NP_EXPR_PRESSURE ? (uint32_t)kExpressionTypeID : std::min<uint32_t>(e.param_id, 5));
                x.noteExpressionValue.noteId = e.note_id >= 0 ? e.note_id : notes.idOf(e.channel, e.key);
                x.noteExpressionValue.value = exprNormalized((int)e.param_id, e.value);
                events.addEvent(x);
                break;
            case NP_EV_ALL_NOTES_OFF:
                notes.endAll([&](int c, int k, int id) {
                    Event o{};
                    o.type = Event::kNoteOffEvent;
                    o.sampleOffset = x.sampleOffset;
                    o.noteOff.channel = (int16)c;
                    o.noteOff.pitch = (int16)k;
                    o.noteOff.velocity = 0;
                    o.noteOff.noteId = id;
                    events.addEvent(o);
                });
                break;
            default: break;
            }
        }
        context.tempo = io.tempo > 0 ? io.tempo : 120;
        context.timeSigNumerator = io.tsig_num > 0 ? io.tsig_num : 4;
        context.timeSigDenominator = io.tsig_den > 0 ? io.tsig_den : 4;
        context.state = ProcessContext::kTempoValid | ProcessContext::kTimeSigValid | (io.playing ? ProcessContext::kPlaying : 0);
        if (io.beat_pos >= 0)
        {
            context.projectTimeMusic = io.beat_pos;
            context.state |= ProcessContext::kProjectTimeMusicValid;
        }
        data.numSamples = frames;
        processor->process(data);
        context.projectTimeSamples += frames;
        context.continousTimeSamples += frames;
        // What the plugin moved by itself (its own automation) goes to its controller, on the main thread.
        for (int32 i = 0, n = paramsOut.getParameterCount(); i < n; i++)
            if (auto* q = paramsOut.getParameterData(i))
            {
                int32 pc = q->getPointCount();
                int32 off = 0;
                ParamValue v = 0;
                if (pc > 0 && q->getPoint(pc - 1, off, v) == kResultOk) toController.push(q->getParameterId(), v);
            }
        // Its note output.
        io.out_count = 0;
        if (io.out_events)
            for (int32 i = 0, n = eventsOut.getEventCount(); i < n && io.out_count < io.out_cap; i++)
            {
                Event e{};
                if (eventsOut.getEvent(i, e) != kResultOk) continue;
                np_event& o = io.out_events[io.out_count];
                o = np_event{};
                o.frame = (uint32_t)std::max<int32>(0, e.sampleOffset);
                if (e.type == Event::kNoteOnEvent)
                {
                    o.type = NP_EV_NOTE_ON; o.channel = e.noteOn.channel; o.key = e.noteOn.pitch; o.note_id = e.noteOn.noteId; o.value = e.noteOn.velocity;
                }
                else if (e.type == Event::kNoteOffEvent)
                {
                    o.type = NP_EV_NOTE_OFF; o.channel = e.noteOff.channel; o.key = e.noteOff.pitch; o.note_id = e.noteOff.noteId; o.value = e.noteOff.velocity;
                }
                else continue;
                io.out_count++;
            }
        bool silent = true;
        if (mainOut >= 0 && mainOut < data.numOutputs)
        {
            auto& bus = data.outputs[mainOut];
            foldOut(out, outCh, (const float* const*)bus.channelBuffers32, bus.numChannels, frames);
            uint64 all = bus.numChannels >= 64 ? ~0ull : ((1ull << bus.numChannels) - 1);
            silent = bus.numChannels == 0 || (bus.silenceFlags & all) == all;
        }
        else
            for (int c = 0; c < outCh; c++) std::memset(out[c], 0, sizeof(float) * frames);
        return silent ? 0 : 1;
    }

    bool saveState(std::vector<uint8_t>& out) override
    {
        MemoryStream cs, ks;
        if (component->getState(&cs) != kResultOk) return false;
        bool single = U::cast<IEditController>(component) != nullptr;
        if (!single) controller->getState(&ks);
        out.clear();
        out.insert(out.end(), {'N', 'P', 'S', 'T'});
        putU32(out, NP_FORMAT_VST3);
        putU32(out, (uint32_t)cs.getSize());
        out.insert(out.end(), cs.getData(), cs.getData() + cs.getSize());
        putU32(out, (uint32_t)ks.getSize());
        out.insert(out.end(), ks.getData(), ks.getData() + ks.getSize());
        return true;
    }

    bool loadState(const uint8_t* d, size_t size, std::string& err) override
    {
        const uint8_t* p = d;
        const uint8_t* end = d + size;
        uint32_t fmt = 0, clen = 0, klen = 0;
        if (size < 8 || std::memcmp(d, "NPST", 4) != 0) { err = "not a neoplug state"; return false; }
        p += 4;
        if (!getU32(p, end, fmt) || fmt != NP_FORMAT_VST3) { err = "not a VST3 state"; return false; }
        if (!getU32(p, end, clen) || (size_t)(end - p) < clen) { err = "truncated"; return false; }
        const uint8_t* comp = p;
        p += clen;
        if (!getU32(p, end, klen) || (size_t)(end - p) < klen) { err = "truncated"; return false; }
        const uint8_t* ctrl = p;
        MemoryStream cs((void*)comp, (TSize)clen);
        if (component->setState(&cs) != kResultOk) { err = "the component refused its state"; return false; }
        cs.seek(0, IBStream::kIBSeekSet, nullptr);
        controller->setComponentState(&cs);
        if (klen > 0 && U::cast<IEditController>(component) == nullptr)
        {
            MemoryStream ks((void*)ctrl, (TSize)klen);
            controller->setState(&ks);
        }
        return true;
    }

    int latency() override { return (int)processor->getLatencySamples(); }

    int tail() override
    {
        uint32 t = processor->getTailSamples();
        return t == kInfiniteTail ? -1 : (int)t;
    }

    void idle() override
    {
        ParamRing::Item it;
        while (toController.pop(it)) controller->setParamNormalized(it.id, it.value);
    }

    int poll() override
    {
        int32 flags = restartFlags.exchange(0);
        int r = 0;
        if (flags & kLatencyChanged) { latencySamples = (int)processor->getLatencySamples(); r |= NP_POLL_LATENCY; }
        if (flags & (kParamTitlesChanged | kParamValuesChanged)) r |= NP_POLL_PARAMS;
        if (flags & (kReloadComponent | kIoChanged)) r |= NP_POLL_RESTART;
        return r;
    }

    double paramPlain(uint32_t id, double v) override { return controller->normalizedParamToPlain(id, v); }

    void reset() override
    {
        if (!active) return;
        notes.clear();
        processor->setProcessing(false);
        component->setActive(false);
        component->setActive(true);
        processor->setProcessing(true);
    }

    static FIDString platformType(const char* api)
    {
        std::string a = api ? api : "";
        if (a == "nsview") return kPlatformTypeNSView;
        if (a == "hwnd") return kPlatformTypeHWND;
        if (a == "x11") return kPlatformTypeX11EmbedWindowID;
        return nullptr;
    }

    int guiSupport(const char* api) override
    {
        FIDString t = platformType(api);
        if (!t) return 0;
        if (!view) view = owned(controller->createView(ViewType::kEditor));
        if (!view) return 0;
        // A VST3 view embeds in a parent; a floating window is the host's own window around it.
        return view->isPlatformTypeSupported(t) == kResultTrue ? 3 : 0;
    }

    bool attached = false;

    bool guiAttach(void* parent, const char* api, int& w, int& h, std::string& err) override
    {
        FIDString t = platformType(api);
        if (!t) { err = "unknown window API"; return false; }
        if (!view) view = owned(controller->createView(ViewType::kEditor));
        if (!view) { err = "the plugin has no editor"; return false; }
        if (view->isPlatformTypeSupported(t) != kResultTrue) { err = std::string("editor does not support ") + t; return false; }
        if (!frame) frame = owned(new Frame());
        view->setFrame(frame);
        if (view->attached(parent, t) != kResultOk) { err = "attached() failed"; return false; }
        attached = true;
        ViewRect r{};
        view->getSize(&r);
        w = r.getWidth();
        h = r.getHeight();
        return true;
    }

    void guiDetach() override
    {
        if (view)
        {
            if (attached) view->removed();
            view->setFrame(nullptr);
            attached = false;
            view = nullptr;
        }
    }
};

tresult PLUGIN_API Handler::performEdit(ParamID id, ParamValue v)
{
    owner->toProcessor.push(id, v);
    return kResultOk;
}

tresult PLUGIN_API Handler::restartComponent(int32 flags)
{
    owner->restartFlags |= flags;
    return kResultOk;
}

Plugin* vst3Open(const std::string& path, const std::string& id, std::string& err)
{
    auto* p = new Vst3Plugin();
    if (!p->open(path, id, err))
    {
        delete p;
        return nullptr;
    }
    return p;
}

} // namespace np
