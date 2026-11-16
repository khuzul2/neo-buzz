#!/usr/bin/env bash
# Dogfood (docs/specs/plugin-export.md §6, PX2): the exported plug-ins loaded in NeoBuzz's own Neo Plug, on the runner's OS.
#   null     every plug-in as CLAP and VST3 beside its native machine in rebuzz-headless, driven over MCP like an agent would
#            (ci/null-test.py: parameters and units, patterns, automation every block, renders nulled, .nbl and .bmx round trips)
#   gui      every plug-in's own editor in the studio's window (Windows, macOS): screenshot, a knob dragged through the window system,
#            the slot value that changed, resize, close, reopen, the song saved as .nbl and .bmx and loaded again
# Run from the repository root in bash (Git Bash on Windows):   PluginExport/ci/dogfood.sh <assets folder> <null|gui|both>
# env: RUNNER_OS, RUNNER_TEMP (the SDKs are cloned there by the workflow: vst3sdk, clap)
set -uo pipefail

ASSETS_IN="$1"; WHAT="${2:-both}"
if [ "$RUNNER_OS" = Windows ]; then T=$(cygpath -m "$RUNNER_TEMP"); W=$(pwd -W); X=.exe; L=neoplug.dll; PY=python; ASSETS=$(cygpath -m "$(cd "$ASSETS_IN" && pwd)")
else T=$RUNNER_TEMP; W=$PWD; X=; L=libneoplug.so; PY=python3; ASSETS=$(cd "$ASSETS_IN" && pwd); fi
[ "$RUNNER_OS" = macOS ] && L=libneoplug.dylib
OUT="$W/dogfood-results"; mkdir -p "$OUT/shots" "$OUT/null"
B="$W/build/PluginHost"
win=(-nologo); [ "$RUNNER_OS" = Windows ] && win+=("-p:SolutionDir=$W/" -p:ReBuzzCleanBuild=true)

echo "::group::Neo Plug's native library (neoplug, neoplug-scan)"
case "$RUNNER_OS" in
  Windows) gen=(-A x64) ;;
  Linux) gen=(-G Ninja) ;;
  *) gen=(-DCMAKE_OSX_ARCHITECTURES=arm64) ;;
esac
cmake -S PluginHost -B "$B" "${gen[@]}" -DCMAKE_BUILD_TYPE=Release -DNEOPLUG_VST3_SDK="$T/vst3sdk" -DNEOPLUG_CLAP="$T/clap" -DNEOPLUG_FIXTURES=OFF 2>&1 | tail -2
cmake --build "$B" --config Release -j 4 2>&1 | tail -2
git submodule update --init --depth 1 3rdParty/serilog/serilog
echo "::endgroup::"

echo "::group::rebuzz-headless"
dotnet build ReBuzz.Headless/ReBuzz.Headless.csproj -c Release -v:q "${win[@]}" 2>&1 | grep -E " error |Build succeeded" | sort -u
G="$W/ReBuzz.Headless/bin/Release/net10.0/Gear/NeoPlug"
H="$W/ReBuzz.Headless/bin/Release/net10.0/rebuzz-headless$X"
ls "$G" | head
echo "::endgroup::"

fail=0
NAMES=()
while IFS= read -r line; do NAMES+=("$line"); done < <($PY -c "import json;print('\n'.join(p['name'] for p in json.load(open('PluginExport/plugins.json'))))" | tr -d '\r')

