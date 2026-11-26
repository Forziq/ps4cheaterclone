#!/usr/bin/env bash
# Builds PS4Mem.app: the SwiftUI app with the self-contained backend inside, so it runs on a Mac
# without .NET installed. Needs the .NET 10 SDK and the Xcode Command Line Tools to build.
#
#   scripts/build-app.sh                  build build/PS4Mem.app
#   OUT=/Applications scripts/build-app.sh   put the app somewhere else
#   RID=osx-x64 scripts/build-app.sh      build the backend for Intel Macs (default: this Mac's chip)
#
# Layout:
#   PS4Mem.app/Contents/Info.plist
#   PS4Mem.app/Contents/MacOS/PS4Mem                    the SwiftUI app
#   PS4Mem.app/Contents/Resources/AppIcon.icns
#   PS4Mem.app/Contents/Resources/backend/PS4Mem.Api    the backend (dotnet publish --self-contained)
#   PS4Mem.app/Contents/Resources/backend/payloads/     the ps4debug .bin from payloads/, if there is one
#
# The app starts the backend on 127.0.0.1 when it opens and stops it when it quits. The bundle is
# signed ad hoc (codesign -s -): fine on the Mac that built it, not for handing out.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

VERSION="${VERSION:-0.2.0}"
OUT="${OUT:-$ROOT/build}"
case "$(uname -m)" in
    arm64) DEFAULT_RID=osx-arm64 ;;
    *)     DEFAULT_RID=osx-x64 ;;
esac
RID="${RID:-$DEFAULT_RID}"

APP="$OUT/PS4Mem.app"
WORK="$ROOT/build/work"
CONTENTS="$APP/Contents"
BACKEND="$CONTENTS/Resources/backend"

echo "==> Cleaning $APP"
rm -rf "$APP" "$WORK"
mkdir -p "$CONTENTS/MacOS" "$BACKEND" "$WORK"

echo "==> Publishing the backend ($RID, self-contained)"
dotnet publish src/PS4Mem.Api -c Release -r "$RID" --self-contained -o "$BACKEND" --nologo -v quiet \
    -p:DebugType=None -p:GenerateDocumentationFile=false

echo "==> Building the app (swift build -c release)"
swift build -c release --package-path mac
cp "$(swift build -c release --package-path mac --show-bin-path)/PS4Mem" "$CONTENTS/MacOS/PS4Mem"

shopt -s nullglob
payloads=(payloads/*.bin)
shopt -u nullglob
if (( ${#payloads[@]} > 0 )); then
    echo "==> Copying the payload: ${payloads[*]}"
    mkdir -p "$BACKEND/payloads"
    cp "${payloads[@]}" "$BACKEND/payloads/"
else
    echo "==> No payloads/*.bin found: choose the payload in the app's Inject sheet instead."
fi

echo "==> Drawing the icon"
swift scripts/make-icon.swift "$WORK/icon.png"
ICONSET="$WORK/AppIcon.iconset"
mkdir -p "$ICONSET"
for px in 16 32 128 256 512; do
    sips -z "$px" "$px" "$WORK/icon.png" --out "$ICONSET/icon_${px}x${px}.png" >/dev/null
    sips -z $((px * 2)) $((px * 2)) "$WORK/icon.png" --out "$ICONSET/icon_${px}x${px}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$CONTENTS/Resources/AppIcon.icns"

echo "==> Writing Info.plist"
cat > "$CONTENTS/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>PS4 Mem</string>
    <key>CFBundleDisplayName</key>
    <string>PS4 Mem</string>
    <key>CFBundleExecutable</key>
    <string>PS4Mem</string>
    <key>CFBundleIdentifier</key>
    <string>io.github.ps4mem.PS4Mem</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleVersion</key>
    <string>$VERSION</string>
    <key>CFBundleIconFile</key>
    <string>AppIcon</string>
    <key>LSMinimumSystemVersion</key>
    <string>14.0</string>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.developer-tools</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSLocalNetworkUsageDescription</key>
    <string>PS4 Mem connects to your PS4 on the local network to read and change game memory.</string>
</dict>
</plist>
PLIST
plutil -lint "$CONTENTS/Info.plist" >/dev/null

echo "==> Signing ad hoc"
# Every Mach-O file in the backend first (inside out), then the bundle, which seals the rest.
while IFS= read -r -d '' file; do
    if file -b "$file" | grep -q 'Mach-O'; then
        codesign --force --sign - --timestamp=none "$file"
    fi
done < <(find "$BACKEND" -type f -print0)
codesign --force --sign - --timestamp=none "$APP"
codesign --verify --strict "$APP"

rm -rf "$WORK"
echo "==> Done: $APP ($(du -sh "$APP" | cut -f1))"
echo "    Open it from Finder, or: open \"$APP\""
