#!/usr/bin/env python3
"""Builds the open-music library for KSAMusic's public release (user, 2026-11-06: an all-redistributable soundtrack
around Askerad's Transfer Windows, plus Kevin MacLeod's KSP tracks, Vano and Atomikku no Ongaku as extras).

Every track becomes library/Music/<album>/<id>.ogg with a unique id (KSAMusic finds tracks by file name anywhere
under Music/), and library/library.json records its title, album, composer, licence and source, which the playlist
editor and the release credits read. Sources live in sources/ (one folder per download); nothing here touches the
personal pack (out/KSAMusic, tools/build_pack.py).

    python3 tools/build_library.py
"""
import json
import re
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "sources"
LIB = ROOT / "library"
TW_ZIP = ROOT / "transferwindows" / "SKRD_Transfer_Windows" / "Music"

CC_BY_3 = ("CC-BY-3.0", "https://creativecommons.org/licenses/by/3.0/")
CC_BY_4 = ("CC-BY-4.0", "https://creativecommons.org/licenses/by/4.0/")
CC_BY_SA_4 = ("CC-BY-SA-4.0", "https://creativecommons.org/licenses/by-sa/4.0/")

YT = "https://www.youtube.com/watch?v="

# Askerad's KSP albums: (id, title, album, file, YouTube id). Licence: YouTube's Creative Commons Attribution
# (CC BY 3.0) on every one of these videos, checked 2026-11-06.
ASKERAD = [
    ("TW_Home", "Home", "Transfer Windows, Vol. 1", TW_ZIP / "1-Home.ogg", "0INHLhbzWO8"),
    ("TW_OneSmallStep", "One Small Step", "Transfer Windows, Vol. 1", TW_ZIP / "4-OneSmallStep.ogg", "wFusBNACESM"),
    ("TW_PurpleQueen", "Purple Queen", "Transfer Windows, Vol. 1", TW_ZIP / "2-PurpleQueen.ogg", "9ie6VJJUjA4"),
    ("TW_Curvature", "Curvature", "Transfer Windows, Vol. 1", TW_ZIP / "3-Curvature.ogg", "XyO92kKfVyU"),
    ("TW_VabVibes", "VAB Vibes", "Transfer Windows, Vol. 1", TW_ZIP / "5-VabVibes.ogg", "LIqfDIftN7E"),
    ("TW_LullabyForAPlanetarium", "Lullaby for a Planetarium", "Transfer Windows, Vol. 1",
     TW_ZIP / "6-LullabyforaPlanetarium.ogg", "1hzHtOIpbSk"),
    ("TW_BalconyMoongazing", "Balcony Moongazing", "Transfer Windows, Vol. 1", TW_ZIP / "7-BalconyMoongazing.ogg", "q4qlmPOrGOU"),
    ("TW_MyCapsuleMySnacksAndMe", "My Capsule, My Snacks & Me", "Transfer Windows, Vol. 1",
     TW_ZIP / "8-MyCapsuleMySnacksAndMe.ogg", "8eO3jqwWVeM"),
    ("TW_LabcoatsGalore", "Labcoats Galore", "Transfer Windows, Vol. 1", None, "kvXdadS0P8M"),
    ("TW_CorrectionManeuvre", "Correction Maneuvre", "Transfer Windows, Vol. 2", None, "gACK_rqbOz4"),
    ("TW_VoidDwellers", "Void Dwellers", "Transfer Windows, Vol. 2", None, "Qc0TVySrUto"),
    ("TWMun_Nebulosus", "Nebulosus", "Transfer Windows: The Dark Sounds Of The Mun EP", None, "2NAHrpctsuA"),
    ("TWMun_OrbitalClockwork", "Orbital Clockwork", "Transfer Windows: The Dark Sounds Of The Mun EP", None, "3u6DHC8iKTg"),
    ("TWMun_ZeroGEpiphany", "Zero-G Epiphany", "Transfer Windows: The Dark Sounds Of The Mun EP", None, "zThLyQLX7Yo"),
]

