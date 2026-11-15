#!/usr/bin/env bash
# Plug-in host CI (docs/specs/plugin-host.md, H1): builds the native library with CMake (SDKs cloned, the SDK's AGain and
# Note Expression Synth as fixtures), installs Surge XT on the runner (GPL: a test fixture, never shipped), builds
# rebuzz-headless with the Neo Plug machines and runs tools/plugin-host-test.py. For fork-ci (Linux), fork-mac and
# fork-package (Windows); run from the repository root in bash (Git Bash on Windows).
#
#   tools/ci-plugin-host.sh            env: RUNNER_OS, RUNNER_TEMP, GH_TOKEN (for Surge XT's release list)
set -euo pipefail

if [ "$RUNNER_OS" = Windows ]; then T=$(cygpath -m "$RUNNER_TEMP"); W=$(pwd -W); X=.exe; L=neoplug.dll; PY=python
else T=$RUNNER_TEMP; W=$PWD; X=; L=libneoplug.so; PY=python3; fi
[ "$RUNNER_OS" = macOS ] && L=libneoplug.dylib
B="$W/build/PluginHost"
OUT="$W/plugin-host-results"
mkdir -p "$OUT"

echo "::group::SDKs (VST3 3.8.1, CLAP 1.2.10)"
if [ "$RUNNER_OS" = Linux ]; then
  sudo apt-get update -q
  sudo apt-get install -y -q ninja-build libx11-xcb-dev libxcb-util-dev libxcb-cursor-dev libxcb-xkb-dev libxkbcommon-dev \
    libxkbcommon-x11-dev libfontconfig1-dev libcairo2-dev libgtkmm-3.0-dev libsqlite3-dev libxcb-keysyms1-dev
fi
git clone -q --depth 1 --branch v3.8.1_build_84 https://github.com/steinbergmedia/vst3sdk "$T/vst3sdk"
git -C "$T/vst3sdk" -c advice.detachedHead=false submodule update -q --init --depth 1 base pluginterfaces public.sdk cmake vstgui4
git -c advice.detachedHead=false clone -q --depth 1 --branch 1.2.10 https://github.com/free-audio/clap "$T/clap"
echo "::endgroup::"

echo "::group::neoplug, neoplug-scan, neoplug-test.clap and the SDK's examples"
case "$RUNNER_OS" in
  Windows) gen=(-A x64) ;;
  Linux) gen=(-G Ninja) ;;
  *) gen=(-DCMAKE_OSX_ARCHITECTURES=arm64) ;;   # (never an empty array: macOS's bash 3.2 calls it unbound under set -u)
esac
cmake -S PluginHost -B "$B" "${gen[@]}" -DCMAKE_BUILD_TYPE=Release -DNEOPLUG_VST3_SDK="$T/vst3sdk" -DNEOPLUG_CLAP="$T/clap"
cmake --build "$B" --config Release -j 4
ls -la "$B/bin"
echo "::endgroup::"

echo "::group::Surge XT (a test fixture on the runner only)"
$PY tools/get-surge.py "$T/surge" "$T/surge.txt"
echo "::endgroup::"

echo "::group::rebuzz-headless with the Neo Plug machines"
git submodule update --init --depth 1 3rdParty/serilog/serilog
# On Windows the projects' restore hack needs SolutionDir and ReBuzzCleanBuild, as fork-ci's core-build.
win=(-nologo); [ "$RUNNER_OS" = Windows ] && win+=("-p:SolutionDir=$W/" -p:ReBuzzCleanBuild=true)
dotnet build ReBuzz.Headless/ReBuzz.Headless.csproj -c Release -v:q "${win[@]}"
ls ReBuzz.Headless/bin/Release/net10.0/Gear/NeoPlug
echo "::endgroup::"

# The fixtures in one folder (the SDK's examples include plug-ins made to misbehave), Surge XT's in its own.
F="$T/plugin-fixtures"
mkdir -p "$F"
cp -R "$B/VST3/Release/again.vst3" "$B/VST3/Release/note-expression-synth.vst3" "$B/bin/neoplug-test.clap" "$F/"
dirs=(--dir "$F")
surge=()
surgepath=""
sep=:; [ "$RUNNER_OS" = Windows ] && sep=";"
if [ -s "$T/surge.txt" ]; then
  while IFS='|' read -r p _; do
    [ -n "$p" ] && dirs+=(--dir "$(dirname "$p")") && surgepath="$surgepath${surgepath:+$sep}$(dirname "$p")"
  done < "$T/surge.txt"
  surge=(--gen "Surge XT (VST3)" --gen "Surge XT (CLAP)" --fx "Surge XT Effects (VST3)" --fx "Surge XT Effects (CLAP)")
