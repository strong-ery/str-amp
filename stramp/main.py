#!/usr/bin/env python3
# SPDX-License-Identifier: CC-BY-NC-SA-4.0
"""
main.py — CLI entry point for stramp.

Usage:
    stramp [directory] [--waybar]

If no directory is given, ~/Music is used.
"""

from __future__ import annotations

import logging
import os
import sys
from pathlib import Path

import gi
gi.require_version("Gtk", "4.0")
gi.require_version("Adw", "1")
from gi.repository import Adw

from stramp import __version__
from stramp.ui.window import StrampWindow


log = logging.getLogger(__name__)


class StrampApp(Adw.Application):
    def __init__(self, music_dir: str, waybar_mode: bool):
        super().__init__(application_id="io.stramp.player")
        self.music_dir   = music_dir
        self.waybar_mode = waybar_mode
        self.connect(
            "activate",
            lambda app: StrampWindow(app, self.music_dir, self.waybar_mode).present(),
        )


def main():
    import argparse

    logging.basicConfig(
        level=logging.WARNING,
        format="%(levelname)s [%(name)s] %(message)s",
    )

    p = argparse.ArgumentParser(
        prog="stramp",
        description=f"stramp {__version__} — Strong's music player",
    )
    p.add_argument(
        "directory",
        nargs="?",
        default=str(Path.home() / "Music"),
        help="Path to your music library (default: ~/Music)",
    )
    p.add_argument(
        "--waybar",
        action="store_true",
        help="Enable waybar integration (writes /tmp/stramp.json, socket at /tmp/stramp.sock)",
    )
    p.add_argument(
        "--version",
        action="version",
        version=f"stramp {__version__}",
    )
    p.add_argument(
        "--debug",
        action="store_true",
        help="Enable debug logging",
    )
    args = p.parse_args()

    if args.debug:
        logging.getLogger().setLevel(logging.DEBUG)

    music_dir = os.path.expanduser(args.directory)
    if not os.path.isdir(music_dir):
        print(f"stramp: error: '{music_dir}' is not a directory.", file=sys.stderr)
        sys.exit(1)

    sys.exit(StrampApp(music_dir, args.waybar).run(sys.argv[:1]))


if __name__ == "__main__":
    main()
