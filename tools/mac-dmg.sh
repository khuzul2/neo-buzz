#!/bin/bash
# Fork (docs/specs/studio-mvp.md §3.9): the disk image people install the Mac app from. It opens
# on a window with NeoBuzz Live.app, an arrow to a link to Applications, and how to open an app that
# isn't notarized; built with dmgbuild (MIT, from PyPI into a throwaway venv), no Finder scripting.
# Then it checks what a person gets: the image verifies and mounts as "NeoBuzz Live" with those three
# items, the app in it is named NeoBuzz Live (its Info.plist), whole and signed, the read-me says how
# to install and open it, and the app copied out of it keeps its signature.
#
#   tools/mac-dmg.sh <NeoBuzz Live.app> <out.dmg> [folder to install the copy into]
#   (fork-mac.yml: NeoBuzz-Live-macos-arm64.dmg; mac-release.yml publishes it)
set -euo pipefail
app="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
dmg="$2"
install_to="${3:-}"
root="$(cd "$(dirname "$0")/.." && pwd)"
name="NeoBuzz Live"                 # the app's name (ReBuzz.Product.Name), the volume's and the bundle's
bundle="$name.app"
work="$(mktemp -d)"
mnt="$work/mnt"
trap 'hdiutil detach "$mnt" -force >/dev/null 2>&1 || true' EXIT

test "$(basename "$app")" = "$bundle" || { echo "expected $bundle, got $(basename "$app")" >&2; exit 1; }
codesign --verify --deep --strict "$app"

python3 -m venv "$work/venv"
"$work/venv/bin/pip" install -q "dmgbuild>=1.6,<2"

readme="$work/Read me first.txt"
cat > "$readme" <<'TXT'
NeoBuzz Live for Mac (Apple silicon)

Install
  Drag NeoBuzz Live onto the Applications folder in this window, then eject the disk image.

The first time you open it
  NeoBuzz Live is signed but not notarized by Apple (that needs a paid Apple Developer ID), so the
  first time you open it macOS says it can't verify the app.

  1. Close that message. Don't choose Move to Trash.
  2. Open System Settings → Privacy & Security, scroll to Security, and click Open Anyway
     next to NeoBuzz Live. Confirm with your password or Touch ID.
  3. Open NeoBuzz Live again. From then on it opens normally.

  Or, in Terminal, once:
    xattr -dr com.apple.quarantine "/Applications/NeoBuzz Live.app"

Updating
  Drag the new NeoBuzz Live onto Applications and choose Replace. You may need to do the first-time
  step again for a new version. A ReBuzz.app in Applications is this app under its earlier name:
  move it to the Trash. NeoBuzz Live goes on with its settings.

NeoBuzz Live is based on ReBuzz, by WDE and the ReBuzz contributors (MIT licence):
https://github.com/wasteddesign/ReBuzz
TXT

rm -f "$dmg"
built=0
for attempt in 1 2 3; do
  # hdiutil is sometimes "Resource busy" on CI runners; a retry clears it.
  if "$work/venv/bin/dmgbuild" -s "$root/tools/mac-dmg/settings.py" \
      -D app="$app" -D readme="$readme" -D here="$root/tools/mac-dmg" "$name" "$dmg"; then
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
# The volume's name, as Finder and the desktop show it.
volume="$(diskutil info "$mnt" | sed -n 's/^ *Volume Name: *//p')"
test "$volume" = "$name" || { echo "the volume is \"$volume\", not \"$name\"" >&2; exit 1; }
test -d "$mnt/$bundle"
test ! -e "$mnt/ReBuzz.app"
test "$(readlink "$mnt/Applications")" = "/Applications"
test -f "$mnt/Read me first.txt"
test -f "$mnt/.DS_Store"
test -f "$mnt/.background.tiff"
# The app in it, by its Info.plist: named NeoBuzz Live, its executable there, the identifier unchanged.
plist="$mnt/$bundle/Contents/Info.plist"
for key in CFBundleName CFBundleDisplayName CFBundleExecutable; do
  value="$(/usr/libexec/PlistBuddy -c "Print :$key" "$plist")"
  test "$value" = "$name" || { echo "$key is \"$value\", not \"$name\"" >&2; exit 1; }
done
test "$(/usr/libexec/PlistBuddy -c "Print :CFBundleIdentifier" "$plist")" = "org.neobuzz.rebuzz"
test -x "$mnt/$bundle/Contents/MacOS/$name"
# The read-me: how to install it and open it the first time, by its name, and what it is based on.
while IFS= read -r line; do
  grep -F -- "$line" "$mnt/Read me first.txt" >/dev/null || { echo "Read me first.txt doesn't say: $line" >&2; exit 1; }
done <<'LINES'
NeoBuzz Live for Mac (Apple silicon)
Drag NeoBuzz Live onto the Applications folder in this window
click Open Anyway
next to NeoBuzz Live. Confirm with your password or Touch ID.
xattr -dr com.apple.quarantine "/Applications/NeoBuzz Live.app"
NeoBuzz Live is based on ReBuzz, by WDE and the ReBuzz contributors (MIT licence):
https://github.com/wasteddesign/ReBuzz
LINES
echo "extended attributes on the app in the image:"; xattr -l "$mnt/$bundle" || true
codesign --verify --deep --strict "$mnt/$bundle"
if [ -n "$install_to" ]; then
  mkdir -p "$install_to"
  rm -rf "$install_to/$bundle"
  ditto "$mnt/$bundle" "$install_to/$bundle"
  codesign --verify --deep --strict "$install_to/$bundle"
  echo "installed a copy at $install_to/$bundle"
fi
hdiutil detach "$mnt" >/dev/null
trap - EXIT
rm -rf "$work"
echo "$dmg: $(du -h "$dmg" | cut -f1); the volume \"$name\" with $bundle, Applications and Read me first.txt"
