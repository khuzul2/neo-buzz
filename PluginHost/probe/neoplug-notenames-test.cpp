// neoplug-notenames-test: the VST3 adapter's note names (docs/specs/daw-modes.md §8.9, NM1), against a fake IUnitInfo. The SDK's example
// plug-ins name no pitch, so there is no VST3 fixture for it: this checks np_vst3_names.h (what np_vst3.cpp reads with) with a unit whose two
// programs name keys differently, a unit without names, and a plug-in with no units. Exit 0 and "ok" when all pass.
#include "np_vst3_names.h"

#include "pluginterfaces/base/funknownimpl.h"

#include <cstdio>
#include <cstring>

using namespace Steinberg;
using namespace Steinberg::Vst;

namespace {

struct Pad { int pitch; const char* name; };

class FakeUnits : public U::Implements<U::Directly<IUnitInfo>>
{
public:
    struct Unit { UnitID id; ProgramListID list; std::vector<std::vector<Pad>> programs; bool names; };
    std::vector<Unit> units;

    int32 PLUGIN_API getUnitCount() override { return (int32)units.size(); }
    tresult PLUGIN_API getUnitInfo(int32 i, UnitInfo& info) override
    {
        if (i < 0 || i >= (int32)units.size()) return kResultFalse;
        info = UnitInfo{};
        info.id = units[i].id;
        info.programListId = units[i].list;
        return kResultOk;
    }
    int32 PLUGIN_API getProgramListCount() override { return 0; }
    tresult PLUGIN_API getProgramListInfo(int32, ProgramListInfo&) override { return kResultFalse; }
    tresult PLUGIN_API getProgramName(ProgramListID, int32, String128) override { return kResultFalse; }
    tresult PLUGIN_API getProgramInfo(ProgramListID, int32, const char8*, String128) override { return kResultFalse; }
    tresult PLUGIN_API hasProgramPitchNames(ProgramListID list, int32 program) override
    {
        const Unit* u = find(list);
        return u && u->names && program >= 0 && program < (int32)u->programs.size() ? kResultTrue : kResultFalse;
    }
    tresult PLUGIN_API getProgramPitchName(ProgramListID list, int32 program, int16 pitch, String128 name) override
    {
        const Unit* u = find(list);
        if (!u || program < 0 || program >= (int32)u->programs.size()) return kResultFalse;
        for (auto& p : u->programs[program])
            if (p.pitch == pitch)
            {
                for (int i = 0; i < 128; i++) { name[i] = (char16_t)p.name[i]; if (!p.name[i]) break; }
                return kResultOk;
            }
        return kResultFalse;
    }
    UnitID PLUGIN_API getSelectedUnit() override { return units.empty() ? 0 : units[0].id; }
    tresult PLUGIN_API selectUnit(UnitID) override { return kResultFalse; }
    tresult PLUGIN_API getUnitByBus(MediaType, BusDirection, int32, int32, UnitID&) override { return kResultFalse; }
    tresult PLUGIN_API setUnitProgramData(int32, int32, IBStream*) override { return kResultFalse; }

private:
    const Unit* find(ProgramListID list) const
    {
        for (auto& u : units) if (u.list == list) return &u;
        return nullptr;
    }
};

int failures = 0;
void check(bool ok, const char* what)
{
    printf("%s %s\n", ok ? "ok  " : "FAIL", what);
    if (!ok) failures++;
}

} // namespace

int main()
{
    auto* fake = new FakeUnits;
    IPtr<FakeUnits> keep(fake);
    std::vector<np_note_name> out;

    check(np::readVst3PitchNames(nullptr, [](UnitID, ProgramListID) { return 0; }, out) == 0, "no unit info: no names");

    check(np::readVst3PitchNames(fake, [](UnitID, ProgramListID) { return 0; }, out) == 0, "no units: no names");

    // A unit without names first, then the drum unit with two programs.
    fake->units.push_back({1, 10, {{}}, false});
    fake->units.push_back({2, 20, {{{36, "Kick"}, {38, "Snare"}, {42, "Hat"}}, {{36, "Tom"}, {40, "Rim"}}}, true});

    int program = 0;
    auto programOf = [&](UnitID unit, ProgramListID) { return unit == 2 ? program : 0; };

    out.clear();
    int n = np::readVst3PitchNames(fake, programOf, out);
    check(n == 3 && out[0].key == 36 && !strcmp(out[0].name, "Kick") && out[1].key == 38 && !strcmp(out[1].name, "Snare") &&
              out[2].key == 42 && !strcmp(out[2].name, "Hat"),
          "the first program names three keys (the unit without names is skipped)");
    check(out[0].channel == -1, "a name is for every channel");

    program = 1;
    out.clear();
    n = np::readVst3PitchNames(fake, programOf, out);
    check(n == 2 && out[0].key == 36 && !strcmp(out[0].name, "Tom") && out[1].key == 40 && !strcmp(out[1].name, "Rim"),
          "another program (a program-list change) names the keys another way");

    program = 7;
    out.clear();
    check(np::readVst3PitchNames(fake, programOf, out) == 0, "a program with no names: none");

    program = -1;
    out.clear();
    check(np::readVst3PitchNames(fake, programOf, out) == 3, "an unknown program (-1) reads the first");

    printf("%s\n", failures ? "FAILED" : "ok");
    return failures ? 1 : 0;
}
