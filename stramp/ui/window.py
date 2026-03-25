# SPDX-License-Identifier: CC-BY-NC-SA-4.0
"""ui/window.py — the main application window."""

from __future__ import annotations

import csv as _csv
import importlib.util as _importlib_util
import json
import os
import random
import re
import subprocess
import sys
import tempfile
import threading
from pathlib import Path

import gi
gi.require_version("Gtk", "4.0")
gi.require_version("Adw", "1")
from gi.repository import Gtk, Adw, GLib, Gio

from stramp import __version__
from stramp.library import (
    scan_library, art_async, get_duration, fmt_time, SongItem,
    ART, strip_art,
)
from stramp.player import Player
from stramp.ui.sidebar import QueueSidebar
from stramp.ui.styles import APP_CSS
import stramp.waybar as waybar

import logging
log = logging.getLogger(__name__)

_DOWNLOAD_SCRIPT = Path(__file__).resolve().parent.parent.parent / "tools" / "download_music.py"

_REMOVED_JSON = Path.home() / ".local" / "share" / "stramp" / "removed.json"


def _load_removed() -> list[dict]:
    try:
        return json.loads(_REMOVED_JSON.read_text())
    except Exception:
        return []


def _save_removed(entries: list[dict]):
    _REMOVED_JSON.parent.mkdir(parents=True, exist_ok=True)
    _REMOVED_JSON.write_text(json.dumps(entries, indent=2))


