# SPDX-License-Identifier: CC-BY-NC-SA-4.0
"""
player.py — thin wrapper around python-mpv that owns playback state.
"""

from __future__ import annotations

import logging

log = logging.getLogger(__name__)

try:
    import mpv
except ImportError:
    print("ERROR: python-mpv not found.  pip install python-mpv --break-system-packages")
    raise


class Player:
    """
    Wraps an mpv.MPV instance and exposes simple play/pause/seek helpers.

    Callbacks
    ---------
    on_time_pos(value: float)  — called on every time-position tick
    on_eof()                   — called when a track finishes naturally
    """

    def __init__(self, on_time_pos=None, on_eof=None):
        self._mpv = mpv.MPV(video=False, terminal=False, quiet=True)
        self._on_time_pos_cb = on_time_pos
        self._on_eof_cb      = on_eof
        self.is_playing      = False

        self._mpv.observe_property("time-pos",    self._time_pos_handler)
        self._mpv.observe_property("eof-reached", self._eof_handler)

    # ── Internal MPV handlers ─────────────────────────────────────────────

    def _time_pos_handler(self, _name, value):
        if value is not None and self._on_time_pos_cb:
            self._on_time_pos_cb(float(value))

    def _eof_handler(self, _name, value):
        if value and self._on_eof_cb:
            self._on_eof_cb()

    # ── Public API ────────────────────────────────────────────────────────

    def play(self, path: str):
        """Load and immediately start playing *path*."""
        self._mpv.play(path)
        self._mpv.pause = False
        self.is_playing = True

    def pause(self):
        self._mpv.pause = True
        self.is_playing = False

    def resume(self):
        self._mpv.pause = False
        self.is_playing = True

    def toggle_pause(self) -> bool:
        """Toggle play/pause. Returns True if now playing."""
        if self.is_playing:
            self.pause()
        else:
            self.resume()
        return self.is_playing

    def seek(self, position: float):
        """Seek to an absolute position in seconds."""
        try:
            self._mpv.seek(position, "absolute")
        except Exception:
            log.debug("Seek to %.2f failed", position, exc_info=True)

    @property
    def time_pos(self) -> float:
        try:
            return float(self._mpv.time_pos or 0.0)
        except Exception:
            return 0.0

    def quit(self):
        try:
            self._mpv.quit()
        except Exception:
            log.debug("MPV quit error", exc_info=True)
