#!/usr/bin/env python3
"""Drag-and-drop playlist editor for KSAMusic's public pack (user, 2026-11-06: "make a tool for me to assign tracks to
all situations easily, drag and drop format").

Serves tools/playlist_editor.html on http://127.0.0.1:8767 with the library (library/library.json, built by
build_library.py) and the audio for preview. Save writes:
  pack/assignments.json        the editor's state (what goes where), to reopen and keep editing; the release
                               ships it as Playlists/preset.json (the editor's "Reset to preset")
  pack/Playlists/menu.cfg      main menu and credits
  pack/Playlists/kerbol.cfg    everything else (KSAMusic plays it in Kerbol worlds)
  pack/Playlists/sol.cfg       the same, for Sol (unless "leave Sol flight to KSA" is on, then without flight)
in the Soundtrack Editor format KSAMusic reads (see KSAMusic/Cfg.cs). Nothing here touches out/KSAMusic.

    python3 tools/playlist_editor.py [--port 8767]
"""
import argparse
import json
import mimetypes
import os
import re
import xml.etree.ElementTree as ET
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import unquote, urlparse

ROOT = Path(__file__).resolve().parent.parent
LIB = ROOT / "library"
PACK = ROOT / "pack"
HTML = Path(__file__).with_name("playlist_editor.html")
GAME_MUSIC = Path(os.path.realpath(Path.home() / "Games/KSA/current")) / "Content" / "Core" / "Music"


def game_tracks():
    """KSA's own music, played from the game install (the mod does the same); never shipped."""
    out = []
    for f in sorted(GAME_MUSIC.glob("*.ogg")) if GAME_MUSIC.is_dir() else []:
        stem = f.stem
        title = stem[3:] if len(stem) > 3 and stem[:2].isdigit() and stem[2] == "_" else stem
        title = re.sub(r"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ", title)
        out.append({"id": stem, "title": title, "album": "Kitten Space Agency (your game)", "artist": "RocketWerkz",
                    "license": "KSA's own music, played from your game", "pack": False, "own": False, "game": True,
                    "path": str(f), "duration": None})
    return out


def full_library():
    lib = json.loads((LIB / "library.json").read_text())
    lib["tracks"] += game_tracks()
    return lib


def cfg_value(v):
    return "True" if v is True else "False" if v is False else str(v)


def write_playlist(out, card, tracks):
    out.append("playlist")
    out.append("{")
    out.append(f"\tname = {card['title']}")
    out.append(f"\tloop = {cfg_value(card.get('loop', True))}")
    out.append(f"\tshuffle = {cfg_value(card.get('shuffle', False))}")
    out.append(f"\tpauseOnGamePause = {cfg_value(card.get('pauseOnGamePause', True))}")
    out.append("\ttracks")
    out.append("\t{")
    out.extend(f"\t\ttrack = {t}" for t in tracks)
    out.append("\t}")
    out.append("\tplayWhen")
    out.append("\t{")
    for k, v in card["when"].items():
        out.append(f"\t\t{k} = {cfg_value(v)}")
    out.append("\t}")
    out.append("}")


def write_pack(state, pack=PACK):
    """Writes assignments.json and Playlists/{menu,kerbol,sol}.cfg into `pack`; returns the track ids used."""
    pack.mkdir(parents=True, exist_ok=True)
    (pack / "assignments.json").write_text(json.dumps(state, indent=1))
    pl = pack / "Playlists"
    pl.mkdir(exist_ok=True)
    header = ["// KSAMusic playlists, written by tools/playlist_editor.py. Edit them there, not here.",
              "// First matching playlist wins, so specific situations come first."]
    menu, world, sol = list(header), list(header), list(header)
    used = set()
    for card in state["cards"]:              # already in priority order (the editor sorts them)
        tracks = card.get("tracks") or []
        if not tracks:
            continue                          # nothing assigned: KSA's own music (or silence) for that situation
        used.update(tracks)
        target = menu if card.get("file") == "menu" else world
        write_playlist(target, card, tracks)
        if target is world and not (state.get("solFlightToKsa") and card["when"].get("scene") == "Flight"):
            write_playlist(sol, card, tracks)
    (pl / "menu.cfg").write_text("\n".join(menu) + "\n")
    (pl / "kerbol.cfg").write_text("\n".join(world) + "\n")
    (pl / "sol.cfg").write_text("\n".join(sol) + "\n")
    return sorted(used)


