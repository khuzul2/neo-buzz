# BTDSys PeerCtrl – ReBuzz Managed Machine Port

A faithful C# re-implementation of the classic **BTDSys PeerCtrl v1.5/1.6**
Buzz machine, written as a ReBuzz *managed machine*.

Original C++ source © 2002–2008 Ed Powley (BTDSys).  
This port carries the same BSD-style permissive licence.

---

## What is PeerCtrl?

PeerCtrl is a **general-purpose control machine** – it controls the parameters
of other machines in the session without producing any audio of its own.  
Classic use-cases include:

| Goal | How |
|------|-----|
| Drive several parameters at once from one slider | Add one assignment per target parameter on the same track |
| Smooth (glide) any parameter | Set the **Inertia** global parameter |
| Non-linear mapping | Draw a curve in the **Mapping** editor |
| MIDI CC → Buzz parameter | Assign a CC number per track |
| Hardware controller feedback | Enable **Feedback** – controller lights snap to saved positions on reload |
| Endless rotary encoders (inc/dec) | Enable **Inc/Dec** mode on the MIDI panel |
| Tie tracks together | Enable **Slaved** on a track |

---

## Feature mapping vs. the original

| Original feature | Port status |
|---|---|
| Up to 255 tracks | 64 tracks (configurable via `MAX_TRACKS` constant) |
| Global Inertia parameter | ✅ identical behaviour |
| Per-track Slaved parameter | ✅ identical behaviour |
| Per-track Value parameter (0–65534) | ✅ identical behaviour |
| Multiple assignments per track | ✅ full support |
| Piecewise-linear mapping curve | ✅ interactive WPF editor with Mirror/Invert/Reset |
| MIDI CC input (absolute) | ✅ |
| MIDI CC learn (click Learn, move control) | ✅ |
| MIDI CC inc/dec (CC 96/97) | ✅ |
| MIDI inc/dec wrap mode | ✅ |
| MIDI feedback output | ✅ sends to all MIDI output devices |
| Ctrl Rate attribute (sub-tick updates) | Mapped to **Send Freq** attribute |
| Stop on Mute attribute | ✅ |
| Plugin interface (XY pad, Mixer GUI…) | Not ported – ReBuzz has native alternatives |
| bmx/bmxml state persistence | ✅ via `MachineState` / XML serialisation |
| ImportFinished (rename fix-up) | ✅ |

---

## Parameters

### Global
| Name | Range | Default | Description |
|------|-------|---------|-------------|
| Inertia | 0–1280 | 0 | Glide time in ticks/10. 0 = off; 10 = 1 tick; 1280 = 128 ticks |

### Track (per track)
| Name | Range | Default | Description |
|------|-------|---------|-------------|
| Slaved | 0/1 | 0 | When 1, this track mirrors the previous track's Value |
| Value | 0–65534 | 32767 | The 0–100% value sent through the mapping to the target parameter |

### Attributes (in Assignment Settings dialog)
| Name | Range | Default | Description |
|------|-------|---------|-------------|
| MIDI Inc/Dec Amount | 0–65534 | 1024 | Step size for endless-encoder CC 96/97 messages |
| Send Freq | 0–64 | 2 | How often (in Work() calls) inertia is pushed out. 0 = tick only |
| Stop on mute | 0/1 | 0 | When 1, no control changes are sent while the machine is muted |

---

## Assignment Settings dialog

Open with **right-click → Assignment Settings…**

The dialog shows one instance at a time; opening it again while it is
already visible brings it to the front.

### Track selector

Tracks that already have assignments are shown in **bright green**.
Empty tracks are shown in the default colour. This lets you see at a
glance which slots are free before adding a new assignment.

Machines in the Machine dropdown are listed **alphabetically**.

### MIDI Learn

1. Select an assignment in the list.
2. Click **Learn** (turns red).
3. Move a knob or fader on your hardware controller.
4. The Controller and Channel fields update automatically.

### Keyboard navigation in dropdowns

After selecting an item with the mouse, you can immediately use:
- **Arrow keys** – move one item at a time
- **Page Up / Page Down** – jump a page at a time
- **Enter** – confirm selection
- **Escape** – close the dropdown

### Mapping curve editor

- **Left-click** on empty canvas → adds a control point
- **Drag** an existing point → moves it
- **Right-click** on a point → removes it (endpoints are locked)
- **Right-click** on empty canvas → context menu with:
  - **Mirror** – flip the curve horizontally
  - **Invert** – flip the curve vertically
  - **Reset to linear** – restore the default straight line

The default mapping is linear: `Value=0 → 0%` of parameter range,
`Value=65534 → 100%`.

### MIDI Feedback

Enable **Feedback** on an assignment to send MIDI CC messages back to
the hardware whenever the parameter value changes. On song reload,
saved positions are transmitted to all MIDI output devices so controller
lights snap to the correct positions automatically.

Feedback is sent to **all** MIDI output devices simultaneously.
This avoids the need for a device selector and ensures hardware
connected on any port receives the update.

> **BCR2000 note:** use the BCR2000 in **absolute CC mode** (0–127).
> The **Inc/Dec** option is for Doepfer Pocket Dial-style endless
> encoders only (CC 96/97 protocol).

---

## Inertia

When **Inertia** is non-zero, every value change is interpolated over
`Inertia / 10` ticks. The interpolation step is recalculated per
`Work()` call using the host's `SamplesPerTick`.

Setting **Inertia = 0** in a pattern immediately snaps all tracks to
their target values.

---

## Building

### Prerequisites
- `dotnet` CLI ≥ 10.0
- ReBuzz installed at `C:\Program Files\ReBuzz`
  (adjust `ReBuzzDir` in `Directory.Build.props` if different)

### Steps

```powershell
cd PeerCtrl
dotnet build -c Release
```

Run as Administrator so the post-build step can copy the DLL to
`C:\Program Files\ReBuzz\Gear\Generators\`.

---

## File overview

| File | Purpose |
|------|---------|
| `PeerCtrl.cs` | Machine class, track state, data structures, inertia, MIDI |
| `SettingsWindow.xaml` | Placeholder XAML (excluded from compilation) |
| `SettingsWindow.xaml.cs` | Full code-only settings dialog + `CurveCanvas` + `DarkCombo` |
| `PeerCtrl.csproj` | .NET 10 / WPF project |
| `Directory.Build.props` | Sets `ReBuzzDir` for the project |

---

## Licence

BSD 3-Clause (matching the original BTDSys source code).

```
Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice,
  this list of conditions and the following disclaimer.
* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.
* Neither the name of BTDSys nor the names of its contributors may be used
  to endorse or promote products derived from this software without
  specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED.
```
