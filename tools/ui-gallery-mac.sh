#!/bin/bash
# Fork (docs/specs/ui-gallery.md): the real ReBuzz.app on a Mac's screen, to check the headless gallery against: the
# screen with the app open on the demo song (its window and the macOS menu bar), then each menu of the menu bar opened
# through System Events, where the Mac allows it (Accessibility; a runner may not). Fork Mac's gallery job runs it.
#
#   tools/ui-gallery-mac.sh <ReBuzz.app> <song> <output folder>
set -uo pipefail
app="$1"
song="$2"
out="$3"
mkdir -p "$out"
settings="$(mktemp -d)"
exe="$app/Contents/MacOS/ReBuzz.Studio"

system_profiler SPDisplaysDataType 2>/dev/null | grep -iE "resolution|retina" > "$out/screen.txt" || true
echo "screen: $(tr -s ' ' < "$out/screen.txt" | tr '\n' ' ')"

"$exe" "$song" --settings "$settings" > "$out/app.log" 2>&1 &
pid=$!
# Started once the engine has opened the song (the log says so), or after 60 s.
for i in $(seq 1 60); do
  grep -q "Night Drive" "$out/app.log" 2>/dev/null && break
  kill -0 "$pid" 2>/dev/null || break
  sleep 1
done
sleep 8

front="tell application \"System Events\" to set frontmost of (first process whose unix id is $pid) to true"
osascript -e "$front" > /dev/null 2> "$out/osascript.txt" || echo "frontmost: $(cat "$out/osascript.txt")"
sleep 2

shot() {
  if screencapture -x "$out/$1.png"; then echo "captured $1"; else echo "screencapture failed: $1"; fi
}

shot mac-real-010-screen

n=100
for menu in ReBuzz File Edit Transport View Window Help; do
  if [ "$menu" = ReBuzz ]; then item="menu bar item 2 of menu bar 1"; else item="menu bar item \"$menu\" of menu bar 1"; fi
  if osascript -e "tell application \"System Events\" to tell (first process whose unix id is $pid) to click $item" > /dev/null 2> "$out/osascript.txt"; then
    sleep 1
    shot "mac-real-$n-menu-$(echo "$menu" | tr '[:upper:]' '[:lower:]')"
    osascript -e 'tell application "System Events" to key code 53' > /dev/null 2>&1 || true
    sleep 1
  else
    echo "menus not opened ($menu): $(tr '\n' ' ' < "$out/osascript.txt")"
    break
  fi
  n=$((n + 1))
done

kill "$pid" 2>/dev/null || true
sleep 2
kill -9 "$pid" 2>/dev/null || true
ls -la "$out"
