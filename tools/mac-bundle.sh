#!/bin/bash
# Fork (docs/specs/studio-mvp.md §3.9): ReBuzz.app for Apple silicon, self-contained (no .NET
# install needed), with PortAudio, libsndfile (and what it links) and the Link helper inside.
# Unsigned for distribution: ad-hoc signed only, as arm64 requires (no paid Developer ID, so not
# notarized). tools/mac-dmg.sh wraps it in the disk image people install from; the first time it
# opens, macOS asks for Privacy & Security → Open Anyway (or `xattr -dr com.apple.quarantine`).
#
#   tools/mac-bundle.sh <output folder>      (on macOS, with brew's portaudio, libsndfile, dylibbundler)
set -euo pipefail
out="${1:-out}"
root="$(cd "$(dirname "$0")/.." && pwd)"
app="$out/ReBuzz.app"
macos="$app/Contents/MacOS"
rm -rf "$app"
mkdir -p "$macos" "$app/Contents/Resources"

dotnet publish "$root/ReBuzz.Studio/ReBuzz.Studio.csproj" -c Release -r osx-arm64 --self-contained -p:UseAppHost=true -nologo -v:q -o "$macos"

# Native libraries, beside the app (NativeLibraries looks there first), with what they link.
brew_lib="$(brew --prefix)/lib"
cp "$brew_lib/libportaudio.2.dylib" "$macos/libportaudio.dylib"
cp "$brew_lib/libsndfile.1.dylib" "$macos/libsndfile.dylib"
chmod u+w "$macos"/*.dylib
for lib in libportaudio.dylib libsndfile.dylib; do
  install_name_tool -id "@loader_path/$lib" "$macos/$lib"
  dylibbundler -of -cd -b -x "$macos/$lib" -d "$macos/libs" -p "@loader_path/libs/" >/dev/null
done

# The app icon: ReBuzz's own (ReBuzz/rebuzzlogo.ico holds one 256 px PNG), as an .icns up to 256 px.
iconset="$out/ReBuzz.iconset"
rm -rf "$iconset" && mkdir -p "$iconset"
python3 - "$root/ReBuzz/rebuzzlogo.ico" "$iconset/source.png" <<'PY'
import struct, sys
data = open(sys.argv[1], 'rb').read()
count = struct.unpack('<H', data[4:6])[0]
best = max(range(count), key=lambda i: data[6 + 16 * i] or 256)
size, offset = struct.unpack('<II', data[6 + 16 * best + 8:6 + 16 * best + 16])
assert data[offset:offset + 8] == b'\x89PNG\r\n\x1a\n', 'the icon entry is not a PNG'
open(sys.argv[2], 'wb').write(data[offset:offset + size])
PY
for px in 16 32 128 256; do
  sips -z $px $px "$iconset/source.png" --out "$iconset/icon_${px}x${px}.png" >/dev/null
  if [ $px -le 128 ]; then
    sips -z $((px * 2)) $((px * 2)) "$iconset/source.png" --out "$iconset/icon_${px}x${px}@2x.png" >/dev/null
  fi
done
rm "$iconset/source.png"
iconutil -c icns "$iconset" -o "$app/Contents/Resources/ReBuzz.icns"
rm -rf "$iconset"

version="$(tr -cd '0-9' < "$root/ReBuzz/BuildNumber.txt" 2>/dev/null || echo 0)"
cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>ReBuzz</string>
  <key>CFBundleDisplayName</key><string>ReBuzz</string>
  <key>CFBundleIdentifier</key><string>org.neobuzz.rebuzz</string>
  <key>CFBundleExecutable</key><string>ReBuzz.Studio</string>
  <key>CFBundleIconFile</key><string>ReBuzz</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>0.$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSMicrophoneUsageDescription</key><string>ReBuzz records audio takes from your audio input.</string>
  <key>NSLocalNetworkUsageDescription</key><string>Ableton Link finds other apps on your network.</string>
</dict>
</plist>
PLIST

# Ad-hoc signature (free, no Developer ID): arm64 runs nothing unsigned.
codesign --force --deep --sign - "$app"
echo "$app: $(du -sh "$app" | cut -f1)"