# Kevin MacLeod tracks KSP used (KSP wiki soundtrack page), from incompetech, CC BY 4.0.
MACLEOD = ["Arcadia", "Bathed in the Light", "Brittle Rille", "Dreamy Flashback", "Frost Waltz", "Frost Waltz (Alternate)",
           "Frozen Star", "Groove Grove", "Impact Lento", "Wizardtorium", "Sneaky Adventure"]


def slug(s):
    return re.sub(r"[^A-Za-z0-9]", "", s.title())


def duration(path):
    out = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", str(path)],
                         capture_output=True, text=True).stdout.strip()
    return round(float(out), 1) if out else None


def encode(src, dst):
    """Ogg Vorbis at quality 5 (~160 kbit/s); an ogg that's already small enough is copied as it is."""
    dst.parent.mkdir(parents=True, exist_ok=True)
    if dst.exists() and dst.stat().st_mtime >= src.stat().st_mtime:
        return
    if src.suffix.lower() == ".ogg" and src.stat().st_size / max(duration(src) or 1, 1) < 30_000:
        dst.write_bytes(src.read_bytes())
        return
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(src), "-vn", "-map_metadata", "-1",
                    "-c:a", "libvorbis", "-q:a", "5", str(dst)], check=True)


def main():
    tracks = []

    def add(tid, title, album, artist, src, lic, source, attribution):
        dst = LIB / "Music" / slug(album) / f"{tid}.ogg"
        encode(src, dst)
        tracks.append({
            "id": tid, "title": title, "album": album, "artist": artist,
            "license": lic[0], "licenseUrl": lic[1], "source": source, "attribution": attribution,
            "file": str(dst.relative_to(LIB)), "duration": duration(dst),
            "pack": artist == "Askerad",            # bundled by default; the editor can add any other track
        })

    yt_dir = SRC / "askerad-youtube"
    for tid, title, album, file, vid in ASKERAD:
        src = file or next(yt_dir.glob(f"*[[]{vid}[]].ogg"))
        add(tid, title, album, "Askerad", src, CC_BY_3, YT + vid,
            f'"{title}" by Askerad ({YT + vid}), licensed under CC BY 3.0')

    for title in MACLEOD:
        src = SRC / "macleod" / f"{title}.mp3"
        url = "https://incompetech.com/music/royalty-free/mp3-royaltyfree/" + title.replace(" ", "%20") + ".mp3"
        add("MacLeod_" + slug(title), title, "Kevin MacLeod (KSP)", "Kevin MacLeod", src, CC_BY_4, url,
            f'"{title}" Kevin MacLeod (incompetech.com), Licensed under Creative Commons: By Attribution 4.0 License, '
            "http://creativecommons.org/licenses/by/4.0/")

    vano = SRC / "vano" / "files" / "Music"
    if vano.exists():
        seen = set()
        for f in sorted(vano.rglob("*.ogg")):
            title = f.stem
            tid = "Vano_" + slug(title)
            if tid in seen:                          # Expedition 13 is in both Space and Construction
                continue
            seen.add(tid)
            add(tid, title, f"Vano's Soundtrack ({f.parent.name})", "MusicByVano", f, CC_BY_SA_4,
                "https://spacedock.info/mod/3667", f'"{title}" by MusicByVano, licensed under CC BY-SA 4.0')

    # A silent track: KSP played no music on the ground, in the atmosphere or at launch, and an empty situation would
    # hand over to KSA's own music instead. A playlist of this keeps those quiet.
    silence = SRC / "silence" / "silence.ogg"
    if not silence.exists():
        silence.parent.mkdir(parents=True, exist_ok=True)
        subprocess.run(["ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", "30",
                        "-c:a", "libvorbis", "-q:a", "0", str(silence)], check=True)
    add("KSAMusic_Silence", "Silence", "KSAMusic", "", silence, ("CC0-1.0", "https://creativecommons.org/publicdomain/zero/1.0/"),
        "", "Silence (30 s of nothing, made for KSAMusic)")
    tracks[-1]["pack"] = True

    LIB.mkdir(exist_ok=True)
    (LIB / "library.json").write_text(json.dumps({"tracks": tracks}, indent=1))
    print(f"{len(tracks)} tracks, {sum(t['pack'] for t in tracks)} in the default pack -> {LIB / 'library.json'}")


if __name__ == "__main__":
    main()
