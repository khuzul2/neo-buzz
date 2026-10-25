# Pedal Gain

A lightweight stereo **gain effect** for [ReBuzz](https://github.com/wasteddesign/ReBuzz) with a **mute control** and a smooth **inertia fade** when muting or unmuting.

---

## Parameters

| Parameter | Range | Default | Description |
|-----------|-------|---------|-------------|
| **Gain** | 0 – 200 | 100 | Output level in percent. 100 = unity (0 dB), 200 = +6 dB, 50 = −6 dB |
| **Mute** | off / on | off | Silences the output. The transition follows the Inertia setting |
| **Inertia** | 0 – 500 ms | 20 ms | Fade duration when Mute is toggled. 0 = instant hard cut |

### How Inertia works

When **Mute** is switched on the output gain ramps linearly from its current value down to zero over the number of milliseconds set by **Inertia**. When **Mute** is switched off the gain ramps back up to the **Gain** target over the same duration. Setting **Inertia** to 0 produces an instant (sample-accurate) cut with no click suppression — useful when you want a hard gate. A value of 20 ms is the sweet spot for a pop-free mute on most material.

---

## Requirements

- [ReBuzz](https://github.com/wasteddesign/ReBuzz) (any recent release)
- [.NET 10 Desktop Runtime — Windows x64](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) — only needed to build from source

---

## Installation

1. Copy `Pedal Gain.NET.dll` into your ReBuzz Effects gear folder:
   ```
   C:\Program Files\ReBuzz\Gear\Effects\
   ```
2. (Re)start ReBuzz. **Pedal Gain** will appear under **Effects** in the machine list.

---

## Building from source

Open a terminal in the project folder and run:

```powershell
dotnet build PedalGain.csproj -c Release
```

The DLL `Pedal Gain.NET.dll` is written directly to `C:\Program Files\ReBuzz\Gear\Effects\`.

If ReBuzz is installed in a non-default location, pass the path:

```powershell
dotnet build PedalGain.csproj -c Release /p:BuzzDir="D:\MyReBuzz"
```

---

## License

MIT
