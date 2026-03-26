# SPDX-License-Identifier: CC-BY-NC-SA-4.0
"""
player.py — thin wrapper around python-mpv that owns playback state.
"""
from __future__ import annotations
import logging
from pathlib import Path
log = logging.getLogger(__name__)
try:
    import mpv
except ImportError:
    print("ERROR: python-mpv not found.  pip install python-mpv --break-system-packages")
    raise


def _ensure_mpris_plugin():
    """Symlink mpv-mpris into the user scripts dir on first launch if available."""
    src = Path("/usr/lib/mpv/mpris.so")
    dst = Path.home() / ".config/mpv/scripts/mpris.so"
    if src.exists() and not dst.exists():
        dst.parent.mkdir(parents=True, exist_ok=True)
        try:
            dst.symlink_to(src)
            log.debug("Symlinked mpris plugin: %s -> %s", src, dst)
        except Exception:
            log.debug("Could not symlink mpris plugin", exc_info=True)


class Player:
    """
    Wraps an mpv.MPV instance and exposes simple play/pause/seek helpers.
    Callbacks
    ---------
    on_time_pos(value: float)  — called on every time-position tick
    """

    def __init__(self, on_time_pos=None, on_eof=None):
        _ensure_mpris_plugin()
        self._mpv = mpv.MPV(
            video=False,
            terminal=False,
            quiet=True,
        )
        self._on_time_pos_cb = on_time_pos
        self._on_eof_cb      = on_eof
        self.is_playing      = False

        self._mpv.observe_property("time-pos", self._time_pos_handler)
        self._mpv.register_event_callback(self._event_handler)

    def _event_handler(self, event):
        if event.get("event_id") == mpv.MpvEventID.END_FILE:
            reason = event.get("event", {}).get("reason", "")
            if reason == "eof" and self._on_eof_cb:
                self._on_eof_cb()

    # ── Internal MPV handlers ─────────────────────────────────────────────

    def _time_pos_handler(self, _name, value):
        if value is not None and self._on_time_pos_cb:
            self._on_time_pos_cb(float(value))

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

    def set_volume(self, volume: float):
        """Set playback volume 0–100."""
        try:
            self._mpv.volume = max(0.0, min(100.0, volume))
        except Exception:
            log.debug("Volume set to %.2f failed", volume, exc_info=True)

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