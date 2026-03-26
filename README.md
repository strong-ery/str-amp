# stramp
A lightweight GTK4 music player for Linux, built for local libraries. Shuffle-first, minimal RAM, clean UI.

![License](https://img.shields.io/badge/license-CC--BY--NC--SA--4.0-blue)
![Python](https://img.shields.io/badge/python-3.11+-green)

## Features

- Shuffle-first playback with a live queue sidebar
- Album art display with automatic fetching and fixing
- Library search
- Download songs by name or from a Spotify Exportify CSV via YouTube
- Native MPRIS2 integration — shows up in waybar, playerctl, and any MPRIS-aware tool
- Waybar scroll-to-volume, click-to-pause, right-click-to-skip

## Dependencies

**System packages (pacman):**
```
sudo pacman -S python-gobject libadwaita python-dbus mpv yt-dlp
```

**Python packages:**
```
pip install python-mpv mutagen --break-system-packages
```

**Optional — waybar MPRIS2 integration:**
```
sudo pacman -S playerctl
```

**Note:** Make sure `~/.local/bin` is in your PATH. Add this to your `.bashrc` or `.zshrc` if not:
```
export PATH="$HOME/.local/bin:$PATH"
```

## Installation
```
git clone https://github.com/strong-ery/stramp
cd stramp
pip install -e . --break-system-packages
```

## Usage
```
stramp                        # uses ~/Music
stramp /path/to/music         # custom directory
stramp --waybar               # enable MPRIS2 + waybar integration
stramp --debug                # verbose logging
stramp --version
```

## Waybar

Launch with `--waybar` and stramp appears automatically in your `mpris` module.

Optional scroll behaviour — add to your `mpris` module in `config.jsonc`:
```jsonc
"on-click": "playerctl play-pause",
"on-click-right": "playerctl next",
"on-scroll-up": "playerctl volume 0.05+",
"on-scroll-down": "playerctl volume 0.05-"
```

## Downloading Music

Stramp can download songs directly into your library via the **⋮** menu:

- **Install Song by Name** — enter artist and title, stramp finds and downloads the best match from YouTube
- **Install from Exportify CSV** — export a Spotify playlist with [Exportify](https://exportify.net), then feed the CSV to stramp to batch download the whole thing

## License

[CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/)

---

*Some portions of this codebase were written with the assistance of generative AI tools.*
>>>>>>> ad83d9d (V1 files)
