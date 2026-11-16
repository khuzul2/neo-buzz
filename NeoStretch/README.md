# neostretch

A small C library behind Neo Audio's Pro warp mode (docs/specs/neo-audio.md section 11). It wraps Signalsmith Stretch behind a plain C ABI (`neostretch.h`) so the engine can load it with `NativeLibraries.TryLoad` and call it from the audio thread without allocation, locks or exceptions.

## Vendored code

| Library | Version | Licence | Where |
|---|---|---|---|
| Signalsmith Stretch | 1.3.2 | MIT | `third_party/signalsmith-stretch/` |
| Signalsmith Linear (FFT, STFT) | 0.6.6 | MIT | `third_party/signalsmith-linear/` |

Only the headers and the licence texts are kept. One line of `signalsmith-stretch.h` is patched (`peaks.reserve(bands/2 + 1)`, marked `neostretch:`) so that the peak search does not allocate while processing. To update, copy the new headers over, re-apply that line and run the test.

## Build and test

```
cmake -S NeoStretch -B build/NeoStretch
cmake --build build/NeoStretch --config Release
ctest --test-dir build/NeoStretch -C Release --output-on-failure
```

The workflows, `build.ps1` and `tools/mac-bundle.sh` do this; the engine looks for `neostretch.dll`, `libneostretch.dylib` or `libneostretch.so` in the app's folder (or in the folder named by `NEO_NATIVE_DIR`). Without it Pro plays as Complex.
