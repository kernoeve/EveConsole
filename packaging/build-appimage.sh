#!/usr/bin/env bash
#
# Builds the Linux tarball and AppImage locally, without cutting a release.
#
# The same recipe the release workflow uses, minus the two steps that talk to GitHub — so what
# comes out is what a release would produce, and testing it tests the real thing rather than an
# approximation of it.
#
#   ./packaging/build-appimage.sh              # version from the csproj
#   ./packaging/build-appimage.sh 0.9.13       # or say it explicitly
#
# Output lands in ./Releases. Run the .AppImage with chmod +x and go.

set -euo pipefail

cd "$(dirname "$0")/.."

# ── Version ───────────────────────────────────────────────────────────────────
#
# ⚠️ Defaults to the csproj's, and that default matters more than it looks. The version a client
# reports is compared against the one recorded in the database, and a client NEWER than the
# database is refused while another client holds the background lease — it is only allowed to
# start when nobody does, because that is the upgrade path. So a test build numbered above your
# other clients will either refuse to start or, if it does get the lease, stamp its number into the
# database and lock every other client out until they are upgraded too.
#
# Build test copies at the same version as everything else unless you mean otherwise.
CSPROJ_VERSION=$(sed -n 's|.*<Version>\([^<]*\)</Version>.*|\1|p' EveConsole.csproj | head -1)
VERSION="${1:-$CSPROJ_VERSION}"

if [ "$VERSION" != "$CSPROJ_VERSION" ]; then
  echo "!! Building $VERSION, but the csproj says $CSPROJ_VERSION."
  echo "!! A client newer than the database is refused while another client holds the lease."
  echo
fi

echo "== EVE Console $VERSION — linux-x64"
echo

# ── Prerequisites ─────────────────────────────────────────────────────────────

command -v dotnet >/dev/null || { echo "dotnet is not on PATH."; exit 1; }

if ! command -v vpk >/dev/null && [ ! -x "$HOME/.dotnet/tools/vpk" ]; then
  echo "== Installing the Velopack CLI (matching the release workflow)"
  dotnet tool install -g vpk --version 1.2.0
fi
export PATH="$PATH:$HOME/.dotnet/tools"

# libvlc is the one thing the app cannot carry: LibVLCSharp loads the system copy. The build does
# not need it, but a copy that runs does — said here rather than discovered as silent alarms.
if ! ldconfig -p 2>/dev/null | grep -q 'libvlc\.so'; then
  echo "!! libvlc was not found. The build will succeed and alarm sounds will not play."
  echo "!! Arch: pacman -S vlc     Debian/Ubuntu: apt install libvlc-dev"
  echo
fi

# ── Publish ───────────────────────────────────────────────────────────────────

echo "== Publishing"
rm -rf publish-linux
dotnet publish EveConsole.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:Version="$VERSION" \
  -o publish-linux

chmod +x publish-linux/EveConsole

# ── Tarball ───────────────────────────────────────────────────────────────────

echo "== Building the tarball"
tar -czf "EveConsole-linux-x64.tar.gz" -C publish-linux .

# ── AppImage ──────────────────────────────────────────────────────────────────
#
# ⚠️ No `vpk download github` here, deliberately. The release workflow pulls the previous release
# first so it can build a delta against it; doing that locally would produce a package that only
# makes sense next to a release this build is not part of. Without it, vpk builds a full one.
echo "== Packing the AppImage"
vpk pack \
  --packId EveConsole \
  --packTitle "EVE Console" \
  --packVersion "$VERSION" \
  --packDir publish-linux \
  --mainExe EveConsole

echo
echo "== Done"
ls -lh Releases/*.AppImage 2>/dev/null || echo "   (no .AppImage in ./Releases — check the vpk output above)"
echo "   EveConsole-linux-x64.tar.gz"
echo
echo "   chmod +x Releases/*.AppImage && ./Releases/*.AppImage"
