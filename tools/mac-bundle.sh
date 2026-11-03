#!/bin/bash
# Fork (docs/specs/studio-mvp.md §3.9): ReBuzz.app for Apple silicon, self-contained (no .NET
# install needed), with PortAudio, libsndfile (and what it links) and the Link helper inside.
# Unsigned for distribution: ad-hoc signed only, as arm64 requires. On another Mac it opens after
#   xattr -dr com.apple.quarantine ReBuzz.app
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
  dylibbundler -of -b -x "$macos/$lib" -d "$macos/libs" -p "@loader_path/libs/" >/dev/null
done

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