if [ "$WHAT" = null ] || [ "$WHAT" = both ]; then
  echo "::group::Null tests in NeoBuzz's Neo Plug (headless, over MCP)"
  F="$T/export-plugins"; mkdir -p "$F"; cp -R "$ASSETS"/* "$F/"
  export NEOPLUG_DEBUG=1
  for n in "${NAMES[@]}"; do
    w="$OUT/null/${n// /-}"
    $PY PluginExport/ci/null-test.py "$H" "$w" --plugin "$n" --dir "$F" --lib "$G/$L" --scan "$G/neoplug-scan$X" > "$w.log" 2>&1 || true
    grep -E "^(NOTE|FAIL|NULL-TEST)" "$w.log" | cut -c1-300 | head -60
    grep -q "^NULL-TEST .* ok" "$w.log" || { echo "FAILED: $n"; fail=1; grep -q "^NULL-TEST" "$w.log" || tail -n 12 "$w.log" | cut -c1-300
      grep -E "\[neoplug\] (save|load|activate|state)" "$w/stderr.txt" 2>/dev/null | cut -c1-220 | tail -n 40; }
    rm -rf "$w"   # the renders are large; the log keeps the figures
  done
  echo "::endgroup::"
  echo "== Null test results"
  for n in "${NAMES[@]}"; do grep -h "^NULL-TEST" "$OUT/null/${n// /-}.log" | cut -c1-600 || echo "NULL-TEST $n: no result"; done
fi

if { [ "$WHAT" = gui ] || [ "$WHAT" = both ]; } && [ "$RUNNER_OS" != Linux ]; then
  echo "::group::Editors in the studio window (Windows, macOS)"
  dotnet build ReBuzz.Studio/ReBuzz.Studio.csproj -c Release -v:q "${win[@]}" 2>&1 | grep -E " error |Build succeeded" | sort -u
  STUDIO="$W/ReBuzz.Studio/bin/Release/net10.0/ReBuzz.Studio$X"
  # In process: the check reads the editor view's pixels and drags its knob, which a sandbox's window (another process, floating; the Windows default) does not allow.
  # The sandboxed editors have their own checks in tools/ci-plugin-host.sh.
  guicheck() {   # label, list of Neo Plug names, extra studio arguments...
    local label="$1" list="$2"; shift 2
    local log="$OUT/gui-$label.log"; rm -f "$log"
    NEOPLUG_PLACEMENT=inprocess NEOPLUG_DEBUG=1 NEOPLUG_LIB="$G/$L" NEOPLUG_SCAN="$G/neoplug-scan$X" NEOBUZZ_PLUGIN_PATH="$ASSETS" NEOBUZZ_PLUGIN_STANDARD=0 \
      NEOBUZZ_PLUGIN_DIR="$T/export-gui-data-$label" NEOBUZZ_PLUGIN_GUI_LOG="$log" \
      "$STUDIO" --no-audio --settings "$T/export-gui-settings-$label" --plugin-export-check "$list" --plugin-export-out "$OUT/shots/$label" "$@" > "$OUT/gui-$label.out" 2>&1 &
    local pid=$!
    ( sleep 1800; kill -9 "$pid" 2>/dev/null ) >/dev/null 2>&1 &
    local watcher=$!
    local rc=0; wait "$pid" || rc=$?; echo "studio exit code $rc ($label)"
    kill "$watcher" 2>/dev/null || true
    cut -c1-300 "$log" 2>/dev/null | head -700 || echo "(no log: the studio did not run the check)"
    if [ "$rc" != 0 ] && [ "$RUNNER_OS" = macOS ]; then   # a crash report of the studio, if the system wrote one
      for r in $(ls -t "$HOME"/Library/Logs/DiagnosticReports/*.ips 2>/dev/null | head -2); do echo "--- crash report $r"; head -c 7000 "$r" | cut -c1-400; done
    fi
    grep -E "controller edit|plug-in output|pump:|events out" "$OUT/gui-$label.out" 2>/dev/null | cut -c1-200 | head -40   # what the host saw of the plug-ins' own edits
    grep -q "plugin-gui: all passed" "$log" 2>/dev/null || { echo "--- studio output (tail)"; tail -n 25 "$OUT/gui-$label.out" | cut -c1-300; fail=1; }
  }
  clap=""; for n in "${NAMES[@]}"; do clap="$clap${clap:+|}$n (CLAP)"; done
  guicheck clap "$clap"
  guicheck vst3 "Neo Filter (VST3)|Neo Poly (VST3)|Neo Learn FM (VST3)|Neo Drums (VST3)"
  echo "::endgroup::"
  # four screenshots in the log, to look at without the artifact (base64 PNG between markers). The studio names a file after the machine
  # ("NeoBuzz-Live-Neo-FM6-CLAP-dragged.png"): the knob dragged, and one of each kind of editor.
  for n in Neo-FM6-CLAP-dragged Neo-Drums-CLAP-dragged Neo-Learn-FM-CLAP-dragged Dye-Essence-CLAP-resized; do
    f=$(ls "$OUT"/shots/clap/*"$n".png 2>/dev/null | head -1)
    [ -n "$f" ] && $PY - "$f" <<'PY'
import base64, sys
d = open(sys.argv[1], "rb").read()
print("PNG-BEGIN", sys.argv[1].split("/")[-1], len(d)); print(base64.b64encode(d).decode()); print("PNG-END")
PY
  done
fi
du -sh "$OUT" 2>/dev/null
exit $fail
