# SPDX-License-Identifier: CC-BY-NC-SA-4.0
"""
waybar.py — exposes stramp as a native MPRIS2 D-Bus player so waybar's
mpris module picks it up automatically alongside Spotify, Firefox, etc.
"""
from __future__ import annotations

import logging

log = logging.getLogger(__name__)

try:
    import dbus
    import dbus.service
    import dbus.mainloop.glib
    from gi.repository import GLib
    DBUS_OK = True
except ImportError:
    DBUS_OK = False
    log.warning("dbus-python not found — MPRIS2 integration disabled.")

_MPRIS_BUS_NAME    = "org.mpris.MediaPlayer2.stramp"
_MPRIS_OBJECT_PATH = "/org/mpris/MediaPlayer2"
_MPRIS_IFACE       = "org.mpris.MediaPlayer2"
_PLAYER_IFACE      = "org.mpris.MediaPlayer2.Player"
_PROPS_IFACE       = "org.freedesktop.DBus.Properties"

_instance: "MprisService | None" = None


class MprisService(dbus.service.Object):
    """
    Minimal MPRIS2 implementation.
    Only the properties and signals waybar actually reads are implemented.
    """

    def __init__(self, on_toggle, on_next, on_prev, on_volume):
        if not DBUS_OK:
            return

        dbus.mainloop.glib.DBusGMainLoop(set_as_default=True)
        bus = dbus.SessionBus()
        self._bus_name = dbus.service.BusName(_MPRIS_BUS_NAME, bus)
        super().__init__(bus, _MPRIS_OBJECT_PATH)

        self._on_toggle = on_toggle
        self._on_next   = on_next
        self._on_prev   = on_prev
        self._on_volume = on_volume

        self._song:   dict  = {}
        self._paused: bool  = False
        self._volume: float = 1.0

    # ── org.mpris.MediaPlayer2 ────────────────────────────────────────────

    @dbus.service.method(_MPRIS_IFACE)
    def Raise(self):
        pass

    @dbus.service.method(_MPRIS_IFACE)
    def Quit(self):
        pass

    # ── org.mpris.MediaPlayer2.Player ─────────────────────────────────────

    @dbus.service.method(_PLAYER_IFACE)
    def PlayPause(self):
        GLib.idle_add(self._on_toggle)

    @dbus.service.method(_PLAYER_IFACE)
    def Next(self):
        GLib.idle_add(self._on_next)

    @dbus.service.method(_PLAYER_IFACE)
    def Previous(self):
        GLib.idle_add(self._on_prev)

    @dbus.service.method(_PLAYER_IFACE)
    def Play(self):
        if self._paused:
            GLib.idle_add(self._on_toggle)

    @dbus.service.method(_PLAYER_IFACE)
    def Pause(self):
        if not self._paused:
            GLib.idle_add(self._on_toggle)

    @dbus.service.method(_PLAYER_IFACE)
    def Stop(self):
        pass

    # ── org.freedesktop.DBus.Properties ──────────────────────────────────

    @dbus.service.method(_PROPS_IFACE, in_signature="ss", out_signature="v")
    def Get(self, interface, prop):
        return self._all_props().get(interface, {}).get(prop)

    @dbus.service.method(_PROPS_IFACE, in_signature="s", out_signature="a{sv}")
    def GetAll(self, interface):
        return self._all_props().get(interface, {})

    @dbus.service.method(_PROPS_IFACE, in_signature="ssv")
    def Set(self, interface, prop, value):
        if interface == _PLAYER_IFACE and prop == "Volume":
            self._volume = max(0.0, min(1.0, float(value)))
            GLib.idle_add(self._on_volume, self._volume * 100.0)
            self.PropertiesChanged(
                _PLAYER_IFACE,
                {"Volume": dbus.Double(self._volume)},
                [],
            )

    @dbus.service.signal(_PROPS_IFACE, signature="sa{sv}as")
    def PropertiesChanged(self, interface, changed, invalidated):
        pass

    # ── Internal ──────────────────────────────────────────────────────────

    def _all_props(self) -> dict:
        status = "Paused" if self._paused else "Playing"
        metadata = dbus.Dictionary({
            "mpris:trackid": dbus.ObjectPath(
                "/org/stramp/track/" + str(abs(hash(self._song.get("path", ""))))
            ),
            "xesam:title":  self._song.get("track",  ""),
            "xesam:artist": dbus.Array([self._song.get("artist", "")], signature="s"),
            "xesam:album":  self._song.get("album",  ""),
        }, signature="sv")

        return {
            _MPRIS_IFACE: {
                "CanQuit":             dbus.Boolean(False),
                "CanRaise":            dbus.Boolean(False),
                "HasTrackList":        dbus.Boolean(False),
                "Identity":            dbus.String("Stramp"),
                "SupportedUriSchemes": dbus.Array([], signature="s"),
                "SupportedMimeTypes":  dbus.Array([], signature="s"),
            },
            _PLAYER_IFACE: {
                "PlaybackStatus": dbus.String(status),
                "LoopStatus":     dbus.String("None"),
                "Rate":           dbus.Double(1.0),
                "Shuffle":        dbus.Boolean(False),
                "Metadata":       metadata,
                "Volume":         dbus.Double(self._volume),
                "Position":       dbus.Int64(0),
                "MinimumRate":    dbus.Double(1.0),
                "MaximumRate":    dbus.Double(1.0),
                "CanGoNext":      dbus.Boolean(True),
                "CanGoPrevious":  dbus.Boolean(True),
                "CanPlay":        dbus.Boolean(True),
                "CanPause":       dbus.Boolean(True),
                "CanSeek":        dbus.Boolean(False),
                "CanControl":     dbus.Boolean(True),
            },
        }

    def update(self, song: dict, paused: bool = False, volume: float = 100.0):
        self._song   = song
        self._paused = paused
        self._volume = volume / 100.0
        self.PropertiesChanged(
            _PLAYER_IFACE,
            {
                "PlaybackStatus": dbus.String("Paused" if paused else "Playing"),
                "Metadata":       self._all_props()[_PLAYER_IFACE]["Metadata"],
                "Volume":         dbus.Double(self._volume),
            },
            [],
        )

    def stop(self):
        self._song   = {}
        self._paused = True
        self.PropertiesChanged(
            _PLAYER_IFACE,
            {"PlaybackStatus": dbus.String("Stopped")},
            [],
        )


# ── Public API ────────────────────────────────────────────────────────────────

def start(on_toggle, on_next, on_prev, on_volume):
    global _instance
    if not DBUS_OK:
        return
    try:
        _instance = MprisService(on_toggle, on_next, on_prev, on_volume)
        log.debug("MPRIS2 service registered as %s", _MPRIS_BUS_NAME)
    except Exception:
        log.warning("Could not register MPRIS2 service", exc_info=True)


def write(song: dict, paused: bool = False, volume: float = 100.0):
    """Update the now-playing metadata."""
    if _instance:
        _instance.update(song, paused, volume)


def clear():
    """Signal stopped state."""
    if _instance:
        _instance.stop()


def start_socket_listener(on_toggle, on_next, on_prev, on_volume=None):
    start(on_toggle, on_next, on_prev, on_volume or (lambda v: None))