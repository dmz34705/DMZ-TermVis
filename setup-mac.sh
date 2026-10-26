#!/bin/sh
# One-time setup for DMZ TermVis on macOS.
# Downloads from the internet are quarantined, and Apple Silicon only runs signed binaries,
# so this clears the quarantine flag and applies a local (ad-hoc) signature.
cd "$(dirname "$0")" || exit 1
xattr -dr com.apple.quarantine . 2>/dev/null
chmod +x termvis
if codesign --force --sign - termvis; then
    echo "DMZ TermVis is ready. Start it with:  ./termvis"
else
    echo "Signing failed. Try: xcode-select --install   then run this script again."
    exit 1
fi
