## How it sounds

A spectrum and level display: it analyses the audio (left, right or both) with a chosen window and shows low, mid and high band levels, volume, stereo balance and correlation in its own window. It makes no sound and changes nothing.

## Use it for

- Watching the balance of low, mid and high on a bus.
- Checking stereo balance and phase correlation by eye.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Analysed channel | Mix | Left, Right or Mix |
| FFT window | Hanning | None, Hanning, Hamming and others |
| Low band ceiling | 300 Hz | where the low meter stops |
| High band floor | 2400 Hz | where the high meter starts |
| Volume smoothing | 0.01 s | how calm the volume trace is, 1 to 100 ms |

## Starting points

- Defaults: Mix, Low 300 Hz, High 2400 Hz.
- The same readings through the tools: `analyze_mix` and `meters_read(machines=["Master"])`.

## Pitfalls

- It is a window: the Studio does not show it, and the tools give the same numbers.
- Unverified: what Display scale scales, and what it listens to when nothing is connected.

## Pairs well with

- Neo Analyzer and Neo Scope for the same job inside the Studio.

## Successor

Neo Analyzer and Neo Scope do the same job with the app's own views: `machine_replace(machine="SpectrumBlock")` does not apply, because it is a display, not a processor.

## Checklist

- [ ] `machine_describe(machine="SpectrumBlock")`: twelve parameters, `Analysed channel` first.
- [ ] `meters_read(machines=["Master"])`: the same levels without a window.
- [ ] `analyze_mix`: the tools read the spectral balance too.
