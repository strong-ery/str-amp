# SPDX-License-Identifier: CC-BY-NC-SA-4.0
"""ui/sidebar.py — "Up Next" queue panel."""

from __future__ import annotations

from gi.repository import Gtk

from stramp.library import art_async


def _apply_art(stack, img, pb):
    """Module-level helper to avoid lambda capture bugs with art callbacks."""
    if pb:
        img.set_from_pixbuf(pb)
        stack.set_visible_child_name("art")
    else:
        stack.set_visible_child_name("placeholder")


class QueueSidebar(Gtk.Box):
    """
    A vertical panel showing the next N tracks in the queue.
    Call refresh(queue, queue_pos, jump_callback) to redraw.
    """

    QUEUE_SHOW = 40

    def __init__(self):
        super().__init__(orientation=Gtk.Orientation.VERTICAL)
        self.add_css_class("sidebar")
        self.set_size_request(280, -1)

        hdr = Gtk.Label(label="UP NEXT")
        hdr.add_css_class("section-header")
        hdr.set_xalign(0)
        self.append(hdr)

        scroll = Gtk.ScrolledWindow()
        scroll.set_vexpand(True)
        scroll.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        self.append(scroll)

        self._box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=2)
        self._box.set_margin_start(8)
        self._box.set_margin_end(8)
        self._box.set_margin_bottom(8)
        scroll.set_child(self._box)

    def refresh(self, queue: list[dict], queue_pos: int, jump_callback):
        """Rebuild the visible queue rows."""
        child = self._box.get_first_child()
        while child:
            nxt = child.get_next_sibling()
            self._box.remove(child)
            child = nxt

        shown = queue[queue_pos : queue_pos + self.QUEUE_SHOW]
        for i, song in enumerate(shown):
            playing = (i == 0)
            row = self._make_row(song, playing, queue_pos + i, jump_callback)
            self._box.append(row)

    def _make_row(self, song: dict, playing: bool, idx: int, jump_callback) -> Gtk.Box:
        row = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=10)
        row.add_css_class("queue-row")
        if playing:
            row.add_css_class("playing")
        row.set_margin_top(2)
        row.set_margin_bottom(2)

        # Thumbnail
        stk = Gtk.Stack()
        stk.set_transition_type(Gtk.StackTransitionType.CROSSFADE)
        stk.set_transition_duration(180)
        img = Gtk.Image()
        img.set_pixel_size(48)
        img.add_css_class("art-frame")
        stk.add_named(img, "art")
        ph = Gtk.Label(label="♫")
        ph.add_css_class("queue-art-placeholder")
        ph.set_size_request(48, 48)
        ph.set_xalign(0.5)
        ph.set_yalign(0.5)
        stk.add_named(ph, "placeholder")
        stk.set_visible_child_name("placeholder")
        row.append(stk)

        art_async(song["path"], 48, lambda pb, s=stk, im=img: _apply_art(s, im, pb))

        # Text
        tb = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=2)
        tb.set_hexpand(True)
        tb.set_valign(Gtk.Align.CENTER)
        tl = Gtk.Label(label=song["track"])
        tl.add_css_class("queue-title")
        if playing:
            tl.add_css_class("playing")
        tl.set_ellipsize(3)
        tl.set_xalign(0)
        al = Gtk.Label(label=song["artist"])
        al.add_css_class("queue-artist")
        al.set_ellipsize(3)
        al.set_xalign(0)
        tb.append(tl)
        tb.append(al)
        row.append(tb)

        ck = Gtk.GestureClick()
        ck.connect("released", lambda g, n, x, y, pos=idx: jump_callback(pos))
        row.add_controller(ck)

        return row
