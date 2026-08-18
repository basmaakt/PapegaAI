#!/usr/bin/env bash
# Bouwt een zelfstandige PapegaAI voor Linux.
#
# Werkt zowel op Linux als op Windows (Git Bash): `dotnet publish -r linux-x64`
# cross-compileert prima. Het resultaat in dist-linux/ is een map die je in zijn
# geheel naar de doelmachine kopieert; daar draai je install.sh.
#
#   ./build-linux.sh              # x64, zelfstandig (geen .NET nodig op de pc)
#   ./build-linux.sh linux-arm64  # bijvoorbeeld voor een Raspberry Pi / ARM-laptop
#   ./build-linux.sh linux-x64 --no-gpu   # zonder Vulkan-runtime (~60 MB kleiner)

set -euo pipefail

cd "$(dirname "$0")"

RID="${1:-linux-x64}"
OUT="dist-${RID}"
shift || true

echo "→ publish ${RID} → ${OUT}"
dotnet publish -c Release -r "${RID}" --self-contained -o "${OUT}" --nologo

# Whisper.net levert de native bibliotheken voor élk platform mee, ongeacht de
# RID waarvoor je publiceert. Op de doelmachine is daar niets van bruikbaar
# behalve de eigen architectuur, dus dat scheelt zo'n 70 MB.
echo "→ overtollige runtimes opruimen"
if [ -d "${OUT}/runtimes" ]; then
    find "${OUT}/runtimes" -mindepth 1 -maxdepth 1 -type d \
        ! -name "${RID}" ! -name vulkan -exec rm -rf {} +
    if [ -d "${OUT}/runtimes/vulkan" ]; then
        find "${OUT}/runtimes/vulkan" -mindepth 1 -maxdepth 1 -type d \
            ! -name "${RID}" -exec rm -rf {} +
    fi
fi
rm -f "${OUT}/ggml-metal.metal"

# Zonder GPU-runtime: alleen de CPU-bibliotheken overhouden.
if [ "${1:-}" = "--no-gpu" ]; then
    echo "→ Vulkan-runtime weglaten"
    rm -rf "${OUT}/runtimes/vulkan"
fi

# Windows-bestandssystemen kennen geen uitvoerbaar-bit; zet het hier zodat een
# build vanaf Windows meteen bruikbaar is na het uitpakken.
chmod +x "${OUT}/papegaai" 2>/dev/null || true
cp -f install.sh "${OUT}/install.sh"
chmod +x "${OUT}/install.sh" 2>/dev/null || true

TARBALL="papegaai-${RID}.tar.gz"
tar czf "${TARBALL}" -C "$(dirname "${OUT}")" "$(basename "${OUT}")"

echo
echo "✓ klaar: ${OUT} ($(du -sh "${OUT}" | cut -f1))"
echo "         ${TARBALL} ($(du -sh "${TARBALL}" | cut -f1))"
echo
echo "  Kopieer het tar.gz-bestand naar de Linux-machine en daar:"
echo "      tar xzf ${TARBALL} && cd ${OUT} && bash install.sh"
echo
echo "  (bash install.sh, niet ./install.sh: een build vanaf Windows verliest het"
echo "   uitvoerbaar-bit. install.sh zet het daarna zelf goed voor het programma.)"
