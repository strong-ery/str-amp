# SPDX-License-Identifier: CC-BY-NC-SA-4.0
"""ui/styles.py — all application CSS in one place."""

APP_CSS = """
.player-window { background-color: @window_bg_color; }

/* ── App title ── */
.app-title {
    font-size: 22px;
    font-weight: 900;
    letter-spacing: 3px;
    color: @accent_color;
    margin-bottom: 1px;
}
.app-subtitle {
    font-size: 10px;
    letter-spacing: 1.5px;
    color: alpha(@window_fg_color, 0.3);
    margin-bottom: 14px;
}

/* ── Album art ── */
.art-frame    { border-radius: 18px; }
.art-placeholder {
    border-radius: 18px;
    background-color: alpha(@accent_color, 0.1);
    color: alpha(@accent_color, 0.4);
    font-size: 72px;
}

/* ── Fix-art overlay button (bottom-right corner of album art) ── */
.fix-art-btn {
    border-radius: 50%;
    padding: 5px;
    background-color: alpha(black, 0.48);
    border: none;
    color: white;
    min-width: 28px; min-height: 28px;
    -gtk-icon-size: 14px;
    margin: 7px;
}
.fix-art-btn:hover {
    background-color: alpha(black, 0.72);
}

/* ── Track info ── */
.track-title  { font-size: 17px; font-weight: bold; color: @window_fg_color; }
.track-artist { font-size: 13px; color: alpha(@window_fg_color, 0.6); }

/* ── Transport ── */
.transport-btn {
    border-radius: 50px; padding: 10px;
    background: none; border: none;
    color: @window_fg_color;
    min-width: 40px; min-height: 40px;
    -gtk-icon-size: 20px;
}
.transport-btn:hover { background-color: alpha(@accent_color, 0.15); color: @accent_color; }
.play-btn {
    border-radius: 50px; padding: 14px;
    background-color: @accent_color;
    color: white;
    border: none;
    min-width: 60px; min-height: 60px;
    -gtk-icon-size: 24px;
}
.play-btn image { color: white; }
.play-btn:hover { background-color: alpha(@accent_color, 0.82); }
.shuffle-active { color: @accent_color; }

/* ── Progress ── */
.progress-scale trough {
    background-color: alpha(@accent_color, 0.18);
    border-radius: 4px; min-height: 5px;
}
.progress-scale highlight { background-color: @accent_color; border-radius: 4px; }
.progress-scale slider {
    background-color: @accent_color; border-radius: 50%;
    min-width: 14px; min-height: 14px; margin: -5px;
    box-shadow: 0 1px 4px alpha(black, 0.35);
}
.time-label {
    font-size: 11px; font-family: monospace;
    color: alpha(@window_fg_color, 0.45);
}

/* ── Volume ── */
.vol-row { }
.vol-icon {
    color: alpha(@window_fg_color, 0.4);
    -gtk-icon-size: 16px;
    margin-right: 2px;
}
.vol-scale trough {
    background-color: alpha(@accent_color, 0.14);
    border-radius: 4px; min-height: 4px;
}
.vol-scale highlight {
    background-color: alpha(@accent_color, 0.65);
    border-radius: 4px;
}
.vol-scale slider {
    background-color: alpha(@window_fg_color, 0.75);
    border-radius: 50%;
    min-width: 11px; min-height: 11px; margin: -4px;
    box-shadow: 0 1px 3px alpha(black, 0.25);
}
.vol-scale:hover highlight { background-color: @accent_color; }
.vol-scale:hover slider    { background-color: @accent_color; }

/* ── Header / menu button ── */
.header-row { margin-bottom: 0; }
.menu-btn {
    border-radius: 50px; padding: 6px;
    background: none; border: none;
    color: alpha(@window_fg_color, 0.45);
    min-width: 28px; min-height: 28px;
    -gtk-icon-size: 16px;
}
.menu-btn:hover {
    background-color: alpha(@accent_color, 0.12);
    color: @accent_color;
}

/* ── Sidebar ── */
.sidebar {
    background-color: alpha(@window_bg_color, 0.55);
    border-left: 1px solid alpha(@window_fg_color, 0.07);
}
.section-header {
    font-size: 10px; font-weight: bold; letter-spacing: 2px;
    color: alpha(@window_fg_color, 0.35);
    padding: 14px 12px 8px 14px;
}

/* ── Queue rows ── */
.queue-row { border-radius: 10px; padding: 6px 8px; }
.queue-row:hover { background-color: alpha(@accent_color, 0.1); }
.queue-row.playing { background-color: alpha(@accent_color, 0.18); }
.queue-art-placeholder {
    border-radius: 6px;
    background-color: alpha(@accent_color, 0.1);
    color: alpha(@accent_color, 0.35);
    font-size: 16px;
}
.queue-title  { font-size: 13px; font-weight: 500; color: @window_fg_color; }
.queue-title.playing { color: @accent_color; font-weight: bold; }
.queue-artist { font-size: 11px; color: alpha(@window_fg_color, 0.5); }

/* ── Library list ── */
.lib-search {
    border-radius: 8px; margin: 6px 10px 4px 10px;
    font-size: 13px;
}
.lib-row { border-radius: 8px; padding: 5px 10px; }
.lib-row:hover { background-color: alpha(@accent_color, 0.1); }
.lib-row.playing { background-color: alpha(@accent_color, 0.16); }
.lib-title  { font-size: 13px; font-weight: 500; color: @window_fg_color; }
.lib-title.playing { color: @accent_color; }
.lib-artist { font-size: 11px; color: alpha(@window_fg_color, 0.5); }

/* ── Download terminal (used by CSV batch install) ── */
.terminal-scroll {
    border-radius: 8px;
    background-color: alpha(black, 0.35);
    border: 1px solid alpha(@window_fg_color, 0.08);
}
.terminal-view {
    font-family: monospace;
    font-size: 11px;
    color: alpha(@window_fg_color, 0.8);
    background: transparent;
    padding: 8px;
}
.terminal-view text { background: transparent; }

/* ── Install progress bar (single-song download dialog) ── */
.install-progress trough {
    background-color: alpha(@accent_color, 0.18);
    border-radius: 4px;
    min-height: 6px;
}
.install-progress progress {
    background-color: @accent_color;
    border-radius: 4px;
}

listview { background: transparent; }
listview > row { padding: 0; background: transparent; }
listview > row:selected { background: transparent; }
listview > row:hover { background: transparent; }
"""