else
  echo "::error::Surge XT could not be installed (see the log above)"
  exit 1
fi

H="$W/ReBuzz.Headless/bin/Release/net10.0/rebuzz-headless$X"
G="$W/ReBuzz.Headless/bin/Release/net10.0/Gear/NeoPlug"
export NEOPLUG_DEBUG=1
$PY tools/plugin-host-test.py "$H" "$OUT/run" "${dirs[@]}" --lib "$G/$L" --scan "$G/neoplug-scan$X" \
  --gen "Note Expression Synth" --gen "Test Synth" "${surge[@]}" --fx "AGain VST3" --fx "Test Gain" --fixtures

# The plug-in editors in the studio, in a real window (H3): Surge XT's own editors embedded in the studio's window (VST3 and CLAP), then
# floating; each run opens, draws (colours in the view), resizes from the host, closes and reopens (Platform/PluginGuiCheck.cs).
# Windows and macOS only: Linux has no editors to show.
if [ "$RUNNER_OS" != Linux ]; then
  echo "::group::Plug-in editors in the studio (Surge XT)"
  dotnet build ReBuzz.Studio/ReBuzz.Studio.csproj -c Release -v:q "${win[@]}"
  STUDIO="$W/ReBuzz.Studio/bin/Release/net10.0/ReBuzz.Studio"
  [ "$RUNNER_OS" = Windows ] && STUDIO="$STUDIO.exe"
  guifail=0
  guicheck() {   # name, extra arguments...
    local label="$1" plug="$2"; shift 2
    local log="$OUT/plugin-gui-$label.log"
    rm -f "$log"
    NEOPLUG_LIB="$G/$L" NEOPLUG_SCAN="$G/neoplug-scan$X" NEOBUZZ_PLUGIN_PATH="$surgepath" NEOBUZZ_PLUGIN_STANDARD=0 \
      NEOBUZZ_PLUGIN_DIR="$T/gui-plugin-data-$label" NEOBUZZ_PLUGIN_GUI_LOG="$log" \
      "$STUDIO" --no-audio --settings "$T/gui-settings-$label" --plugin-gui-check "$plug" "$@" > "$OUT/plugin-gui-$label.out" 2>&1 &
    local pid=$!
    ( sleep 120; kill -9 "$pid" 2>/dev/null ) >/dev/null 2>&1 &   # macOS has no timeout(1)
    local watcher=$!
    wait "$pid"; echo "studio exit code $? ($label)"
    kill "$watcher" 2>/dev/null || true
    cat "$log" 2>/dev/null || echo "(no log: the studio did not run the check)"
    grep -q "plugin-gui: all passed" "$log" 2>/dev/null || { echo "--- studio output (tail)"; tail -n 25 "$OUT/plugin-gui-$label.out" | cut -c1-300; }
    grep -q "plugin-gui: all passed" "$log" 2>/dev/null || guifail=1
  }
  guicheck vst3 "Surge XT (VST3)"
  guicheck clap "Surge XT (CLAP)"
  guicheck float "Surge XT (VST3)" --plugin-gui-floating
  guicheck fx "Surge XT Effects (VST3)" --plugin-gui-fx
  echo "::endgroup::"
  [ "$guifail" = 0 ] || { echo "::error::a plug-in editor check failed (see plugin-gui-*.log)"; exit 1; }
fi

# 0 bytes a buffer on our side of the audio path (AudioHygieneTests, Linux only: its Gear is the studio flavour's).
if [ "$RUNNER_OS" = Linux ]; then
  NEOBUZZ_PLUGIN_PATH="$F" NEOBUZZ_PLUGIN_DIR="$T/studio-plugin-data" NEOBUZZ_PLUGIN_STANDARD=0 \
    dotnet test ReBuzz.Studio.Tests -c Debug --filter "FullyQualifiedName~ANeoPlugHostingAPlugIn" 2>&1 | tee "$OUT/alloc-test.log"
  grep -Eq "Passed: +1,.*Skipped: +0" "$OUT/alloc-test.log" || { echo "::error::the Neo Plug allocation test did not run and pass"; exit 1; }
fi
