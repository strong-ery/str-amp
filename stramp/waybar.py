# SPDX-License-Identifier: CC-BY-NC-SA-4.0
"""
waybar.py — writes JSON status to /tmp/stramp.json and listens on a Unix
socket so waybar (or any script) can send toggle/next/prev commands.
"""

from __future__ import annotations

import json
import logging
import socket
import threading
from pathlib import Path

from gi.repository import GLib

log = logging.getLogger(__name__)

WAYBAR_FILE   = Path("/tmp/stramp.json")
WAYBAR_SOCKET = Path("/tmp/stramp.sock")


def write(song: dict, paused: bool = False):
    """Write the current track info as waybar-compatible JSON."""
    icon = "⏸" if paused else "▶"
    data = {
        "text":    f"{icon}  {song['artist']} — {song['track']}",
        "tooltip": f"{song['track']}\n{song['artist']}\n\nClick to play/pause",
        "class":   "paused" if paused else "playing",
        "alt":     "paused" if paused else "playing",
    }
    try:
        WAYBAR_FILE.write_text(json.dumps(data))
    except Exception:
        log.warning("Could not write waybar file", exc_info=True)


def clear():
    """Write an empty/stopped state and remove the socket."""
    try:
        WAYBAR_FILE.write_text(json.dumps({"text": "", "class": "stopped"}))
    except Exception:
        log.debug("Could not clear waybar file", exc_info=True)
    try:
        if WAYBAR_SOCKET.exists():
            WAYBAR_SOCKET.unlink()
    except Exception:
        log.debug("Could not remove waybar socket", exc_info=True)


def start_socket_listener(on_toggle, on_next, on_prev):
    """
    Spawn a daemon thread that listens on WAYBAR_SOCKET for text commands.
    Commands: 'toggle', 'next', 'prev'
    Callbacks are delivered to the GTK main loop via GLib.idle_add.
    """
    def _listen():
        if WAYBAR_SOCKET.exists():
            WAYBAR_SOCKET.unlink()
        srv = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        srv.bind(str(WAYBAR_SOCKET))
        srv.listen(5)
        while True:
            try:
                conn, _ = srv.accept()
                msg = conn.recv(64).decode().strip()
                conn.close()
                if msg == "toggle":
                    GLib.idle_add(on_toggle)
                elif msg == "next":
                    GLib.idle_add(on_next)
                elif msg == "prev":
                    GLib.idle_add(on_prev)
            except Exception:
                log.debug("Socket listener error", exc_info=True)
                break

    threading.Thread(target=_listen, daemon=True).start()
