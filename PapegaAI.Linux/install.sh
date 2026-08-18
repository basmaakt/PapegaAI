#!/usr/bin/env bash
# Installeert PapegaAI voor de huidige gebruiker. Geen root nodig, behalve voor
# de twee toestemmingen die Linux nu eenmaal alleen als root geeft (de groep
# 'input' en /dev/uinput) — daar wordt je expliciet naar gevraagd.
#
#   ./install.sh              installeren
#   ./install.sh --uninstall  weer verwijderen (laat modellen/instellingen staan)

set -euo pipefail

SOURCE_DIR="$(cd "$(dirname "$0")" && pwd)"
PREFIX="${HOME}/.local"
APP_DIR="${PREFIX}/lib/papegaai"
BIN_LINK="${PREFIX}/bin/papegaai"
DESKTOP_FILE="${PREFIX}/share/applications/papegaai.desktop"
ICON_DIR="${PREFIX}/share/icons/hicolor"
UDEV_RULE="/etc/udev/rules.d/99-papegaai-uinput.rules"

say()  { printf '\n\033[1m%s\033[0m\n' "$*"; }
ok()   { printf '  \033[32m✓\033[0m %s\n' "$*"; }
warn() { printf '  \033[33m!\033[0m %s\n' "$*"; }
have() { command -v "$1" >/dev/null 2>&1; }

ask() {
    local answer=""
    read -r -p "  $1 [j/N] " answer || true
    [[ "${answer}" =~ ^[jJyY] ]]
}

uninstall() {
    say "PapegaAI verwijderen"
    pkill -f "${APP_DIR}/papegaai" 2>/dev/null || true
    rm -rf "${APP_DIR}"
    rm -f "${BIN_LINK}" "${DESKTOP_FILE}" "${HOME}/.config/autostart/papegaai.desktop"
    find "${ICON_DIR}" -name 'papegaai.png' -delete 2>/dev/null || true
    ok "programma, menu-item, autostart en iconen verwijderd"
    echo
    echo "  Modellen, instellingen en geschiedenis staan nog in:"
    echo "    ~/.local/share/PapegaAI   en   ~/.config/PapegaAI"
    echo "  Verwijder die zelf als je ze niet meer nodig hebt."
    exit 0
}

if [ "${1:-}" = "--uninstall" ]; then
    uninstall
fi

if [ ! -f "${SOURCE_DIR}/papegaai" ]; then
    echo "papegaai niet gevonden in ${SOURCE_DIR} — draai dit script vanuit de uitgepakte map." >&2
    exit 1
fi

# ---------------------------------------------------------------- programma --
say "PapegaAI installeren in ${APP_DIR}"
pkill -f "${APP_DIR}/papegaai" 2>/dev/null || true
mkdir -p "${APP_DIR}" "${PREFIX}/bin" "${PREFIX}/share/applications"

# Bij een upgrade eerst opruimen: oude native bibliotheken die blijven staan
# kunnen naast de nieuwe geladen worden.
if have rsync; then
    rsync -a --delete --exclude install.sh "${SOURCE_DIR}/" "${APP_DIR}/"
else
    rm -rf "${APP_DIR:?}/"*
    cp -r "${SOURCE_DIR}/." "${APP_DIR}/"
    rm -f "${APP_DIR}/install.sh"
fi
chmod +x "${APP_DIR}/papegaai"
ln -sf "${APP_DIR}/papegaai" "${BIN_LINK}"
ok "programma geïnstalleerd"

if "${APP_DIR}/papegaai" export-icons "${ICON_DIR}" >/dev/null 2>&1; then
    ok "iconen geïnstalleerd"
else
    warn "iconen konden niet worden uitgepakt"
fi

cat > "${DESKTOP_FILE}" <<DESKTOP
[Desktop Entry]
Type=Application
Name=PapegaAI
Comment=Dicteren met een druk op de knop
Exec=${APP_DIR}/papegaai run
Icon=papegaai
Terminal=false
Categories=Utility;AudioVideo;
DESKTOP
ok "menu-item aangemaakt"

