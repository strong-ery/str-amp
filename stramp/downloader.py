#!/usr/bin/env python3
# SPDX-License-Identifier: CC-BY-NC-SA-4.0
"""
tools/download_music.py — download a Spotify CSV export from YouTube.

Usage:
    python tools/download_music.py --csv export.csv --out ~/Music
    python tools/download_music.py --csv export.csv --out ~/Music --workers 4 --cookies-from-browser firefox
    python tools/download_music.py --retry-uncertain uncertain_20250101_120000.json --out ~/Music

Dependencies:
    pacman -S yt-dlp
    pip install mutagen --break-system-packages
"""

import csv
import subprocess
import json
import argparse
import re
import urllib.request
import urllib.parse
import threading
import concurrent.futures
from pathlib import Path
from datetime import datetime

try:
    from mutagen.id3 import ID3, TIT2, TPE1, TALB, TDRC, APIC, ID3NoHeaderError
    from mutagen.mp3 import MP3
    MUTAGEN_AVAILABLE = True
except ImportError:
    MUTAGEN_AVAILABLE = False
    print("⚠  mutagen not found — install it with: pip install mutagen --break-system-packages")
    print("   Falling back to yt-dlp metadata (less reliable)\n")

POPULARITY_THRESHOLD = 40

BAD_UPLOADERS  = ["lyrics", "karaoke", "nightcore", "8d", "amv"]
GOOD_HINTS     = ["topic", "official", "vevo"]

SEARCH_QUERIES = [
    "{artist} - {track}",
    "{track} - {artist}",
    "{artist} {track} official audio",
]

DEFAULT_WORKERS = 6

# ── Thread-safe print ─────────────────────────────────────────────────────────

_print_lock = threading.Lock()

def tprint(*args, **kwargs):
    with _print_lock:
        print(*args, **kwargs)

# ── CSV ───────────────────────────────────────────────────────────────────────

def parse_csv(csv_path: str) -> list[dict]:
    songs = []
    with open(csv_path, newline="", encoding="utf-8-sig") as f:
        reader = csv.DictReader(f)
        for row in reader:
            songs.append({
                "track":       row["Track Name"].strip(),
                "artist":      row["Artist Name(s)"].strip(),
                "album":       row["Album Name"].strip(),
                "year":        str(row["Release Date"]).split("-")[0].strip(),
                "popularity":  int(row["Popularity"]) if row["Popularity"] else 0,
                "duration_ms": int(row.get("Duration (ms)", 0) or 0),
            })
    return songs


def parse_uncertain_log(log_path: str) -> list[dict]:
    """Load a previously saved uncertain JSON log as a list of song dicts."""
    with open(log_path, encoding="utf-8") as f:
        entries = json.load(f)
    songs = []
    for e in entries:
        # strip fields added by the logger, keep only original song data
        songs.append({
            "track":       e.get("track", ""),
            "artist":      e.get("artist", ""),
            "album":       e.get("album", ""),
            "year":        e.get("year", ""),
            "popularity":  e.get("popularity", 0),
            "duration_ms": e.get("duration_ms", 0),
        })
    return [s for s in songs if s["track"] and s["artist"]]

# ── Helpers ───────────────────────────────────────────────────────────────────

def normalize_artists(artist_str: str) -> list[str]:
    return [
        a.strip()
        for a in re.split(r"[;,/&]|feat\.?|\(feat", artist_str, flags=re.IGNORECASE)
        if a.strip()
    ]

def clean_text(s: str) -> str:
    return re.sub(r"[^\w\s]", "", s.lower())

def detect_version(text: str) -> set:
    text = text.lower()
    tags = {
        "slowed":       ["slowed", "slowed + reverb"],
        "sped":         ["sped up", "speed up"],
        "instrumental": ["instrumental"],
        "live":         ["live"],
        "remix":        ["remix"],
    }
    return {k for k, v in tags.items() if any(x in text for x in v)}

def safe_filename(s: str) -> str:
    return re.sub(r'[\\/*?:"<>|]', "_", s)

def already_exists(out_dir: Path, artist: str, track: str) -> bool:
    prefix = f"{safe_filename(artist)} - {safe_filename(track)}"
    return any(out_dir.glob(f"{prefix}.*"))