class StrampWindow(Adw.ApplicationWindow):
    QUEUE_SHOW = 40

    def __init__(self, app: Adw.Application, music_dir: str, waybar_mode: bool):
        super().__init__(application=app)
        self.music_dir   = music_dir
        self.waybar_mode = waybar_mode

        self.set_title("Stramp")
        self.set_default_size(920, 640)
        self.set_resizable(True)

        css = Gtk.CssProvider()
        css.load_from_string(APP_CSS)
        Gtk.StyleContext.add_provider_for_display(
            self.get_display(), css, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION
        )

        self.library = scan_library(music_dir)
        if not self.library:
            self._show_empty()
            return

        self.queue:      list[dict] = []
        self.queue_pos:  int        = 0
        self.shuffled:   bool       = True
        self.duration:   float      = 0.0
        self.seeking:    bool       = False
        self._dragging:  bool       = False
        self._advancing: bool       = False

        self.player = Player(
            on_time_pos=self._on_time_pos,
        )

        self._build_queue()
        self._build_ui()
        self._load_current()

        if self.waybar_mode:
            waybar.start_socket_listener(
                on_toggle=self._on_play_pause,
                on_next=self._on_next,
                on_prev=self._on_prev,
                on_volume=self._on_waybar_volume,
            )

    # ── Empty state ───────────────────────────────────────────────────────

    def _show_empty(self):
        d = Adw.MessageDialog(transient_for=self, modal=True)
        d.set_heading("No audio files found")
        d.set_body(f"No playable files found in:\n{self.music_dir}")
        d.add_response("ok", "OK")
        d.connect("response", lambda *_: self.close())
        d.present()

    # ── Queue management ──────────────────────────────────────────────────

    def _build_queue(self):
        self.queue = list(self.library)
        if self.shuffled:
            random.shuffle(self.queue)
        self.queue_pos = 0

    # ── UI construction ───────────────────────────────────────────────────

    def _build_ui(self):
        root = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL)
        root.add_css_class("player-window")
        self.set_content(root)

        root.append(self._build_left())

        self.sidebar = QueueSidebar()
        root.append(self.sidebar)

    def _build_left(self) -> Gtk.Box:
        left = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
        left.set_hexpand(True)
        left.set_margin_top(28);  left.set_margin_bottom(20)
        left.set_margin_start(32); left.set_margin_end(24)

        # ── Header row: app title + volume + three-dots menu ─────────────
        header_row = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL)
        header_row.add_css_class("header-row")

        titles_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
        titles_box.set_hexpand(True)

        title_lbl = Gtk.Label(label="STRAMP")
        title_lbl.add_css_class("app-title")
        title_lbl.set_xalign(0)
        titles_box.append(title_lbl)

        sub_lbl = Gtk.Label(label="STRONG'S MUSIC PLAYER")
        sub_lbl.add_css_class("app-subtitle")
        sub_lbl.set_xalign(0)
        titles_box.append(sub_lbl)

        header_row.append(titles_box)
        header_row.append(self._build_volume())
        header_row.append(self._build_menu_button())
        left.append(header_row)

        # ── Album art with fix-art overlay button ─────────────────────────
        art_box = Gtk.Box()
        art_box.set_halign(Gtk.Align.CENTER)
        left.append(art_box)

        self.art_stack = Gtk.Stack()
        self.art_stack.set_transition_type(Gtk.StackTransitionType.CROSSFADE)
        self.art_stack.set_transition_duration(250)

        self.art_image = Gtk.Image()
        self.art_image.set_pixel_size(220)
        self.art_image.add_css_class("art-frame")
        self.art_stack.add_named(self.art_image, "art")

        ph = Gtk.Label(label="♫")
        ph.add_css_class("art-placeholder")
        ph.set_size_request(220, 220)
        ph.set_xalign(0.5); ph.set_yalign(0.5)
        self.art_stack.add_named(ph, "placeholder")

        # Wrap stack in an Overlay so the fix-art button floats over it
        art_overlay = Gtk.Overlay()
        art_overlay.set_child(self.art_stack)

        self._fix_art_btn = Gtk.Button()
        self._fix_art_btn.set_icon_name("document-edit-symbolic")
        self._fix_art_btn.add_css_class("fix-art-btn")
        self._fix_art_btn.set_halign(Gtk.Align.END)
        self._fix_art_btn.set_valign(Gtk.Align.END)
        self._fix_art_btn.set_tooltip_text("Fix album cover…")
        self._fix_art_btn.connect("clicked", self._on_fix_art)
        art_overlay.add_overlay(self._fix_art_btn)

        art_box.append(art_overlay)

        # Track info
        info = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=2)
        info.set_margin_top(8)
        left.append(info)

        self.title_lbl = Gtk.Label(label="")
        self.title_lbl.add_css_class("track-title")
        self.title_lbl.set_ellipsize(3); self.title_lbl.set_xalign(0)
        info.append(self.title_lbl)

        self.artist_lbl = Gtk.Label(label="")
        self.artist_lbl.add_css_class("track-artist")
        self.artist_lbl.set_ellipsize(3); self.artist_lbl.set_xalign(0)
        info.append(self.artist_lbl)

        # Progress bar
        left.append(self._build_progress())

        # Transport controls
        left.append(self._build_transport())

        # Library section
        left.append(self._build_library())

        return left

    # ── Menu button ───────────────────────────────────────────────────────

    def _build_menu_button(self) -> Gtk.MenuButton:
        menu = Gio.Menu()
        menu.append("Remove Current Song…",        "app.remove_song")
        menu.append("Install Song by Name…",        "app.install_by_name")
        menu.append("Install from Exportify CSV…",  "app.install_from_csv")

        btn = Gtk.MenuButton()
        btn.set_icon_name("view-more-symbolic")
        btn.add_css_class("menu-btn")
        btn.set_valign(Gtk.Align.START)
        btn.set_menu_model(menu)
        btn.set_tooltip_text("More options")

        app = self.get_application()
        for name, cb in [
            ("remove_song",      self._on_remove_song),
            ("install_by_name",  self._on_install_by_name),
            ("install_from_csv", self._on_install_from_csv),
        ]:
            action = Gio.SimpleAction.new(name, None)
            action.connect("activate", cb)
            app.add_action(action)

        return btn

    # ── Volume slider ─────────────────────────────────────────────────────

    def _build_volume(self) -> Gtk.Box:
        vol_row = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=6)
        vol_row.add_css_class("vol-row")
        vol_row.set_valign(Gtk.Align.CENTER)
        vol_row.set_hexpand(False)
        vol_row.set_margin_start(16)
        vol_row.set_margin_end(8)

        self._vol_icon = Gtk.Image.new_from_icon_name("audio-volume-medium-symbolic")
        self._vol_icon.add_css_class("vol-icon")
        vol_row.append(self._vol_icon)

        self.vol_slider = Gtk.Scale.new_with_range(Gtk.Orientation.HORIZONTAL, 0, 100, 1)
        self.vol_slider.set_value(100)
        self.vol_slider.set_draw_value(False)
        self.vol_slider.set_hexpand(False)
        self.vol_slider.set_size_request(120, -1)
        self.vol_slider.add_css_class("vol-scale")
        self.vol_slider.connect("value-changed", self._on_volume_changed)
        vol_row.append(self.vol_slider)

        return vol_row

    def _on_volume_changed(self, slider):
        vol = slider.get_value()
        self.player.set_volume(vol)
        if vol == 0:
            icon = "audio-volume-muted-symbolic"
        elif vol < 35:
            icon = "audio-volume-low-symbolic"
        elif vol < 70:
            icon = "audio-volume-medium-symbolic"
        else:
            icon = "audio-volume-high-symbolic"
        self._vol_icon.set_from_icon_name(icon)

    # ── Progress bar ──────────────────────────────────────────────────────

    def _build_progress(self) -> Gtk.Box:
        prog_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=2)
        prog_box.set_margin_top(12)

        self.progress = Gtk.Scale.new_with_range(Gtk.Orientation.HORIZONTAL, 0, 1, 0.01)
        self.progress.add_css_class("progress-scale")
        self.progress.set_draw_value(False)
        self.progress.set_hexpand(True)
        self.progress.connect("change-value", self._on_scale_change_value)

        drag = Gtk.GestureDrag()
        drag.connect("drag-begin", self._on_drag_begin)
        drag.connect("drag-end",   self._on_drag_end)
        self.progress.add_controller(drag)

        prog_box.append(self.progress)

        time_row = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL)
        self.time_elapsed = Gtk.Label(label="0:00")
        self.time_elapsed.add_css_class("time-label")
        self.time_elapsed.set_xalign(0)
        self.time_total = Gtk.Label(label="0:00")
        self.time_total.add_css_class("time-label")
        self.time_total.set_hexpand(True)
        self.time_total.set_xalign(1)
        time_row.append(self.time_elapsed)
        time_row.append(self.time_total)
        prog_box.append(time_row)

        return prog_box

    # ── Transport controls ────────────────────────────────────────────────

    def _build_transport(self) -> Gtk.Box:
        ctrl = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=4)
        ctrl.set_halign(Gtk.Align.CENTER)
        ctrl.set_margin_top(12)

        self.shuffle_btn = self._btn(
            "media-playlist-shuffle-symbolic", self._on_shuffle, "transport-btn"
        )
        if self.shuffled:
            self.shuffle_btn.add_css_class("shuffle-active")

        self.play_btn = self._btn(
            "media-playback-start-symbolic", self._on_play_pause, "play-btn"
        )

        ctrl.append(self.shuffle_btn)
        ctrl.append(self._btn("media-skip-backward-symbolic", self._on_prev, "transport-btn"))
        ctrl.append(self.play_btn)
        ctrl.append(self._btn("media-skip-forward-symbolic",  self._on_next, "transport-btn"))
        ctrl.append(self._btn("view-refresh-symbolic", self._on_reshuffle,   "transport-btn"))

        return ctrl

    # ── Library section ───────────────────────────────────────────────────

    def _build_library(self) -> Gtk.Box:
        container = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)

        sep = Gtk.Separator(orientation=Gtk.Orientation.HORIZONTAL)
        sep.set_margin_top(14)
        container.append(sep)

        hdr = Gtk.Label(label="LIBRARY")
        hdr.add_css_class("section-header")
        hdr.set_xalign(0)
        container.append(hdr)

        self.lib_search = Gtk.SearchEntry()
        self.lib_search.add_css_class("lib-search")
        self.lib_search.set_placeholder_text("Search songs…")
        self.lib_search.connect("search-changed", self._on_lib_search)
        container.append(self.lib_search)

        lib_scroll = Gtk.ScrolledWindow()
        lib_scroll.set_vexpand(True)
        lib_scroll.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        lib_scroll.set_margin_top(2)
        container.append(lib_scroll)

        self._lib_store = Gio.ListStore(item_type=SongItem)
        self._populate_lib_store(self.library)

        factory = Gtk.SignalListItemFactory()
        factory.connect("setup",  self._lib_setup)
        factory.connect("bind",   self._lib_bind)
        factory.connect("unbind", self._lib_unbind)

        sel_model = Gtk.NoSelection(model=self._lib_store)
        self._lib_view = Gtk.ListView(model=sel_model, factory=factory)
        self._lib_view.set_single_click_activate(True)
        self._lib_view.connect("activate", self._on_lib_activate)
        lib_scroll.set_child(self._lib_view)

        return container

    def _btn(self, icon: str, cb, css: str) -> Gtk.Button:
        b = Gtk.Button()
        b.set_icon_name(icon)
        b.add_css_class(css)
        b.connect("clicked", cb)
        return b

    # ── Library ListView ──────────────────────────────────────────────────

    def _populate_lib_store(self, songs: list[dict]):
        self._lib_store.remove_all()
        for s in songs:
            self._lib_store.append(SongItem(s))

    def _lib_setup(self, _factory, item):
        row = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=1)
        row.add_css_class("lib-row")
        row.set_margin_top(1); row.set_margin_bottom(1)
        t = Gtk.Label(); t.add_css_class("lib-title"); t.set_ellipsize(3); t.set_xalign(0)
        a = Gtk.Label(); a.add_css_class("lib-artist"); a.set_ellipsize(3); a.set_xalign(0)
        row.append(t); row.append(a)
        row._title_lbl  = t
        row._artist_lbl = a
        item.set_child(row)

    def _lib_bind(self, _factory, item):
        row  = item.get_child()
        song = item.get_item().song
        row._title_lbl.set_text(song["track"])
        row._artist_lbl.set_text(song["artist"])
        playing = bool(self.queue) and self.queue[self.queue_pos]["path"] == song["path"]
        if playing:
            row.add_css_class("playing")
            row._title_lbl.add_css_class("playing")
        else:
            row.remove_css_class("playing")
            row._title_lbl.remove_css_class("playing")

    def _lib_unbind(self, _factory, item):
        pass

    def _on_lib_activate(self, _listview, position: int):
        song = self._lib_store.get_item(position).song
        rest = [s for s in self.library if s["path"] != song["path"]]
        if self.shuffled:
            random.shuffle(rest)
        self.queue     = [song] + rest
        self.queue_pos = 0
        self._load_current()

    def _on_lib_search(self, entry):
        q = entry.get_text().strip().lower()
        filtered = (
            self.library if not q
            else [s for s in self.library
                  if q in s["track"].lower() or q in s["artist"].lower()]
        )
        self._populate_lib_store(filtered)

    # ── Load & play ───────────────────────────────────────────────────────

    def _load_current(self):
        log.debug("_load_current called queue_pos=%d track=%s",
                self.queue_pos, self.queue[self.queue_pos].get("track"))
        if not self.queue:
            return
        self._advancing = False
        song = self.queue[self.queue_pos]
        GLib.idle_add(self.title_lbl.set_text,  song["track"])
        GLib.idle_add(self.artist_lbl.set_text, song["artist"])
        art_async(song["path"], 220, self._set_main_art)

        self.duration = get_duration(song["path"])
        log.debug("_load_current duration=%.2f for %s", self.duration, song["track"])
        GLib.idle_add(self.time_total.set_text, fmt_time(self.duration))
        GLib.idle_add(self.progress.set_range, 0, max(1, self.duration))

        self.player.play(song["path"])
        log.debug("_load_current player.play() called for %s", song["path"])
        GLib.idle_add(self.play_btn.set_icon_name, "media-playback-pause-symbolic")

        if self.waybar_mode:
            waybar.write(song, volume=self.vol_slider.get_value())

        GLib.idle_add(
            self.sidebar.refresh,
            self.queue, self.queue_pos, self._jump_to, self._remove_from_queue,
        )
        GLib.idle_add(self._lib_view.queue_draw)
        log.debug("_load_current done for %s", song["track"])

    def _set_main_art(self, pb):
        if pb:
            self.art_image.set_from_pixbuf(pb)
            self.art_stack.set_visible_child_name("art")
        else:
            self.art_image.clear()
            self.art_stack.set_visible_child_name("placeholder")

    # ── MPV time-pos callback ─────────────────────────────────────────────

    def _on_time_pos(self, value: float):
        log.debug("time_pos=%.2f duration=%.2f seeking=%s advancing=%s",
                value, self.duration, self.seeking, self._advancing)
        if not self.seeking:
            GLib.idle_add(self._update_progress, value)
        if self.duration > 0 and value >= self.duration - 0.33 and not self._advancing and not self.seeking and not self._dragging:
            log.debug(">>> AUTO-ADVANCE triggered at %.2f", value)
            self._advancing = True
            GLib.idle_add(self._on_next)

    def _update_progress(self, pos: float):
        self._dragging = True
        self.progress.set_value(pos)
        self._dragging = False
        self.time_elapsed.set_text(fmt_time(pos))

    # ── Scrubbing ─────────────────────────────────────────────────────────

    def _on_scale_change_value(self, _scale, _scroll_type, value):
        if not self._dragging:
            self.seeking    = True
            self._advancing = False
            self.player.seek(max(0.0, value))
            if not self.player.is_playing:
                self.player.resume()
                self.play_btn.set_icon_name("media-playback-pause-symbolic")
            GLib.timeout_add(120, self._clear_seeking)
        self.time_elapsed.set_text(fmt_time(max(0.0, value)))
        return False

    def _on_drag_begin(self, _gesture, _x, _y):
        self._dragging = True
        self.seeking   = True

    def _on_drag_end(self, _gesture, _dx, _dy):
        self._dragging = False
        self._advancing = False
        val = self.progress.get_value()
        log.debug("_on_drag_end val=%.2f duration=%.2f", val, self.duration)
        GLib.timeout_add(200, self._finish_drag_seek, val)

    def _finish_drag_seek(self, seeked_to: float):
        actual = max(self.progress.get_value(), self.player.time_pos)
        log.debug("_finish_drag_seek seeked_to=%.2f actual=%.2f duration=%.2f", seeked_to, actual, self.duration)
        self.seeking = False
        self._advancing = False
        if self.duration > 0 and actual >= self.duration - 2.0:
            log.debug(">>> DRAG-END ADVANCE triggered")
            self._advancing = True
            try:
                self.player.pause()
            except Exception:
                pass
            GLib.idle_add(self._on_next)
        else:
            self.player.seek(actual)
            if not self.player.is_playing:
                self.player.resume()
                self.play_btn.set_icon_name("media-playback-pause-symbolic")
        return False

    def _clear_seeking(self):
        self.seeking = False
        return False

    # ── Transport ─────────────────────────────────────────────────────────

    def _on_waybar_volume(self, vol: float):
        """Called when waybar scroll changes volume — keeps slider in sync."""
        self.vol_slider.set_value(vol)

    def _on_play_pause(self, _btn=None):
        now_playing = self.player.toggle_pause()
        icon = "media-playback-pause-symbolic" if now_playing else "media-playback-start-symbolic"
        self.play_btn.set_icon_name(icon)
        if self.waybar_mode:
            waybar.write(self.queue[self.queue_pos], paused=not now_playing, volume=self.vol_slider.get_value())

    def _on_next(self, _btn=None):
        log.debug("_on_next called queue_pos=%d queue_len=%d advancing=%s",
                self.queue_pos, len(self.queue), self._advancing)
        if self.queue_pos < len(self.queue) - 1:
            self.queue_pos += 1
        else:
            self._build_queue()
        self._load_current()
        return False

    def _on_prev(self, _btn=None):
        if self.player.time_pos > 3:
            self.player.seek(0)
        else:
            self.queue_pos = max(0, self.queue_pos - 1)
            self._load_current()

    def _on_shuffle(self, _btn=None):
        self.shuffled = not self.shuffled
        if self.shuffled:
            self.shuffle_btn.add_css_class("shuffle-active")
        else:
            self.shuffle_btn.remove_css_class("shuffle-active")

    def _on_reshuffle(self, _btn=None):
        current = self.queue[self.queue_pos]
        rest = [s for s in self.library if s != current]
        random.shuffle(rest)
        self.queue     = [current] + rest
        self.queue_pos = 0
        GLib.idle_add(
            self.sidebar.refresh,
            self.queue, self.queue_pos, self._jump_to, self._remove_from_queue,
        )

    def _jump_to(self, pos: int):
        self.queue_pos = pos
        self._load_current()

    # ── Queue: remove by position (sidebar right-click) ───────────────────

    def _remove_from_queue(self, queue_idx: int):
        """
        Remove the song at *queue_idx* from the live queue.
        Called after the sidebar slide-away animation completes.
        Returns False so GLib.timeout_add doesn't repeat the call.
        """
        if queue_idx < 0 or queue_idx >= len(self.queue):
            return False

        if queue_idx == self.queue_pos:
            # Removing the currently playing song — advance to next
            self.queue.pop(queue_idx)
            if not self.queue:
                self._build_queue()
            else:
                self.queue_pos = min(self.queue_pos, len(self.queue) - 1)
            self._load_current()
        else:
            self.queue.pop(queue_idx)
            # If removed song was before current, shift position back
            if queue_idx < self.queue_pos:
                self.queue_pos -= 1
            GLib.idle_add(
                self.sidebar.refresh,
                self.queue, self.queue_pos, self._jump_to, self._remove_from_queue,
            )

        return False  # don't repeat if invoked via GLib.timeout_add

    # ══════════════════════════════════════════════════════════════════════
    # Menu actions
    # ══════════════════════════════════════════════════════════════════════

    # ── 1. Remove current song ────────────────────────────────────────────

    def _on_remove_song(self, _action=None, _param=None):
        if not self.queue:
            return
        song = self.queue[self.queue_pos]

        d = Adw.MessageDialog(transient_for=self, modal=True)
        d.set_heading("Remove song permanently?")
        d.set_body(
            f"\u201c{song['track']}\u201d by {song['artist']} will be deleted from disk "
            f"and won\u2019t be re-imported.\n\nThis cannot be undone."
        )
        d.add_response("cancel", "Cancel")
        d.add_response("remove", "Remove")
        d.set_response_appearance("remove", Adw.ResponseAppearance.DESTRUCTIVE)
        d.set_default_response("cancel")
        d.set_close_response("cancel")
        d.connect("response", self._on_remove_confirmed, song)
        d.present()

    def _on_remove_confirmed(self, _dialog, response: str, song: dict):
        if response != "remove":
            return

        # Delete the file from disk
        path = Path(song["path"])
        try:
            path.unlink(missing_ok=True)
            log.debug("Deleted %s", path)
        except Exception as e:
            log.warning("Could not delete %s: %s", path, e)

        # Persist to removed log so scan_library can filter it in the future
        removed = _load_removed()
        removed.append({
            "track":  song["track"],
            "artist": song["artist"],
            "path":   str(path),
        })
        _save_removed(removed)

        # Drop from in-memory library and queue
        self.library = [s for s in self.library if s["path"] != song["path"]]
        self.queue   = [s for s in self.queue   if s["path"] != song["path"]]

        if not self.library:
            self._show_empty()
            return

        # Clamp position and keep playing
        self.queue_pos = min(self.queue_pos, len(self.queue) - 1)
        if not self.queue:
            self._build_queue()
        self._populate_lib_store(self.library)
        self._load_current()

    # ── 2. Fix album cover ────────────────────────────────────────────────

    def _load_dl_module(self):
        """
        Lazily load tools/download_music.py as a module so we can call its
        art-fetching functions (fetch_itunes_art, fetch_yt_thumbnail, etc.)
        without a full subprocess round-trip.
        Returns the module, or None if the script is missing/broken.
        """
        if not _DOWNLOAD_SCRIPT.exists():
            return None
        try:
            spec = _importlib_util.spec_from_file_location("stramp_dl", _DOWNLOAD_SCRIPT)
            mod  = _importlib_util.module_from_spec(spec)
            spec.loader.exec_module(mod)
            return mod
        except Exception as e:
            log.warning("Could not load download_music module: %s", e)
            return None

    def _on_fix_art(self, _btn=None):
        """
        Show the "Fix Album Cover" dialog offering two choices:
          1. Search for a new cover  (iTunes API → YouTube thumbnail fallback)
          2. Remove the cover entirely
        """
        if not self.queue:
            return
        song = self.queue[self.queue_pos]

        dlg = Adw.Window(transient_for=self, modal=True)
        dlg.set_title("Fix Album Cover")
        dlg.set_default_size(360, -1)

        outer = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
        dlg.set_content(outer)

        hb = Adw.HeaderBar()
        hb.set_show_end_title_buttons(False)
        cancel_btn = Gtk.Button(label="Cancel")
        cancel_btn.connect("clicked", lambda *_: dlg.close())
        hb.pack_start(cancel_btn)
        outer.append(hb)

        # Stack with three pages: choice → working → done
        stack = Gtk.Stack()
        stack.set_transition_type(Gtk.StackTransitionType.CROSSFADE)
        stack.set_transition_duration(150)
        outer.append(stack)

        # ── Page 1: choice ────────────────────────────────────────────────
        choice_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10)
        choice_box.set_margin_top(20); choice_box.set_margin_bottom(24)
        choice_box.set_margin_start(24); choice_box.set_margin_end(24)

        desc = Gtk.Label()
        desc.set_markup(
            f'The cover art for \u201c{song["track"]}\u201d looks wrong?\n'
            f'Choose what to do:'
        )
        desc.set_wrap(True)
        desc.add_css_class("dim-label")
        desc.set_xalign(0)
        choice_box.append(desc)

        search_btn = Gtk.Button(label="Search for New Cover")
        search_btn.add_css_class("suggested-action")
        search_btn.add_css_class("pill")
        choice_box.append(search_btn)

        rm_cover_btn = Gtk.Button(label="Remove Cover")
        rm_cover_btn.add_css_class("destructive-action")
        rm_cover_btn.add_css_class("pill")
        choice_box.append(rm_cover_btn)

        stack.add_named(choice_box, "choice")

        # ── Page 2: working ───────────────────────────────────────────────
        work_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10)
        work_box.set_halign(Gtk.Align.CENTER)
        work_box.set_margin_top(36); work_box.set_margin_bottom(36)

        spinner = Gtk.Spinner()
        spinner.set_size_request(36, 36)
        work_box.append(spinner)

        work_lbl = Gtk.Label(label="Searching…")
        work_lbl.add_css_class("dim-label")
        work_box.append(work_lbl)

        stack.add_named(work_box, "working")

        # ── Page 3: done ──────────────────────────────────────────────────
        done_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10)
        done_box.set_margin_top(20); done_box.set_margin_bottom(24)
        done_box.set_margin_start(24); done_box.set_margin_end(24)

        done_lbl = Gtk.Label(label="")
        done_lbl.set_wrap(True)
        done_lbl.set_xalign(0)
        done_box.append(done_lbl)

        close_btn = Gtk.Button(label="Close")
        close_btn.add_css_class("pill")
        close_btn.set_halign(Gtk.Align.CENTER)
        close_btn.connect("clicked", lambda *_: dlg.close())
        done_box.append(close_btn)

        stack.add_named(done_box, "done")
        stack.set_visible_child_name("choice")

        # ── "Search" button handler ───────────────────────────────────────
        def _on_search(_btn):
            cancel_btn.set_sensitive(False)
            stack.set_visible_child_name("working")
            spinner.start()

            def _work():
                dm = self._load_dl_module()
                if not dm:
                    GLib.idle_add(_finish, False, "\u2718 Download script not found.")
                    return
                try:
                    GLib.idle_add(work_lbl.set_text, "Searching iTunes\u2026")
                    itunes_url = dm.fetch_itunes_art(song)
                    if itunes_url:
                        GLib.idle_add(work_lbl.set_text, "Downloading cover\u2026")
                        img_data = dm.download_image(itunes_url)
                        if img_data and dm.embed_art(song["path"], img_data):
                            GLib.idle_add(_finish, True, "\u2714 New cover applied from iTunes.")
                            return

                    # iTunes miss — try YouTube thumbnail
                    GLib.idle_add(work_lbl.set_text, "Searching YouTube\u2026")
                    candidates = dm.search_candidates(song)
                    if candidates:
                        scored = sorted(
                            [(dm.score_result(song, c), c) for c in candidates],
                            key=lambda x: x[0][0],
                            reverse=True,
                        )
                        best = scored[0][1]
                        yt_url = dm.fetch_yt_thumbnail(best)
                        if yt_url:
                            GLib.idle_add(work_lbl.set_text, "Downloading cover\u2026")
                            img_data = dm.download_image(yt_url)
                            if img_data:
                                mime = "image/webp" if yt_url.endswith(".webp") else "image/jpeg"
                                if dm.embed_art(song["path"], img_data, mime=mime):
                                    GLib.idle_add(
                                        _finish, True,
                                        "\u2714 Cover applied from YouTube thumbnail."
                                    )
                                    return

                    GLib.idle_add(_finish, False, "\u2718 Couldn\u2019t find a suitable cover.")
                except Exception as e:
                    GLib.idle_add(_finish, False, f"\u2718 Error: {e}")

            def _finish(success: bool, msg: str):
                spinner.stop()
                colour = "#57c27c" if success else "#e05c5c"
                done_lbl.set_markup(f'<span foreground="{colour}">{msg}</span>')
                stack.set_visible_child_name("done")
                ART.invalidate(song["path"])
                # Re-sync mpv to its current position after mutagen rewrote the file
                if success:
                    current_pos = self.player.time_pos
                    self.player.seek(current_pos)
                art_async(song["path"], 220, self._set_main_art)

            threading.Thread(target=_work, daemon=True).start()

        # ── "Remove cover" button handler ─────────────────────────────────
        def _on_remove_cover(_btn):
            strip_art(song["path"])
            ART.invalidate(song["path"])
            current_pos = self.player.time_pos
            self.player.seek(current_pos)
            GLib.idle_add(self._set_main_art, None)
            dlg.close()

        search_btn.connect("clicked", _on_search)
        rm_cover_btn.connect("clicked", _on_remove_cover)

        dlg.present()

    # ── 3. Install song by Artist + Title ─────────────────────────────────

    def _on_install_by_name(self, _action=None, _param=None):
        dlg = Adw.Window(transient_for=self, modal=True)
        dlg.set_title("Install Song")
        dlg.set_default_size(420, 260)

        outer = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
        dlg.set_content(outer)

        hb = Adw.HeaderBar()
        hb.set_show_end_title_buttons(False)
        cancel_btn = Gtk.Button(label="Cancel")
        cancel_btn.connect("clicked", lambda *_: dlg.close())
        hb.pack_start(cancel_btn)
        outer.append(hb)

        content = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=14)
        content.set_margin_top(20)
        content.set_margin_bottom(20)
        content.set_margin_start(24)
        content.set_margin_end(24)
        outer.append(content)

        # ── Entry rows ────────────────────────────────────────────────────
        group = Adw.PreferencesGroup()
        content.append(group)

        artist_row = Adw.EntryRow(title="Artist")
        track_row  = Adw.EntryRow(title="Title")
        url_row    = Adw.EntryRow(title="YouTube URL (optional — skips search)")
        group.add(artist_row)
        group.add(track_row)
        group.add(url_row)

        # ── Progress area (hidden until download starts) ───────────────────
        prog_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6)
        prog_box.set_visible(False)
        content.append(prog_box)

        status_lbl = Gtk.Label(label="")
        status_lbl.add_css_class("dim-label")
        status_lbl.set_xalign(0)
        status_lbl.set_ellipsize(3)
        prog_box.append(status_lbl)

        progress_bar = Gtk.ProgressBar()
        progress_bar.add_css_class("install-progress")
        progress_bar.set_pulse_step(0.08)
        prog_box.append(progress_bar)

        result_lbl = Gtk.Label(label="")
        result_lbl.set_xalign(0)
        result_lbl.set_visible(False)
        prog_box.append(result_lbl)

        # ── Download button ───────────────────────────────────────────────
        dl_btn = Gtk.Button(label="Download")
        dl_btn.add_css_class("suggested-action")
        dl_btn.add_css_class("pill")
        dl_btn.set_halign(Gtk.Align.CENTER)
        content.append(dl_btn)

        # Keep a handle so we can cancel the pulse timeout later
        _state = {"pulse_id": None}

        def _start_pulse():
            progress_bar.pulse()
            return True  # keep repeating

        def on_download(_btn):
            artist = artist_row.get_text().strip()
            track  = track_row.get_text().strip()
            url    = url_row.get_text().strip()
            if not artist or not track:
                return

            dl_btn.set_sensitive(False)
            group.set_sensitive(False)
            cancel_btn.set_sensitive(False)

            prog_box.set_visible(True)
            result_lbl.set_visible(False)
            progress_bar.set_fraction(0)

            if url:
                status_lbl.set_text(f"Downloading from URL\u2026")
            else:
                status_lbl.set_text(f"Searching for \u201c{track}\u201d\u2026")

            _state["pulse_id"] = GLib.timeout_add(80, _start_pulse)

            tmp = tempfile.NamedTemporaryFile(
                mode="w", suffix=".csv", delete=False, encoding="utf-8"
            )
            writer = _csv.DictWriter(tmp, fieldnames=[
                "Track Name", "Artist Name(s)", "Album Name",
                "Release Date", "Popularity", "Duration (ms)",
            ])
            writer.writeheader()
            writer.writerow({
                "Track Name": track, "Artist Name(s)": artist,
                "Album Name": "", "Release Date": "",
                "Popularity": 50, "Duration (ms)": 0,
            })
            tmp.close()

            cmd = [sys.executable, str(_DOWNLOAD_SCRIPT),
                "--csv", tmp.name,
                "--out", self.music_dir,
                "--workers", "1"]
            if url:
                cmd += ["--url", url]

            self._run_download_with_progress(
                cmd=cmd,
                status_lbl=status_lbl,
                progress_bar=progress_bar,
                pulse_state=_state,
                on_finish=lambda ok: GLib.idle_add(
                    self._on_install_by_name_finished,
                    ok, dlg, tmp.name,
                    result_lbl, progress_bar, _state,
                ),
            )

        dl_btn.connect("clicked", on_download)
        for row in (artist_row, track_row, url_row):
            row.connect("entry-activated", lambda *_: dl_btn.emit("clicked"))

        dlg.present()

    # ── Finish handler for the single-song dialog ─────────────────────────

    def _on_install_by_name_finished(
        self,
        success: bool,
        dlg: Adw.Window,
        tmp_path: str | None,
        result_lbl: Gtk.Label,
        progress_bar: Gtk.ProgressBar,
        pulse_state: dict,
    ):
        # Stop pulsing
        if pulse_state.get("pulse_id"):
            GLib.source_remove(pulse_state["pulse_id"])
            pulse_state["pulse_id"] = None

        if tmp_path:
            try:
                os.unlink(tmp_path)
            except Exception:
                pass

        self.library = scan_library(self.music_dir)
        self._populate_lib_store(self.library)

        if success:
            progress_bar.set_fraction(1.0)
            result_lbl.set_markup(
                '<span foreground="#57c27c">\u2714\u2002Song added to library</span>'
            )
        else:
            progress_bar.set_fraction(0)
            result_lbl.set_markup(
                '<span foreground="#e05c5c">\u2718\u2002Couldn\u2019t find a good match</span>'
            )

        result_lbl.set_visible(True)
        dlg.set_title("Done" if success else "Not found")

    # ── Progress-aware download helper (single-song) ──────────────────────

    def _run_download_with_progress(
        self,
        cmd: list[str],
        status_lbl: Gtk.Label,
        progress_bar: Gtk.ProgressBar,
        pulse_state: dict,
        on_finish,
    ):
        if not _DOWNLOAD_SCRIPT.exists():
            GLib.idle_add(
                status_lbl.set_text,
                f"ERROR: download script not found at {_DOWNLOAD_SCRIPT}",
            )
            on_finish(False)
            return

        def _worker():
            try:
                proc = subprocess.Popen(
                    cmd,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.STDOUT,
                    text=True,
                    bufsize=1,
                )
                ok = True
                for raw in proc.stdout:
                    line = raw.rstrip()
                    if not line:
                        continue

                    if line.startswith("[") and "]" in line:
                        body = line.split("] ", 1)[-1].strip()
                        GLib.idle_add(
                            status_lbl.set_text,
                            f"Searching for \u201c{body}\u201d\u2026"
                        )

                    elif line.strip().startswith("\u2192"):
                        title = line.strip().lstrip("\u2192").strip()
                        title = re.sub(r"\s*\(score=-?\d+\)\s*$", "", title)
                        GLib.idle_add(status_lbl.set_text, f"Downloading: {title}")

                    elif "cover art" in line.lower():
                        GLib.idle_add(status_lbl.set_text, "Embedding cover art\u2026")

                    elif "UNCERTAIN" in line or (
                        "\u2717" in line and "download" in line.lower()
                    ):
                        ok = False
                        GLib.idle_add(status_lbl.set_text, "No confident match found.")

                    elif line.lower().startswith("done."):
                        GLib.idle_add(
                            status_lbl.set_text,
                            line.split("\u2192")[0].strip()
                        )

                proc.wait()
                on_finish(proc.returncode == 0 and ok)

            except Exception as e:
                GLib.idle_add(status_lbl.set_text, f"Fatal error: {e}")
                on_finish(False)

        threading.Thread(target=_worker, daemon=True).start()

    # ── 4. Install from Exportify CSV ─────────────────────────────────────

    def _on_install_from_csv(self, _action=None, _param=None):
        chooser = Gtk.FileChooserDialog(
            title="Choose Exportify CSV",
            transient_for=self,
            modal=True,
            action=Gtk.FileChooserAction.OPEN,
        )
        chooser.add_button("_Cancel",   Gtk.ResponseType.CANCEL)
        chooser.add_button("_Download", Gtk.ResponseType.ACCEPT)
        chooser.get_widget_for_response(
            Gtk.ResponseType.ACCEPT
        ).add_css_class("suggested-action")

        f = Gtk.FileFilter()
        f.set_name("CSV files")
        f.add_mime_type("text/csv")
        f.add_pattern("*.csv")
        chooser.add_filter(f)

        chooser.connect("response", self._on_csv_chosen)
        chooser.present()

    def _on_csv_chosen(self, chooser, response: int):
        if response != Gtk.ResponseType.ACCEPT:
            chooser.destroy()
            return

        csv_path = chooser.get_file().get_path()
        chooser.destroy()

        dlg = Adw.Window(transient_for=self, modal=True)
        dlg.set_title("Installing from CSV")
        dlg.set_default_size(520, 420)

        outer = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
        dlg.set_content(outer)

        hb = Adw.HeaderBar()
        hb.set_show_end_title_buttons(False)
        outer.append(hb)

        content = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        content.set_margin_top(12); content.set_margin_bottom(16)
        content.set_margin_start(16); content.set_margin_end(16)
        outer.append(content)

        info_lbl = Gtk.Label(label=f"Downloading from: {Path(csv_path).name}")
        info_lbl.add_css_class("dim-label")
        info_lbl.set_xalign(0)
        info_lbl.set_ellipsize(3)
        content.append(info_lbl)

        terminal_scroll = Gtk.ScrolledWindow()
        terminal_scroll.set_vexpand(True)
        terminal_scroll.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        terminal_scroll.add_css_class("terminal-scroll")
        terminal_buf = Gtk.TextBuffer()
        terminal_view = Gtk.TextView(buffer=terminal_buf)
        terminal_view.set_editable(False)
        terminal_view.set_cursor_visible(False)
        terminal_view.set_wrap_mode(Gtk.WrapMode.WORD_CHAR)
        terminal_view.add_css_class("terminal-view")
        terminal_scroll.set_child(terminal_view)
        content.append(terminal_scroll)

        dlg.present()

        self._run_download_subprocess(
            cmd=[sys.executable, str(_DOWNLOAD_SCRIPT),
                 "--csv", csv_path,
                 "--out", self.music_dir],
            terminal_buf=terminal_buf,
            terminal_scroll=terminal_scroll,
            on_finish=lambda ok: GLib.idle_add(
                self._on_install_finished, ok, dlg, None
            ),
        )

    # ── Shared download helper (CSV batch) ────────────────────────────────

    def _run_download_subprocess(
        self,
        cmd: list[str],
        terminal_buf: Gtk.TextBuffer,
        terminal_scroll: Gtk.ScrolledWindow,
        on_finish,
    ):
        if not _DOWNLOAD_SCRIPT.exists():
            GLib.idle_add(
                self._append_terminal, terminal_buf, terminal_scroll,
                f"ERROR: download script not found at:\n  {_DOWNLOAD_SCRIPT}\n"
            )
            on_finish(False)
            return

        def _worker():
            try:
                proc = subprocess.Popen(
                    cmd,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.STDOUT,
                    text=True,
                    bufsize=1,
                )
                for line in proc.stdout:
                    GLib.idle_add(
                        self._append_terminal, terminal_buf, terminal_scroll, line
                    )
                proc.wait()
                on_finish(proc.returncode == 0)
            except Exception as e:
                GLib.idle_add(
                    self._append_terminal, terminal_buf, terminal_scroll,
                    f"\nFATAL: {e}\n"
                )
                on_finish(False)

        threading.Thread(target=_worker, daemon=True).start()

    def _append_terminal(
        self,
        buf: Gtk.TextBuffer,
        scroll: Gtk.ScrolledWindow,
        text: str,
    ):
        buf.insert(buf.get_end_iter(), text)
        adj = scroll.get_vadjustment()
        adj.set_value(adj.get_upper() - adj.get_page_size())

    def _on_install_finished(self, success: bool, dlg: Adw.Window, tmp_path: str | None):
        if tmp_path:
            try:
                os.unlink(tmp_path)
            except Exception:
                pass
        self.library = scan_library(self.music_dir)
        self._populate_lib_store(self.library)
        dlg.set_title("Done \u2713" if success else "Finished with errors")

    # ── Cleanup ───────────────────────────────────────────────────────────

    def do_close_request(self):
        self.player.quit()
        if self.waybar_mode:
            waybar.clear()
        return False