def bodies_json():
    """The game's bodies for "Add a place": the bodies.json KSAMusic writes while KSA runs (newest one found), else
    read straight from the system XMLs of KSA and the mods in the Borea instances."""
    home = Path.home()
    found = list(home.glob(".local/share/Borea/Instances/*/KSAMusic/bodies.json"))
    found += list(home.glob("Documents/My Games/Kitten Space Agency/KSAMusic/bodies.json"))
    if found:
        return max(found, key=os.path.getmtime).read_bytes()
    roots = [Path(os.path.realpath(home / "Games/KSA/current")) / "Content"]
    roots += [Path(os.path.realpath(m)) for m in home.glob(".local/share/Borea/Instances/*/mods/*")]
    systems, seen = [], set()
    for root in roots:
        for f in root.rglob("*.xml") if root.is_dir() else []:
            try:
                with open(f, "rb") as fh:
                    if b"<System " not in fh.read(4096):
                        continue
                sys_el = ET.parse(f).getroot()
            except Exception:
                continue
            if sys_el.tag != "System" or sys_el.get("Id") in seen:
                continue
            seen.add(sys_el.get("Id"))
            entries = [(e.get("Id"), e.get("Parent") or "", e.tag) for e in sys_el if e.get("Id") and "Vehicle" not in e.tag]
            stars = {i for i, p, t in entries if not p and "Barycenter" not in t}
            centres = {i for i, p, t in entries if not p}
            name = sys_el.find("DisplayName")
            systems.append({
                "id": sys_el.get("Id"), "name": name.get("Value") if name is not None else sys_el.get("Id"), "loaded": False,
                "bodies": [{"id": i, "parent": p, "kind": "star" if i in stars else "planet" if (p in centres or not p) else "moon"}
                           for i, p, t in entries if "Barycenter" not in t],
            })
    return json.dumps({"current": None, "systems": systems, "fromFiles": True}).encode()


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        pass

    def send(self, code, body, ctype="application/json"):
        data = body if isinstance(body, bytes) else body.encode()
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        path = unquote(urlparse(self.path).path)
        if path in ("/", "/index.html"):
            return self.send(200, HTML.read_bytes(), "text/html; charset=utf-8")
        if path == "/library.json":
            return self.send(200, json.dumps(full_library()).encode())
        if path == "/bodies.json":
            return self.send(200, bodies_json())
        if path == "/preset.json":
            f = PACK / "preset.json"
            return self.send(200, f.read_bytes() if f.exists() else b"null")
        if path == "/state.json":
            f = PACK / "assignments.json"
            return self.send(200, f.read_bytes() if f.exists() else b"null")
        if path.startswith("/audio/"):
            lib = full_library()["tracks"]
            track = next((t for t in lib if t["id"] == path[len("/audio/"):]), None)
            if not track:
                return self.send(404, "no such track", "text/plain")
            f = Path(track["path"]) if track.get("game") else LIB / track["file"]
            return self.send_file(f)
        return self.send(404, "not found", "text/plain")

    def send_file(self, f):
        # range requests so the browser can seek in the preview
        size = f.stat().st_size
        rng = self.headers.get("Range")
        start, end = 0, size - 1
        if rng and rng.startswith("bytes="):
            a, _, b = rng[6:].partition("-")
            start = int(a) if a else 0
            end = int(b) if b else size - 1
        with open(f, "rb") as fh:
            fh.seek(start)
            data = fh.read(end - start + 1)
        self.send_response(206 if rng else 200)
        self.send_header("Content-Type", mimetypes.guess_type(f.name)[0] or "audio/ogg")
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Length", str(len(data)))
        if rng:
            self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
        self.end_headers()
        self.wfile.write(data)

    def do_POST(self):
        if urlparse(self.path).path != "/save":
            return self.send(404, "not found", "text/plain")
        state = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))))
        used = write_pack(state)
        self.send(200, json.dumps({"saved": True, "tracks": len(used), "dir": str(PACK)}))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8767)
    args = ap.parse_args()
    print(f"KSAMusic playlist editor on http://127.0.0.1:{args.port}", flush=True)
    ThreadingHTTPServer(("127.0.0.1", args.port), Handler).serve_forever()


if __name__ == "__main__":
    main()