# ── Scoring ───────────────────────────────────────────────────────────────────

def score_result(song: dict, result: dict) -> tuple[int, list]:
    title    = result.get("title", "").lower()
    uploader = result.get("uploader", "").lower()
    yt_dur   = result.get("duration", 0)

    artists     = [a.lower() for a in normalize_artists(song["artist"])]
    track_words = clean_text(song["track"]).split()
    title_words = clean_text(title).split()

    score   = 0
    reasons = []

    if any(a in title or a in uploader for a in artists):
        score += 3
    else:
        score -= 2
        reasons.append("artist mismatch")

    common = set(track_words) & set(title_words)
    if len(common) >= max(1, len(track_words) // 2):
        score += 3
    else:
        score -= 2
        reasons.append("track mismatch")

    wanted = detect_version(song["track"])
    yt_v   = detect_version(title)
    if wanted:
        if wanted & yt_v:
            score += 2
        else:
            score -= 2
            reasons.append("version mismatch")
    else:
        if yt_v:
            score -= 1

    if song["duration_ms"] and yt_dur:
        spotify_sec = song["duration_ms"] / 1000
        diff = abs(spotify_sec - yt_dur)
        if wanted:
            score += 1 if diff <= 20 else -1
        else:
            if diff <= 3:
                score += 4
            elif diff <= 8:
                score += 2
            elif diff <= 20:
                score -= 1
            else:
                score -= 5
                reasons.append(f"duration mismatch ({diff:.1f}s)")

    if any(b in uploader for b in BAD_UPLOADERS):
        score -= 3
        reasons.append("bad uploader")
    if any(g in uploader for g in GOOD_HINTS):
        score += 2

    if song["popularity"] >= POPULARITY_THRESHOLD:
        score += 1

    return score, reasons

# ── Search ────────────────────────────────────────────────────────────────────

def search_candidates(song: dict, cookies_from_browser: str | None = None) -> list[dict]:
    seen    = set()
    results = []
    lock    = threading.Lock()

    def _run_query(q_template):
        query = q_template.format(artist=song["artist"], track=song["track"])
        cmd   = ["yt-dlp", "--dump-json", "--no-playlist", f"ytsearch7:{query}"]
        if cookies_from_browser:
            cmd += ["--cookies-from-browser", cookies_from_browser]
        proc  = subprocess.run(cmd, capture_output=True, text=True)
        local = []
        for line in proc.stdout.splitlines():
            if not line.strip():
                continue
            try:
                entry = json.loads(line)
            except json.JSONDecodeError:
                continue
            vid_id = entry.get("id") or entry.get("webpage_url")
            if vid_id:
                local.append((vid_id, entry))
        return local

    with concurrent.futures.ThreadPoolExecutor(max_workers=len(SEARCH_QUERIES)) as ex:
        futures = [ex.submit(_run_query, q) for q in SEARCH_QUERIES]
        for fut in concurrent.futures.as_completed(futures):
            for vid_id, entry in fut.result():
                with lock:
                    if vid_id not in seen:
                        seen.add(vid_id)
                        results.append(entry)

    return results

# ── Album art ─────────────────────────────────────────────────────────────────

def fetch_itunes_art(song: dict) -> str | None:
    # Prefer artist+album search; fall back to artist+track when album is unknown
    search_term = (
        f"{song['artist']} {song['album']}"
        if song.get("album")
        else f"{song['artist']} {song['track']}"
    )
    query = urllib.parse.quote(search_term)
    # Use entity=song so we get track-level results (includes artworkUrl100)
    # even when searching by album name — and it works for track-name searches too
    url = f"https://itunes.apple.com/search?term={query}&media=music&entity=song&limit=10"
    try:
        with urllib.request.urlopen(url, timeout=8) as r:
            data = json.loads(r.read())
        track_lower  = song["track"].lower()
        artist_lower = song["artist"].lower()
        results = data.get("results", [])
        # First pass: require both track name and artist to match
        for result in results:
            t = result.get("trackName", "").lower()
            a = result.get("artistName", "").lower()
            art_url = result.get("artworkUrl100", "")
            if art_url and track_lower in t and artist_lower in a:
                return art_url.replace("100x100bb", "600x600bb")
        # Second pass: accept any result that has art (better than nothing)
        for result in results:
            art_url = result.get("artworkUrl100", "")
            if art_url:
                return art_url.replace("100x100bb", "600x600bb")
    except Exception as e:
        tprint(f"  ⚠ iTunes art lookup failed: {e}")
    return None


def fetch_yt_thumbnail(yt_result: dict) -> str | None:
    """
    Pick the best thumbnail from yt-dlp's thumbnails array.

    yt-dlp returns thumbnails in ascending resolution order (smallest first).
    We want the largest standard YouTube thumbnail, which is typically
    maxresdefault > sddefault > hqdefault — all of which appear in the URL.
    We prefer those over channel-avatar / alternate-crop URLs.
    """
    thumbnails = yt_result.get("thumbnails") or []

    # Score each thumbnail: higher = better
    def _thumb_score(t: dict) -> int:
        url = t.get("url", "")
        if not url.startswith("http"):
            return -1
        # Strongly prefer standard YT thumbnail filenames by resolution tier
        if "maxresdefault" in url:
            return 4
        if "sddefault" in url:
            return 3
        if "hqdefault" in url:
            return 2
        if "mqdefault" in url:
            return 1
        # Avoid channel avatars and other non-video images
        if "ggpht" in url or "ytimg.com/vi_webp" not in url and "ytimg.com/vi/" not in url:
            return 0
        return 0

    scored = sorted(thumbnails, key=_thumb_score, reverse=True)
    for t in scored:
        url = t.get("url", "")
        if url.startswith("http") and _thumb_score(t) >= 0:
            return url

    # Hard fallback to the top-level thumbnail field
    return yt_result.get("thumbnail")


def download_image(url: str) -> bytes | None:
    try:
        req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
        with urllib.request.urlopen(req, timeout=10) as r:
            return r.read()
    except Exception as e:
        tprint(f"  ⚠ image download failed ({url}): {e}")
        return None

def embed_art(mp3_path: str, img_data: bytes, mime: str = "image/jpeg") -> bool:
    try:
        tags = ID3(mp3_path)
        tags.delall("APIC")
        tags.add(APIC(encoding=3, mime=mime, type=3, desc="Cover", data=img_data))
        tags.save(mp3_path, v2_version=3)
        return True
    except Exception as e:
        tprint(f"  ⚠ art embedding failed: {e}")
        return False

def attach_cover_art(mp3_path: Path, song: dict, yt_result: dict):
    itunes_url = fetch_itunes_art(song)
    if itunes_url:
        img_data = download_image(itunes_url)
        if img_data and embed_art(str(mp3_path), img_data):
            tprint("  ✔ cover art → iTunes")
            return

    tprint("  ⚠ iTunes art unavailable, falling back to YouTube thumbnail")
    yt_thumb_url = fetch_yt_thumbnail(yt_result)
    if not yt_thumb_url:
        tprint("  ⚠ no YouTube thumbnail found either, skipping cover")
        return

    img_data = download_image(yt_thumb_url)
    if not img_data:
        tprint("  ⚠ could not download YouTube thumbnail, skipping cover")
        return

    mime = "image/webp" if yt_thumb_url.endswith(".webp") else "image/jpeg"
    if embed_art(str(mp3_path), img_data, mime=mime):
        tprint("  ✔ cover art → YouTube thumbnail (fallback)")
    else:
        tprint("  ✗ cover art embedding failed entirely")

# ── Download & tag ────────────────────────────────────────────────────────────

def download_audio(url: str, out_template: str,
                   cookies_from_browser: str | None = None) -> bool:
    cmd = [
        "yt-dlp", "-x",
        "--audio-format", "mp3",
        "--audio-quality", "0",
        "--no-embed-metadata",
        "--sleep-requests", "2",
        "--sleep-interval", "3",
        "--output", out_template,
    ]
    if cookies_from_browser:
        cmd += ["--cookies-from-browser", cookies_from_browser]
    cmd.append(url)
    proc = subprocess.run(cmd, capture_output=True, text=True)
    if proc.returncode != 0:
        for line in proc.stderr.splitlines():
            tprint(f"  [yt-dlp] {line}")
    return proc.returncode == 0

def tag_with_mutagen(path: str, song: dict) -> bool:
    try:
        try:
            tags = ID3(path)
        except ID3NoHeaderError:
            tags = ID3()
        tags.delall("TIT2"); tags.add(TIT2(encoding=3, text=song["track"]))
        tags.delall("TPE1"); tags.add(TPE1(encoding=3, text=song["artist"]))
        tags.delall("TALB"); tags.add(TALB(encoding=3, text=song["album"]))
        tags.delall("TDRC"); tags.add(TDRC(encoding=3, text=song["year"]))
        tags.save(path, v2_version=3)
        return True
    except Exception as e:
        tprint(f"  ⚠ mutagen tagging failed: {e}")
        return False

def tag_with_ytdlp(url: str, out_template: str, song: dict,
                   cookies_from_browser: str | None = None) -> bool:
    meta_args = " ".join([
        f"-metadata title={json.dumps(song['track'])}",
        f"-metadata artist={json.dumps(song['artist'])}",
        f"-metadata album={json.dumps(song['album'])}",
        f"-metadata date={json.dumps(song['year'])}",
    ])
    cmd = [
        "yt-dlp", "-x",
        "--audio-format", "mp3",
        "--audio-quality", "0",
        "--no-embed-metadata",
        "--sleep-requests", "2",
        "--sleep-interval", "3",
        "--output", out_template,
    ]
    if cookies_from_browser:
        cmd += ["--cookies-from-browser", cookies_from_browser]
    cmd.append(url)
    proc = subprocess.run(cmd, capture_output=True, text=True)
    if proc.returncode != 0:
        for line in proc.stderr.splitlines():
            tprint(f"  [yt-dlp] {line}")
    return proc.returncode == 0

def download_and_tag(song: dict, url: str, yt_result: dict,
                     out_dir: Path, uncertain_log: list, log_lock: threading.Lock,
                     cookies_from_browser: str | None = None) -> bool:
    out_template = str(out_dir / f"{safe_filename(song['artist'])} - {safe_filename(song['track'])}.%(ext)s")
    mp3_path     = out_dir / f"{safe_filename(song['artist'])} - {safe_filename(song['track'])}.mp3"

    if MUTAGEN_AVAILABLE:
        if not download_audio(url, out_template, cookies_from_browser):
            tprint("  ✗ download failed")
            with log_lock:
                uncertain_log.append({"reason": "download_failed", "url": url, **song})
            return False
        if mp3_path.exists():
            tag_with_mutagen(str(mp3_path), song)
            attach_cover_art(mp3_path, song, yt_result)
        else:
            tprint("  ⚠ mp3 not found after download for tagging")
    else:
        if not tag_with_ytdlp(url, out_template, song, cookies_from_browser):
            tprint("  ✗ download+tag failed")
            with log_lock:
                uncertain_log.append({"reason": "download_failed", "url": url, **song})
            return False
        tprint("  ⚠ mutagen unavailable, cover art skipped")

    return True

# ── Per-song pipeline ─────────────────────────────────────────────────────────

def process_song(song: dict, out_dir: Path,
                 uncertain_log: list, log_lock: threading.Lock,
                 counter: list, total: int, counter_lock: threading.Lock,
                 cookies_from_browser: str | None = None):
    with counter_lock:
        counter[0] += 1
        idx = counter[0]

    label = f"{song['artist']} - {song['track']}"

    if already_exists(out_dir, song["artist"], song["track"]):
        tprint(f"[{idx}/{total}] {label}  → already exists, skipping")
        return

    tprint(f"[{idx}/{total}] {label}")

    candidates = search_candidates(song, cookies_from_browser)
    if not candidates:
        tprint("  ✗ NO RESULTS")
        with log_lock:
            uncertain_log.append({"reason": "no_results", **song})
        return

    scored = sorted(
        [(score_result(song, c), c) for c in candidates],
        key=lambda x: x[0][0],
        reverse=True,
    )

    (best_score, reasons), best = scored[0]
    tprint(f"  → {best.get('title')} (score={best_score})")

    if best_score < 3:
        tprint("  ⚠ UNCERTAIN — skipping download")
        with log_lock:
            uncertain_log.append({
                "reason": reasons,
                "chosen_title": best.get("title"),
                "url": best.get("webpage_url"),
                **song,
            })
        return

    download_and_tag(song, best.get("webpage_url"), best, out_dir,
                     uncertain_log, log_lock, cookies_from_browser)

# ── Entry point ───────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(
        description="Download a Spotify CSV export from YouTube as tagged mp3s."
    )

    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--csv",
                        help="Path to Spotify CSV export (Exportify format)")
    source.add_argument("--retry-uncertain", metavar="LOG",
                        help="Path to a previous uncertain_*.json log — retry only those songs")

    parser.add_argument("--out",     required=True, help="Output directory for mp3s")
    parser.add_argument("--workers", type=int, default=DEFAULT_WORKERS,
                        help=f"Parallel download workers (default: {DEFAULT_WORKERS})")
    parser.add_argument("--url", metavar="URL",
                        help="Skip YouTube search and download directly from this URL. "
                             "Only valid with a single-song CSV.")
    parser.add_argument("--cookies-from-browser", metavar="BROWSER",
                        dest="cookies_from_browser",
                        help="Pass cookies from your browser to yt-dlp (e.g. firefox, chrome). "
                             "Fixes 'Sign in to confirm you're not a bot' errors.")
    args = parser.parse_args()

    if not MUTAGEN_AVAILABLE:
        print("Run:  pip install mutagen --break-system-packages   for reliable ID3 tagging.\n")

    out_dir = Path(args.out).expanduser()
    out_dir.mkdir(parents=True, exist_ok=True)

    if args.retry_uncertain:
        songs = parse_uncertain_log(args.retry_uncertain)
        print(f"stramp-dl: retrying {len(songs)} uncertain songs from {args.retry_uncertain}")
    else:
        songs = parse_csv(args.csv)

    # ── Direct URL mode ───────────────────────────────────────────────────
    if args.url:
        if len(songs) != 1:
            print("ERROR: --url requires a single-song CSV (exactly one row)")
            return
        song = songs[0]
        print(f"stramp-dl: direct URL → {song['artist']} - {song['track']}")
        uncertain_log = []
        log_lock      = threading.Lock()
        yt_stub = {
            "webpage_url": args.url,
            "title":       song["track"],
            "uploader":    "",
            "duration":    song["duration_ms"] // 1000 if song["duration_ms"] else 0,
            "thumbnails":  [],
        }
        ok = download_and_tag(
            song, args.url, yt_stub, out_dir,
            uncertain_log, log_lock, args.cookies_from_browser,
        )
        if ok:
            print(f"\nDone. 1/1 downloaded → {out_dir}")
        else:
            print(f"\nDone. 0/1 downloaded, 1 uncertain → {out_dir}")
        return

    # ── Normal batch mode ─────────────────────────────────────────────────
    total         = len(songs)
    uncertain_log = []
    log_lock      = threading.Lock()
    counter       = [0]
    counter_lock  = threading.Lock()

    print(f"stramp-dl: {total} songs, {args.workers} workers → {out_dir}\n")

    def flush_log():
        if uncertain_log:
            log_path = out_dir / f"uncertain_{datetime.now().strftime('%Y%m%d_%H%M%S')}.json"
            with open(log_path, "w") as f:
                json.dump(uncertain_log, f, indent=2)
            print(f"\n  Uncertain log → {log_path}")

    try:
        with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
            futures = {
                pool.submit(
                    process_song,
                    song, out_dir, uncertain_log, log_lock,
                    counter, total, counter_lock,
                    args.cookies_from_browser,
                ): song
                for song in songs
            }
            for fut in concurrent.futures.as_completed(futures):
                try:
                    fut.result()
                except Exception as e:
                    song = futures[fut]
                    tprint(f"  ✗ unhandled error for {song['artist']} - {song['track']}: {e}")
                    with log_lock:
                        uncertain_log.append({"reason": f"exception: {e}", **song})

    except KeyboardInterrupt:
        print("\n\n⚠ Interrupted.")
        flush_log()
        return

    flush_log()
    uncertain = len(uncertain_log)
    success   = total - uncertain
    print(f"\nDone. {success}/{total} downloaded, {uncertain} uncertain → {out_dir}")


if __name__ == "__main__":
    main()