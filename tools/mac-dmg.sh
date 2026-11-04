#!/bin/bash
# Fork (docs/specs/studio-mvp.md §3.9): the disk image people install the Mac app from. It opens
# on a window with ReBuzz.app, an arrow to a link to Applications, and how to open an app that
# isn't notarized; built with dmgbuild (MIT, from PyPI into a throwaway venv), no Finder scripting.
# Then it checks what a person gets: the image verifies, mounts with those three items, and the app
# copied out of it is whole and keeps its signature.
#
#   tools/mac-dmg.sh <ReBuzz.app> <out.dmg> [folder to install the copy into]
set -euo pipefail
app="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
dmg="$2"
install_to="${3:-}"
root="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
mnt="$work/mnt"
trap 'hdiutil detach "$mnt" -force >/dev/null 2>&1 || true' EXIT

codesign --verify --deep --strict "$app"

python3 -m venv "$work/venv"
"$work/venv/bin/pip" install -q "dmgbuild>=1.6,<2"

readme="$work/Read me first.txt"
cat > "$readme" <<'TXT'
ReBuzz for Mac (Apple silicon)

Install
  Drag ReBuzz onto the Applications folder in this window, then eject the disk image.

The first time you open it
  ReBuzz is signed but not notarized by Apple (that needs a paid Apple Developer ID), so the
  first time you open it macOS says it can't verify the app.

  1. Close that message. Don't choose Move to Trash.
  2. Open System Settings → Privacy & Security, scroll to Security, and click Open Anyway
     next to ReBuzz. Confirm with your password or Touch ID.
  3. Open ReBuzz again. From then on it opens normally.

  Or, in Terminal, once:
    xattr -dr com.apple.quarantine /Applications/ReBuzz.app

Updating
  Drag the new ReBuzz onto Applications and choose Replace. You may need to do the first-time
  step again for a new version.

Based on ReBuzz: https://github.com/wasteddesign/ReBuzz
TXT

rm -f "$dmg"
built=0
for attempt in 1 2 3; do
  # hdiutil is sometimes "Resource busy" on CI runners; a retry clears it.
  if "$work/venv/bin/dmgbuild" -s "$root/tools/mac-dmg/settings.py" \
      -D app="$app" -D readme="$readme" -D here="$root/tools/mac-dmg" "ReBuzz" "$dmg"; then
    built=1
    break
  fi
  echo "dmgbuild failed (attempt $attempt); retrying"
  sleep 10
done
[ $built = 1 ] || { echo "dmgbuild failed three times" >&2; exit 1; }

hdiutil verify "$dmg"

mkdir -p "$mnt"
hdiutil attach "$dmg" -nobrowse -readonly -noautoopen -mountpoint "$mnt" >/dev/null
ls -la "$mnt"
test -d "$mnt/ReBuzz.app"
test "$(readlink "$mnt/Applications")" = "/Applications"
test -f "$mnt/Read me first.txt"
test -f "$mnt/.DS_Store"
test -f "$mnt/.background.tiff"
echo "extended attributes on the app in the image:"; xattr -l "$mnt/ReBuzz.app" || true
codesign --verify --deep --strict "$mnt/ReBuzz.app"
if [ -n "$install_to" ]; then
  mkdir -p "$install_to"
  rm -rf "$install_to/ReBuzz.app"
  ditto "$mnt/ReBuzz.app" "$install_to/ReBuzz.app"
  codesign --verify --deep --strict "$install_to/ReBuzz.app"
  echo "installed a copy at $install_to/ReBuzz.app"
fi
hdiutil detach "$mnt" >/dev/null
trap - EXIT
rm -rf "$work"
echo "$dmg: $(du -h "$dmg" | cut -f1)"
