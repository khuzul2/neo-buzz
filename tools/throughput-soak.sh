#!/usr/bin/env bash
# Fork (docs/specs/machines.md §4, "Throughput guards"): calibrates throughput guards (ReBuzzTests/Automation/Throughput.cs).
# Runs the guards a filter selects, each measured RUNS times (NEOBUZZ_THROUGHPUT_SOAK), quiet and with a busy loop on
# every core, then with every machine chunk's work doubled (NEOBUZZ_THROUGHPUT_SLOWDOWN=2), which every run must fail.
# Each run's figures and each guard's summary are printed, prefixed by the condition; a guard's floor goes between the
# slowest run's figure and half the fastest's.
#
#   tools/throughput-soak.sh <test project or DLL> <dotnet test filter> [runs] [dotnet test options...]
#   tools/throughput-soak.sh NeoBuzz.MachineTests "FullyQualifiedName~SixteenVoicesRunFarFasterThanRealTime" 20
#
# The project must be built (dotnet test --no-build); SOAK_OUT names the folder for the logs (default soak).
set -u
target=$1
filter=$2
runs=${3:-20}
shift $(( $# < 3 ? $# : 3 ))
out=${SOAK_OUT:-soak}
mkdir -p "$out"
out=$(cd "$out" && pwd)
run() {
  label=$1; shift
  rm -rf "$out/$label"; mkdir -p "$out/$label"
  env MEASUREMENTS_DIR="$out/$label" "$@" dotnet test "$target" --no-build --filter "$filter" --logger "console;verbosity=minimal" ${EXTRA[@]+"${EXTRA[@]}"} > "$out/$label.log" 2>&1
  echo "== $label (exit $?)"
  sed "s/^/[$label] /" "$out/$label/throughput.txt" 2>/dev/null
}
EXTRA=("$@")
run quiet NEOBUZZ_THROUGHPUT_SOAK="$runs"
run quiet-slow2 NEOBUZZ_THROUGHPUT_SOAK=5 NEOBUZZ_THROUGHPUT_SLOWDOWN=2
pids=()
trap 'kill ${pids[@]+"${pids[@]}"} 2>/dev/null' EXIT
n=${NUMBER_OF_PROCESSORS:-$(nproc 2>/dev/null || sysctl -n hw.ncpu)}
for i in $(seq "$n"); do (while :; do :; done) & pids+=($!); done
run loaded NEOBUZZ_THROUGHPUT_SOAK="$runs"
run loaded-slow2 NEOBUZZ_THROUGHPUT_SOAK=5 NEOBUZZ_THROUGHPUT_SLOWDOWN=2
