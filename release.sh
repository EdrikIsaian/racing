#!/bin/bash
# Builds standalone downloads (no .NET install needed) into release/:
#   RacingGame-mac-arm64.zip   Apple Silicon Macs (M1/M2/M3/M4)
#   RacingGame-mac-intel.zip   Intel Macs
#   RacingGame-windows.zip     Windows 10/11
# Usage: ./release.sh
set -euo pipefail
cd "$(dirname "$0")"

APP="Racing Game"
OUT="release"
PROJ="RacingGame/RacingGame.csproj"
rm -rf "$OUT"
mkdir -p "$OUT/build"

publish() { # runtime-id, folder
    echo "==> Building $1"
    dotnet publish "$PROJ" -c Release -r "$1" --self-contained \
        -p:PublishSingleFile=true -p:DebugType=none -o "$OUT/build/$2" --nologo -v q
}

mac_app() { # runtime-id, zip name
    publish "$1" "$1"
    local app="$OUT/build/$1-app/$APP.app"
    mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
    cp -R "$OUT/build/$1/." "$app/Contents/MacOS/"
    cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>$APP</string>
    <key>CFBundleDisplayName</key><string>$APP</string>
    <key>CFBundleIdentifier</key><string>com.racinggame.app</string>
    <key>CFBundleExecutable</key><string>RacingGame</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>1.0</string>
    <key>CFBundleVersion</key><string>1</string>
    <key>LSMinimumSystemVersion</key><string>11.0</string>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST
    # Ad-hoc signature so Apple Silicon will run it (not notarized)
    codesign --force --deep -s - "$app" >/dev/null 2>&1
    ditto -c -k --keepParent "$app" "$OUT/$2"
}

mac_app osx-arm64 RacingGame-mac-arm64.zip
mac_app osx-x64   RacingGame-mac-intel.zip

publish win-x64 win-x64
(cd "$OUT/build" && mv win-x64 RacingGame && zip -qr "../RacingGame-windows.zip" RacingGame)

rm -rf "$OUT/build"
echo
echo "Done:"
ls -lh "$OUT"/*.zip
