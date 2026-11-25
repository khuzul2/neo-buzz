// neoplug: a VST3 plug-in's note names (docs/specs/daw-modes.md §8.9, NM1). VST3 has no note-name extension; what it has is IUnitInfo's
// program pitch names: a program list's programs can name each MIDI pitch ("Kick" on 36), and the program in use is the value of the unit's
// program-change parameter. This reads them for the program in use of the first unit that names any pitch. A header of its own, taking the
// unit info and a function for the current program, so a test can run it against a fake (the SDK's example plug-ins have no pitch names).
#pragma once

#include "np_internal.h"

#include "public.sdk/source/vst/utility/stringconvert.h"
#include "pluginterfaces/vst/ivstunits.h"
#include "pluginterfaces/vst/vsttypes.h"

#include <string>
#include <vector>

namespace np {

/// Fills out with the names of the program in use. programOf(unitId, listId) is that unit's current program index (0 when it can't say).
/// Returns how many names there are (0: the plug-in names no pitch). Main thread.
template <class ProgramOf>
int readVst3PitchNames(Steinberg::Vst::IUnitInfo* units, ProgramOf programOf, std::vector<np_note_name>& out)
{
    using namespace Steinberg;
    using namespace Steinberg::Vst;
    if (!units) return 0;
    int32 unitCount = units->getUnitCount();
    for (int32 u = 0; u < unitCount; u++)
    {
        UnitInfo ui{};
        if (units->getUnitInfo(u, ui) != kResultOk || ui.programListId == kNoProgramListId) continue;
        int32 program = programOf(ui.id, ui.programListId);
        if (program < 0) program = 0;
        if (units->hasProgramPitchNames(ui.programListId, program) != kResultTrue) continue;
        size_t before = out.size();
        for (int16 pitch = 0; pitch < 128; pitch++)
        {
            String128 name{};
            if (units->getProgramPitchName(ui.programListId, program, pitch, name) != kResultOk || name[0] == 0) continue;
            np_note_name e{};
            e.key = pitch;
            e.channel = -1;
            copyStr(e.name, sizeof e.name, StringConvert::convert(name));
            out.push_back(e);
        }
        if (out.size() > before) break;       // the first unit that names pitches
    }
    return (int)out.size();
}

} // namespace np
