#!/usr/bin/env python3
"""Builds the public KSAMusic release (user, 2026-11-06: release KSAMusic on its own, with the playlist editor and
their preset). Only redistributable music goes in: Askerad's Transfer Windows albums (CC BY 3.0) plus any other
library track the preset uses (Kevin MacLeod, CC BY 4.0; Vano, CC BY-SA 4.0), each credited.

    python3 tools/make_release.py <version> [KSA dir]

Reads library/library.json (build_library.py) and pack/assignments.json (the preset, saved by the playlist editor).
Builds into dist/; the personal pack (out/KSAMusic, build_pack.py) is never touched, so it's safe while KSA runs.
"""
import json
import os
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from playlist_editor import write_pack  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
LIB = ROOT / "library"
DIST = ROOT / "dist"

SETTINGS = """# KSAMusic settings. Edit while the game is closed.
# volume: starting music level on top of KSA's Music slider; Music > Volume overrides it
volume = 0.8
# kerbol: dmo (play the playlists in Kerbol worlds) or off
kerbol = dmo
# sol: ost (play the playlists in Sol too), stock (KSA's own music only), off (silence)
sol = ost
# ostSpaceLayer: add KSP soundtrack files (if you have them in Music/) to space playlists in Kerbol worlds
ostSpaceLayer = false
# fadeOut: seconds to fade a playlist out when the situation changes
fadeOut = 2
# testAliases: debugging only (play Kerbol body playlists in Sol)
testAliases = false
# overlay: the Music window starts open (Music > Music window remembers your choice)
overlay = false
"""


def main():
    version = sys.argv[1]
    ksa = Path(os.path.realpath(sys.argv[2] if len(sys.argv) > 2 else Path.home() / "Games/KSA/current"))
    game = ksa.name.split("_v")[-1]
    preset_file = ROOT / "pack" / "assignments.json"
    if not preset_file.exists():
        sys.exit("no preset yet: save one in the playlist editor first (pack/assignments.json)")
    preset = json.loads(preset_file.read_text())
    library = {t["id"]: t for t in json.loads((LIB / "library.json").read_text())["tracks"]}

    stage = DIST / "stage"
    mod = stage / "KSAMusic"
    shutil.rmtree(stage, ignore_errors=True)
    mod.mkdir(parents=True)
    subprocess.run([os.environ.get("DOTNET", str(Path.home() / ".dotnet/dotnet")), "build", str(ROOT / "KSAMusic"),
                    "-c", "Release", "-nologo", "-v", "q", f"-p:KsaDir={ksa}",
                    f"-p:OutputPath={DIST / 'build'}/", f"-p:ModOutDir={mod}/"], check=True)

    # playlists from the preset, and the preset itself for the editor's Reset
    used = write_pack(preset, mod)
    (mod / "assignments.json").replace(mod / "Playlists" / "preset.json")

    # music: the Askerad albums plus whatever else the preset uses
    ship = [t for t in library.values() if t["pack"] or t["id"] in used]
    game_music = {f.stem for f in (ksa / "Content" / "Core" / "Music").glob("*.ogg")}   # KSA's own: played from the game
    missing = [i for i in used if i not in library and i not in game_music]
    if missing:
        sys.exit(f"the preset uses tracks the library doesn't have: {missing}")
    credits = []
    for t in ship:
        dst = mod / "Music" / Path(t["file"]).relative_to("Music")
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(LIB / t["file"], dst)
        credits.append({k: t[k] for k in ("id", "title", "album", "artist", "license", "licenseUrl", "source", "attribution", "duration")})
    (mod / "Music" / "credits.json").write_text(json.dumps({"tracks": credits}, indent=1))

    lines = ["# Music credits", "", "Every track that ships with KSAMusic, with the attribution its licence asks for.", ""]
    if set(used) & game_music:
        lines += ["Some playlists also use Kitten Space Agency's own music (by RocketWerkz). It isn't included here: "
                  "KSAMusic plays it from your game install.", ""]
    for album in dict.fromkeys(c["album"] for c in credits):
        lines.append(f"## {album}")
        lines.append("")
        lines += [f"- {c['attribution']}" for c in credits if c["album"] == album]
        lines.append("")
    (mod / "CREDITS.md").write_text("\n".join(lines))

    (mod / "settings.cfg").write_text(SETTINGS)
    for f in ("README.md", "LICENSE", "CHANGELOG.md"):
        if (ROOT / f).exists():
            shutil.copy2(ROOT / f, mod / f)

    zip_path = DIST / f"KSAMusic-v{version}-ksa{game}.zip"
    zip_path.unlink(missing_ok=True)
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for f in sorted(mod.rglob("*")):
            if f.is_file():
                z.write(f, f.relative_to(stage))
    size = zip_path.stat().st_size
    print(f"{zip_path.relative_to(ROOT)}: {len(ship)} tracks ({len(used)} in the preset), {size / 1e6:.1f} MB")


if __name__ == "__main__":
    main()
