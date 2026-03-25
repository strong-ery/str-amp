# SPDX-License-Identifier: CC-BY-NC-SA-4.0
"""
library.py — file scanning, metadata helpers, and the SongItem GObject type.
"""

from __future__ import annotations

import logging
import threading
from pathlib import Path

from gi.repository import GLib, GdkPixbuf, GObject

log = logging.getLogger(__name__)

try:
    from mutagen.id3 import ID3
    from mutagen.mp3 import MP3
    MUTAGEN_OK = True
except ImportError:
    MUTAGEN_OK = False
    log.warning("mutagen not found — album art and duration will be unavailable.")


# ── Metadata ──────────────────────────────────────────────────────────────────

def load_art_pixbuf(path: str, size: int) -> GdkPixbuf.Pixbuf | None:
    """Return a square pixbuf of *size* pixels for the given file, or None."""
    if not MUTAGEN_OK:
        return None
    try:
        tags = ID3(path)
        for key in tags.keys():
            if key.startswith("APIC"):
                data = tags[key].data
                loader = GdkPixbuf.PixbufLoader()
                loader.write(data)
                loader.close()
                pb = loader.get_pixbuf()
                if pb:
                    w, h = pb.get_width(), pb.get_height()
                    side = min(w, h)
                    pb = pb.new_subpixbuf(
                        (w - side) // 2, (h - side) // 2, side, side
                    )
                    return pb.scale_simple(size, size, GdkPixbuf.InterpType.BILINEAR)
    except Exception:
        log.debug("Could not load art for %s", path, exc_info=True)
    return None


def get_duration(path: str) -> float:
    """Return track duration in seconds, or 0.0 on failure."""
    if not MUTAGEN_OK:
        return 0.0
    try:
        return MP3(path).info.length
    except Exception:
        log.debug("Could not read duration for %s", path, exc_info=True)
        return 0.0


def fmt_time(seconds: float) -> str:
    s = int(seconds)
    m, s = divmod(s, 60)
    return f"{m}:{s:02d}"


def _song_from_path(f: Path) -> dict:
    """
    Build a song dict from a file path.
    Tries ID3 tags first; falls back to 'Artist - Title' filename convention.
    """
    title = artist = None
    if MUTAGEN_OK:
        try:
            tags = ID3(str(f))
            title  = str(tags["TIT2"]) if "TIT2" in tags else None
            artist = str(tags["TPE1"]) if "TPE1" in tags else None
        except Exception:
            pass

    if not title or not artist:
        name = f.stem
        if " - " in name:
            parts = name.split(" - ", 1)
            artist = artist or parts[0].strip()
            title  = title  or parts[1].strip()
        else:
            artist = artist or "Unknown"
            title  = title  or name

    return {"path": str(f), "track": title, "artist": artist}


def scan_library(music_dir: str) -> list[dict]:
    """Recursively find all supported audio files under *music_dir*."""
    extensions = ("*.mp3", "*.flac", "*.ogg", "*.opus", "*.m4a", "*.wav")
    songs = []
    for ext in extensions:
        for f in Path(music_dir).rglob(ext):
            songs.append(_song_from_path(f))
    return sorted(songs, key=lambda s: (s["artist"].lower(), s["track"].lower()))


# ── Art cache ─────────────────────────────────────────────────────────────────

class ArtCache:
    """Thread-safe LRU-ish pixbuf cache backed by a deque."""

    def __init__(self, maxsize: int = 120):
        from collections import deque
        self._d: dict = {}
        self._order: deque = deque()
        self._lock = threading.Lock()
        self._max = maxsize

    def get(self, path: str, size: int):
        """Return cached pixbuf, None (miss cached), or the sentinel 'miss'."""
        with self._lock:
            return self._d.get((path, size), "miss")

    def put(self, path: str, size: int, pb):
        key = (path, size)
        with self._lock:
            self._d[key] = pb
            self._order.append(key)
            if len(self._order) > self._max:
                self._d.pop(self._order.popleft(), None)


# Module-level shared cache
ART = ArtCache()


def art_async(path: str, size: int, callback):
    """Fetch art on a worker thread, deliver pixbuf to *callback* on the main thread."""
    def _work():
        cached = ART.get(path, size)
        if cached == "miss":
            pb = load_art_pixbuf(path, size)
            ART.put(path, size, pb)
        else:
            pb = cached
        GLib.idle_add(callback, pb)

    threading.Thread(target=_work, daemon=True).start()


# ── GObject item for Gtk.ListView ─────────────────────────────────────────────

class SongItem(GObject.Object):
    __gtype_name__ = "SongItem"

    def __init__(self, song: dict):
        super().__init__()
        self.song = song
