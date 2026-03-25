# SPDX-License-Identifier: CC-BY-NC-SA-4.0
"""ui/window.py — the main application window."""

from __future__ import annotations

import random

import gi
gi.require_version("Gtk", "4.0")
gi.require_version("Adw", "1")
from gi.repository import Gtk, Adw, GLib, Gio

from stramp import __version__
from stramp.library import (
    scan_library, art_async, get_duration, fmt_time, SongItem
)
from stramp.player import Player
from stramp.ui.sidebar import QueueSidebar
from stramp.ui.styles import APP_CSS
import stramp.waybar as waybar


class StrampWindow(Adw.ApplicationWindow):
    QUEUE_SHOW = 40

    def __init__(self, app: Adw.Application, music_dir: str, waybar_mode: bool):
        super().__init__(application=app)
        self.music_dir   = music_dir
        self.waybar_mode = waybar_mode

        self.set_title("Stramp")
        self.set_default_size(920, 640)
        self.set_resizable(True)

        # Apply CSS
        css = Gtk.CssProvider()
        css.load_from_string(APP_CSS)
        Gtk.StyleContext.add_provider_for_display(
            self.get_display(), css, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION
        )

        self.library = scan_library(music_dir)
        if not self.library:
            self._show_empty()
            return

        self.queue:     list[dict] = []
        self.queue_pos: int        = 0
        self.shuffled:  bool       = True
        self.duration:  float      = 0.0
        self.seeking:   bool       = False

        self.player = Player(
            on_time_pos=self._on_time_pos,
            on_eof=lambda: GLib.idle_add(self._on_next),
        )

        self._build_queue()
        self._build_ui()
        self._load_current()

        if self.waybar_mode:
            waybar.start_socket_listener(
                on_toggle=self._on_play_pause,
                on_next=self._on_next,
                on_prev=self._on_prev,
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

        # App title
        title_lbl = Gtk.Label(label="STRAMP")
        title_lbl.add_css_class("app-title")
        title_lbl.set_xalign(0)
        left.append(title_lbl)

        sub_lbl = Gtk.Label(label="STRONG'S MUSIC PLAYER")
        sub_lbl.add_css_class("app-subtitle")
        sub_lbl.set_xalign(0)
        left.append(sub_lbl)

        # Album art
        art_box = Gtk.Box()
        art_box.set_halign(Gtk.Align.CENTER)
        left.append(art_box)

        self.art_stack = Gtk.Stack()
        self.art_stack.set_transition_type(Gtk.StackTransitionType.CROSSFADE)
        self.art_stack.set_transition_duration(250)
        art_box.append(self.art_stack)

        self.art_image = Gtk.Image()
        self.art_image.set_pixel_size(220)
        self.art_image.add_css_class("art-frame")
        self.art_stack.add_named(self.art_image, "art")

        ph = Gtk.Label(label="♫")
        ph.add_css_class("art-placeholder")
        ph.set_size_request(220, 220)
        ph.set_xalign(0.5); ph.set_yalign(0.5)
        self.art_stack.add_named(ph, "placeholder")

        # Track info
        info = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=2)
        info.set_margin_top(14)
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
        if not self.queue:
            return
        song = self.queue[self.queue_pos]
        GLib.idle_add(self.title_lbl.set_text,  song["track"])
        GLib.idle_add(self.artist_lbl.set_text, song["artist"])
        art_async(song["path"], 220, lambda pb: GLib.idle_add(self._set_main_art, pb))

        self.duration = get_duration(song["path"])
        GLib.idle_add(self.time_total.set_text, fmt_time(self.duration))
        GLib.idle_add(self.progress.set_range, 0, max(1, self.duration))

        self.player.play(song["path"])
        GLib.idle_add(self.play_btn.set_icon_name, "media-playback-pause-symbolic")

        if self.waybar_mode:
            waybar.write(song)

        GLib.idle_add(self.sidebar.refresh, self.queue, self.queue_pos, self._jump_to)
        GLib.idle_add(self._lib_view.queue_draw)

    def _set_main_art(self, pb):
        if pb:
            self.art_image.set_from_pixbuf(pb)
            self.art_stack.set_visible_child_name("art")
        else:
            self.art_stack.set_visible_child_name("placeholder")

    # ── MPV time-pos callback ─────────────────────────────────────────────

    def _on_time_pos(self, value: float):
        if not self.seeking:
            GLib.idle_add(self._update_progress, value)

    def _update_progress(self, pos: float):
        self.progress.set_value(pos)
        self.time_elapsed.set_text(fmt_time(pos))

    # ── Scrubbing ─────────────────────────────────────────────────────────

    def _on_scale_change_value(self, _scale, _scroll_type, value):
        self.seeking = True
        self.time_elapsed.set_text(fmt_time(max(0.0, value)))
        return False

    def _on_drag_begin(self, _gesture, _x, _y):
        self.seeking = True

    def _on_drag_end(self, _gesture, _dx, _dy):
        val = self.progress.get_value()
        self.player.seek(val)
        GLib.timeout_add(120, self._clear_seeking)

    def _clear_seeking(self):
        self.seeking = False
        return False

    # ── Transport ─────────────────────────────────────────────────────────

    def _on_play_pause(self, _btn=None):
        now_playing = self.player.toggle_pause()
        icon = "media-playback-pause-symbolic" if now_playing else "media-playback-start-symbolic"
        self.play_btn.set_icon_name(icon)
        if self.waybar_mode:
            waybar.write(self.queue[self.queue_pos], paused=not now_playing)

    def _on_next(self, _btn=None):
        if self.queue_pos < len(self.queue) - 1:
            self.queue_pos += 1
        else:
            self._build_queue()
        self._load_current()

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
        GLib.idle_add(self.sidebar.refresh, self.queue, self.queue_pos, self._jump_to)

    def _jump_to(self, pos: int):
        self.queue_pos = pos
        self._load_current()

    # ── Cleanup ───────────────────────────────────────────────────────────

    def do_close_request(self):
        self.player.quit()
        if self.waybar_mode:
            waybar.clear()
        return False
