#!/usr/bin/env bash
# Plug-in sandbox CI (docs/specs/plugin-host.md, H2): neoplug-server and the protocol, through the C ABI alone (neoplug-sandbox-test).
#
#   tools/ci-plugin-sandbox.sh build                          builds the native tools into build/PluginHost (SDKs cloned) when ci-plugin-host.sh has not
#   tools/ci-plugin-sandbox.sh quick                          smoke (load, play, automation, state, latency, offline render, null test against in process)
#                                                             in each placement for the CLAP and VST3 fixtures, the crash tests, round-trip latency, a short chain
#   tools/ci-plugin-sandbox.sh hour <minutes>                 the bounded "hour test": five sandboxed plug-ins in series at 128 frames for <minutes>
#                                                             minutes, deadline misses counted against an in-process baseline on the same runner
#
# From the repository root, in bash (Git Bash on Windows). Results go to plugin-host-results/sandbox-*.txt.
set -uo pipefail

if [ "${RUNNER_OS:-Linux}" = Windows ]; then T=$(cygpath -m "${RUNNER_TEMP:-/tmp}"); W=$(pwd -W); X=.exe
else T=${RUNNER_TEMP:-/tmp}; W=$PWD; X=; fi
B="$W/build/PluginHost"
OUT="$W/plugin-host-results"
mkdir -p "$OUT"
mode=${1:-quick}

build() {
  if [ -x "$B/bin/neoplug-sandbox-test$X" ] && [ -x "$B/bin/neoplug-server$X" ]; then return 0; fi
  if [ "${RUNNER_OS:-Linux}" = Linux ]; then
    sudo apt-get update -q
    sudo apt-get install -y -q ninja-build libx11-xcb-dev libxcb-util-dev libxcb-cursor-dev libxcb-xkb-dev libxkbcommon-dev \
      libxkbcommon-x11-dev libfontconfig1-dev libcairo2-dev libgtkmm-3.0-dev libsqlite3-dev libxcb-keysyms1-dev
  fi
  [ -d "$T/vst3sdk" ] || { git clone -q --depth 1 --branch v3.8.1_build_84 https://github.com/steinbergmedia/vst3sdk "$T/vst3sdk" &&
    git -C "$T/vst3sdk" -c advice.detachedHead=false submodule update -q --init --depth 1 base pluginterfaces public.sdk cmake vstgui4; }
  [ -d "$T/clap" ] || git -c advice.detachedHead=false clone -q --depth 1 --branch 1.2.10 https://github.com/free-audio/clap "$T/clap"
  case "${RUNNER_OS:-Linux}" in
    Windows) gen=(-A x64) ;;
    Linux) gen=(-G Ninja) ;;
    *) gen=(-DCMAKE_OSX_ARCHITECTURES=arm64) ;;
  esac
  cmake -S PluginHost -B "$B" "${gen[@]}" -DCMAKE_BUILD_TYPE=Release -DNEOPLUG_VST3_SDK="$T/vst3sdk" -DNEOPLUG_CLAP="$T/clap" || return 1
  cmake --build "$B" --config Release -j 4 || return 1
}

fails=0
run() {   # run <name> <command...>: output to the log and the artifact file; counts a failure
  local name=$1; shift
  echo "::group::$name"
  "$@" 2>&1 | tee -a "$OUT/sandbox-$mode.txt"
  local rc=${PIPESTATUS[0]}
  echo "::endgroup::"
  [ "$rc" -eq 0 ] || { echo "::error::$name failed ($rc)"; fails=$((fails + 1)); }
}

if [ "$mode" = build ]; then build; exit $?; fi
build || exit 1
BIN="$B/bin"
V="$B/VST3/Release"
ST="$BIN/neoplug-sandbox-test$X"
CLAP="$BIN/neoplug-test.clap"
export NEOPLUG_DEBUG=
# (the library finds neoplug-server beside itself; a Windows loader finds neoplug.dll beside the tools)
: > "$OUT/sandbox-$mode.txt"

case "$mode" in
quick)
  for id in neoplug.test.synth neoplug.test.gain neoplug.test.duck; do run "smoke $id" "$ST" smoke "$CLAP" --id "$id"; done
  run "smoke AGain (VST3)" "$ST" smoke "$V/again.vst3"
  run "smoke Note Expression Synth (VST3)" "$ST" smoke "$V/note-expression-synth.vst3"
  NEOPLUG_PROCESS_TIMEOUT_MS=300 run "crash, hang, killed" "$ST" crash "$CLAP"
  for pl in 0 1 2 3; do run "round trip, placement $pl" "$ST" latency "$CLAP" --id neoplug.test.gain --placement "$pl" --block 128 --blocks 20000; done
  run "five in series, short" "$ST" chain "$CLAP#neoplug.test.gain" "$V/again.vst3" "$CLAP#neoplug.test.gain" "$V/again.vst3" "$CLAP#neoplug.test.gain" \
      --placement 1 --block 128 --seconds 20 --baseline 10
  ;;
hour)
  minutes=${2:-10}
  secs=$((minutes * 60))
  base=$((secs / 5)); [ "$base" -gt 180 ] && base=180; [ "$base" -lt 30 ] && base=30
  run "hour test: five sandboxed plug-ins in series, 128 frames, $minutes min" "$ST" chain "$CLAP#neoplug.test.gain" "$V/again.vst3" "$CLAP#neoplug.test.gain" "$V/again.vst3" "$CLAP#neoplug.test.gain" \
      --placement 1 --block 128 --seconds "$secs" --baseline "$base"
  ;;
*) echo "usage: $0 build|quick|hour <minutes>"; exit 2 ;;
esac
exit $((fails > 0 ? 1 : 0))
# (H2: macOS joins when the commit message says [mac])
