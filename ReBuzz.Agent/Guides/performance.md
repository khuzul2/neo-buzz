# Performance and latency

Two different problems share one fix list. **Dropouts** (clicks, gaps, crackle) mean the engine did not finish a buffer in time: too much work for the buffer, or something else on the computer took the time. **Latency** is the delay between a key press (or a launch) and the sound, and what you pay for fixing dropouts with a bigger buffer. Renders are unaffected by both: they run faster than real time into a file, with no device. Only live playing needs this guide, so check `studio_status` for the mode.

## Read the state first

- `audio_status`: the device and its driver family, `buffer_frames`, `sample_rate`, `latency_ms` (from a rendered sample to the speaker), `dropouts` (buffers that missed their deadline since the app started), `worst_overrun_ms` (how far past its deadline the slowest ran), `played_seconds` and `buffer_and_rate_settable`. "No audio output is open" means no device, so nothing is live.
- `app_status`: the process's CPU (`process_percent` of all cores, `busy_cores`), memory, threads. The studio's status line shows the engine's own load: `CPU n%` is the time spent rendering each buffer against the time the buffer allows, and `dropouts` is the same counter as `audio_status`. At 100 % the engine takes the whole buffer's time, so a dropout is certain; keep well below it.
- The question to answer: do the `dropouts` rise while it plays? Take `audio_status` before and after a minute of playing. A steady count that does not move is history, not a problem.

## The drivers

`audio_devices` lists the output devices with their driver family (`kind`), the buffer sizes and rates `audio_set` takes, and `buffer_and_rate_settable`. A device's mode is part of its name, so `audio.Device` (and `audio_set(device="…")`) holds the choice. On Windows, in the order the studio offers them:

| Driver | What it is | Buffer |
|---|---|---|
| ASIO | The interface's own installed driver; the default when one is installed. | Set in the driver's own control panel, not by `audio_set` |
| WASAPI, shared, low latency | The default without ASIO: the system mixer with the smallest buffer it allows. | The driver reports it (`audio_status`) |
| WASAPI, shared | The classic shared mode: other programs keep playing; a larger buffer. | The driver reports it |
| WASAPI, exclusive | Event-driven, but other programs' sound stops while it plays. Offered only while `audio.WasapiExclusive` is on. | The driver reports it |
| WDM-KS (PortAudio) | Listed only with `audio.ShowAdvancedDevices` on; most people do not need it. | Settable |

WASAPI lists the system default of each mode first (it follows Windows when the default output changes) and then every active output. A saved output that is unplugged plays through the system default of the same mode and returns when plugged in. Other platforms use PortAudio: the buffer and rate are settable.

- `audio_set(buffer_frames=256)` and `sample_rate` apply only where `buffer_and_rate_settable` is true. For ASIO and WASAPI, say so and ask the person to change it in the driver's panel or Preferences ▸ Audio. They apply at once and the person hears them: say what you are about to do.
- A device or buffer that does not open, plays no audio within 10 seconds, or drops out in the moments after is put back by the automatic revert (`applied: false`, `reverted: true`, `reason`). Do not retry the same values: try a larger buffer or another device.

## Buffer against latency

At 48 kHz: 32 frames 0.7 ms, 64 1.3 ms, 128 2.7 ms, 256 5.3 ms, 512 10.7 ms, 1024 21 ms, 2048 43 ms, one buffer each way before the device's own delay; `latency_ms` is the figure the driver reports for the whole path. Playing live wants 128 to 256; mixing and listening do not care; rendering does not use it. A dropout is far worse than a few ms: take the smaller buffer only when `dropouts` stays still.

Machines add latency too. A lookahead effect (Neo Comp's `Lookahead`, Neo Limiter, Neo Multiband in linear phase at about 96 ms, Neo Drive's 62 samples) is compensated by delaying the other paths, so the whole mix is late by the longest one while compensation is on (`guide(topic="troubleshooting")`, Latency). For something played live, keep such machines off the monitored path or lower their lookahead, and keep Neo Multiband out of linear phase.

## Find the machine that costs the time

- `render(from=0, to=8, cpu=true)` returns each machine's own work time per second of audio, in ms/s (the result's `cpu` rows; the text line says `CPU: Bass 2.96 ms/s`). One second of audio at 100 % of one core is 1000 ms/s, so a row of 10 is 1 % of a core; the engine spreads machines over `audio.AudioThreads` threads. Compare the rows, not the absolute figures: a machine is a problem when it is far above the others or climbs as the song gets busier. Render the busiest bars, with the most notes playing.
- `cpu_window_seconds=0.5` adds the same figure window by window: a machine whose time climbs after its input stops is working on denormals or a long tail (`guide(topic="troubleshooting")`).
- A Neo Patch machine reports its own cost: `patch_describe(machine="Lead")` has `cpu` in percent of one core, and the node count against the cap.
- Machine pages say what a machine's cost is made of. Heavy: voices (polyphony times unison; Neo Poly has a 32-voice pool), Neo FM6 (6 operators, 2× oversampling), Neo Wave, Neo Drive at 8×, Dye Essence's Harmonic (per partial), Modal (per mode), Granular (per live grain) and High 4× quality, and long reverb tails. `rebuzz://catalog` marks machines the profiler found hot.

## The ladder of fixes

Take one step, measure, stop when it is enough.

1. **Stop other work.** No `render`, `analyze_mix` or `export_audio` while playing: a render pauses the live output. Close what else uses the computer.
2. **Bigger buffer.** `audio_set(buffer_frames=512)` where settable; otherwise the driver's panel. Check `dropouts` for a minute.
3. **Lighter machines.** Fewer voices (`Polyphony`, `Unison`), lower oversampling or quality (Neo Drive `Quality` 2×, Neo FM6's `Engine` Raw, Dye Essence `Quality` Standard 2×) and fewer slots. Bounce a finished part to a sample with `wave_from_render(source=…)` and play that on Neo Sampler. These change the sound a little: say so.
4. **Fewer machines.** Mute what is not needed in the section (`machine_set(machine=…, mute=true)`), though muted machines still process while `advanced.ProcessMutedMachines` is on (more CPU, smoother transitions).
5. **Engine settings** (class `engine`: they change how audio is made, so render before and after and change one at a time). `settings_get(section="audio")`: `audio.AudioThreads` (1 to 8, default 4) and `audio.WorkAlgorithm` (how machines are scheduled onto the threads), both needing a restart. `settings_get(section="advanced", search="SubTick")`: `advanced.SubTickResolution` Lower or Low decreases CPU load. `advanced.Multithreading` is on by default: leave it on.
6. **Tell the person** what remains: a faster computer, a different interface, or rendering instead of playing.

## Do not

- Do not shrink the buffer to impress: stop at the smallest size that keeps `dropouts` still.
- Do not loosen the safety settings (limiter, clip threshold, machine guard) to quiet a meter; they are not a performance control.
- Do not change the audio device or engine settings while the person is playing or an audience is listening.
- Do not diagnose from a render: it does not run in real time. A render that is fast says the song can be played, not that it will be on this device.

## Checklist

- [ ] You know the state: `audio_status` (buffer, `latency_ms`, `dropouts`) and `app_status` (CPU).
- [ ] The cost has a name: `render(from=…, to=…, cpu=true)` on the busiest bars shows which machine.
- [ ] Each change was one step, measured: `audio_status` before and after a minute of playing, `dropouts` unchanged.
- [ ] The latency chosen suits the use: `audio_status` reports `latency_ms`; live playing wants 128 to 256 frames or fewer.
- [ ] The device change is kept only if it stayed: `audio_set` answered `applied: true`, not `reverted: true`.
