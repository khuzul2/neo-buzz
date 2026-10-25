# Pedal Gate

A managed **multi-purpose noise gate** effect for [ReBuzz](https://github.com/wasteddesign/ReBuzz).

---

## Features

| Feature | Detail |
|---|---|
| Threshold | Gate open threshold — 0 to −80 dBFS in 1 dB steps |
| Hysteresis | Independent open / close thresholds — prevents chatter |
| Attack | 0–500 ms — instant to slow fade-in |
| Hold | 0–2000 ms — keeps the gate open after the signal drops |
| Release | 1–2000 ms — controls how fast the gate closes |
| Range | Closed-gate attenuation floor — hard mute to a gentle dip |
| Detection | **Peak** (fast, ideal for transients) or **RMS** (smooth, great for sustained sources) |
| Lookahead | 0–25 ms look-ahead so transients are never clipped on opening |
| Bypass | Pass audio through unmodified |

---

## Requirements

- [ReBuzz](https://github.com/wasteddesign/ReBuzz) (1812-preview or later)
- [.NET 10.0 Desktop Runtime (Windows x64)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- [.NET 10.0 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) — to build from source

---

## Installation

### Option A — Build from source (recommended)

1. Open **PowerShell** in this folder.
2. Run:
   ```powershell
   .\build.ps1
   ```
   Or, if ReBuzz is in a non-default location:
   ```powershell
   .\build.ps1 -BuzzDir "D:\MyReBuzz"
   ```
3. Restart ReBuzz. **Pedal Gate** will appear in the Effects section.

### Option B — Manual `dotnet build`

```powershell
dotnet build PedalGate.csproj -c Release
```

The DLL is written directly to `C:\Program Files\ReBuzz\Gear\Effects\Pedal Gate.NET.dll`.

---

## Parameters

| Parameter | Range | Default | Description |
|---|---|---|---|
| Threshold | 0–80 | 40 | Gate open level. 0 = −80 dBFS, 80 = 0 dBFS |
| Hysteresis | 0–20 dB | 6 | Gap between open and close thresholds |
| Attack ms | 0–500 | 5 | Time to open fully |
| Hold ms | 0–2000 | 100 | Time to stay open after signal drops |
| Release ms | 1–2000 | 200 | Time to close fully |
| Range dB | 0–90 | 0 | Closed-gate floor. 0 = mute, 90 = no cut |
| Detection | Peak / RMS | Peak | Signal measurement mode |
| Lookahead ms | 0–25 | 0 | Pre-open delay to protect transients |
| Bypass | no / yes | no | Bypass all processing |

---

## How it works

### State machine

```
         level ≥ threshold
  ┌──────────────────────────────────┐
  │                                  ↓
CLOSED ──────────────────────► ATTACK ──gain→1──► OPEN
  ↑                                               │
  │                                    level < close threshold
  │                                               ↓
  │                               hold countdown ► HOLD
  │                                               │
  │                          hold expired ────────┘
  │                                               ↓
  └────────── gain→rangeLin ──────────────── RELEASE
```

**Hysteresis** means the gate opens at `Threshold` but only closes when the signal drops a further `Hysteresis` dB below that — preventing the gate from chattering on signals that hover near the threshold.

**Hold** gives the gate a minimum open time after the signal drops, which is essential for drums and percussive sources where gaps between hits are brief.

**Range** sets the gain floor when the gate is closed. At 0 it is a hard gate (≈ −90 dBFS); raised to, say, 30 it gives a 6 dB attenuation-style "duck" rather than a mute.

### Lookahead

When **Lookahead ms** > 0, the detected signal leads the output audio by that amount. This allows the gate to be fully open before a transient arrives at the output — useful on drums when even a 5 ms Attack still clips the initial hit.

### Detection modes

- **Peak** — a fast attack / slow release envelope follower. Best for percussive material.
- **RMS** — a 10 ms sliding-window root-mean-square detector. Better for sustained signals like vocals, bass, and pads where momentary peaks would cause false triggering.

---

## Typical setups

**Drum bus noise gate**
```
Threshold: 36 (−44 dBFS)   Hysteresis: 8    Attack: 1 ms
Hold: 150 ms               Release: 80 ms   Range: 0 (mute)
Detection: Peak             Lookahead: 3 ms
```

**Vocal gate**
```
Threshold: 48 (−32 dBFS)   Hysteresis: 6    Attack: 10 ms
Hold: 200 ms               Release: 300 ms  Range: 0 (mute)
Detection: RMS              Lookahead: 0 ms
```

**Ducking / tonal shaping (Range > 0)**
```
Threshold: 60 (−20 dBFS)   Hysteresis: 0    Attack: 0 ms
Hold: 0 ms                 Release: 50 ms   Range: 60 (−30 dBFS)
Detection: Peak             Lookahead: 0 ms
```

---

## License

MIT