have update-desktop-database && update-desktop-database "${PREFIX}/share/applications" 2>/dev/null || true
have gtk-update-icon-cache && gtk-update-icon-cache -f -t "${ICON_DIR}" 2>/dev/null || true

if [[ ":${PATH}:" != *":${PREFIX}/bin:"* ]]; then
    warn "${PREFIX}/bin staat niet in je PATH — voeg dit toe aan ~/.bashrc of ~/.zshrc:"
    echo "        export PATH=\"\${HOME}/.local/bin:\${PATH}\""
fi

# ------------------------------------------------------------ hulpprogramma --
say "Benodigde hulpprogramma's"

# Per pakket: welk commando het levert, en hoe het heet op de grote distro's.
# PapegaAI werkt zonder deze ook, maar dan alleen via het klembord.
apt_pkgs=(); dnf_pkgs=(); pacman_pkgs=(); missing=()

need() {   # need <commando> <apt> <dnf> <pacman>
    have "$1" && return 0
    missing+=("$1")
    apt_pkgs+=("$2"); dnf_pkgs+=("$3"); pacman_pkgs+=("$4")
}

if [ -n "${WAYLAND_DISPLAY:-}" ] || [ "${XDG_SESSION_TYPE:-}" = "wayland" ]; then
    ok "sessie: Wayland"
    need wl-copy wl-clipboard wl-clipboard wl-clipboard
else
    ok "sessie: X11"
    need xdotool xdotool xdotool xdotool
fi
need notify-send libnotify-bin libnotify libnotify

if [ ${#missing[@]} -eq 0 ]; then
    ok "alle hulpprogramma's aanwezig"
else
    warn "nog te installeren: ${missing[*]}"
    if   have apt;    then echo "        sudo apt install ${apt_pkgs[*]}"
    elif have dnf;    then echo "        sudo dnf install ${dnf_pkgs[*]}"
    elif have pacman; then echo "        sudo pacman -S ${pacman_pkgs[*]}"
    fi
fi

# ---------------------------------------------------------------- rechten ----
say "Toestemmingen"

if id -nG "${USER}" | grep -qw input; then
    ok "je zit in de groep 'input'"
    in_input_group=1
else
    in_input_group=0
    echo "  PapegaAI leest de push-to-talk-toets uit /dev/input — de enige manier die"
    echo "  ook onder Wayland werkt. Daarvoor moet je in de groep 'input' zitten."
    if ask "Nu toevoegen met sudo?"; then
        if sudo usermod -aG input "${USER}"; then
            ok "toegevoegd — log daarna één keer uit en weer in"
        else
            warn "toevoegen mislukt"
        fi
    else
        warn "overgeslagen — op X11 werkt PapegaAI ook zonder (via de RECORD-extensie)"
    fi
fi

if [ -w /dev/uinput ]; then
    ok "/dev/uinput is beschrijfbaar"
else
    echo "  Op Wayland (zeker op GNOME) plakt PapegaAI de tekst via een virtueel"
    echo "  toetsenbord op /dev/uinput. Zonder toegang blijft alleen het klembord over."
    if ask "Regel nu toegang tot /dev/uinput met sudo?"; then
        sudo modprobe uinput || true
        echo uinput | sudo tee /etc/modules-load.d/uinput.conf >/dev/null
        echo 'KERNEL=="uinput", GROUP="input", MODE="0660", OPTIONS+="static_node=uinput"' \
            | sudo tee "${UDEV_RULE}" >/dev/null
        sudo udevadm control --reload-rules || true
        sudo udevadm trigger || true
        ok "udev-regel geplaatst (${UDEV_RULE})"
    else
        warn "overgeslagen"
    fi
fi

# ------------------------------------------------------------------ klaar ----
say "Klaar"
echo "  Volgende stappen:"
echo "    papegaai setup     model downloaden en alles controleren"
echo "    papegaai doctor    controleren wat er nog mist"
echo "    papegaai           starten (tray-icoon, geen venster)"
echo "    papegaai install --launch-at-login    automatisch starten bij inloggen"
echo
if [ "${in_input_group}" = "0" ]; then
    echo "  Let op: log eerst uit en weer in, anders geldt de groep 'input' nog niet."
fi
