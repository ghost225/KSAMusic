# KSAMusic

Situational music for [Kitten Space Agency](https://ahwoo.com/): the right track for where you are and what you're doing, as KSP did it, with a drag-and-drop editor to make the soundtrack your own. A [StarMap](https://github.com/StarMapLoader/StarMap) mod.

## Features

- **Music that follows the game.** Playlists switch by scene (main menu, vehicle editor, tracking station, flight), by situation (launch, on the ground, flying, sub-orbital, in orbit, escaping, between the planets) and by body.
- **A KSP-like soundtrack out of the box.** As in stock KSP, the music plays in space (Kevin MacLeod's tracks, shuffled) and the pad, the ground and the atmosphere stay quiet. The main menu, the vehicle editor and the tracking station get themes from Askerad's Transfer Windows, a fan-made KSP soundtrack.
- **A playlist editor.** **Music → Edit playlists...** in the top menu bar opens it in your browser. Drag tracks onto situations, reorder them, preview anything with the player at the bottom, and add body-specific music by picking from the bodies actually in your game (mods included). **Save** applies it in game straight away, and **Reset to preset** brings the default back.
- **Your own music.** Put `.ogg` files in `KSAMusic/Music` in your game profile (the editor shows the exact folder) and they appear in the editor to assign.
- **KSA's own music** shows up in the editor too, played from your game install.
- **A Music menu** in the top bar: what's playing, next track, mute, volume, and a small status window.
- **Plays Dynamic Music Overhaul's playlists.** It reads Soundtrack Editor Forked's `playlists.cfg` format, so DMO and similar KSP music packs work with their own playlists.

## Music included

All of it can be shared; the full credits, with links, are in [`CREDITS.md`](CREDITS.md) (also in the download).

- **Transfer Windows**, Vol. 1, Vol. 2 and The Dark Sounds Of The Mun EP, by [Askerad](https://www.youtube.com/channel/UCkmEzU58CnNtMk8eDCPG6EQ), CC BY 3.0
- **Kevin MacLeod**'s tracks from KSP's soundtrack ([incompetech.com](https://incompetech.com)), CC BY 4.0

## Requirements

- Kitten Space Agency 2026.10.25.5707 or later
- [StarMap](https://github.com/StarMapLoader/StarMap) 0.4.7 or later

## Installation

**With Borea:** install KSAMusic from the mod list.

**Manually:**

1. Install StarMap and run the game once through it.
2. Extract the release into your profile's `mods` folder (by default `Documents/My Games/Kitten Space Agency/mods/`), giving `mods/KSAMusic/KSAMusic.dll`.
3. Start the game once and close it. KSA adds new mods to `manifest.toml` disabled: set this mod's entry to `enabled = true`.
4. Start the game through StarMap.

To uninstall, delete `mods/KSAMusic`, and `KSAMusic` in your game profile if you saved playlists or added music.

## Settings

`settings.cfg` beside the dll (edit while the game is closed):

- `volume`: KSAMusic's level on top of KSA's Music slider (the Music menu's slider overrides it).
- `kerbol` / `sol`: whether the playlists play in Kerbol-type worlds and in Sol (`dmo` / `ost` = play them, `stock` = KSA's own music only, `off` = silence).
- `fadeOut`: how many seconds a playlist takes to fade out when the situation changes.

## Works with

- **[Kitten Main Menu](https://github.com/ghost225/KSAMainMenu)** (optional): music on its main menu and credits screen.

For other mods: `KSAMusic.Api.SetScene(name)` forces a scene (`null` for automatic); `CurrentPlaylist` and `CurrentTrack` say what's playing.

## Building

```sh
dotnet build KSAMusic -c Release -p:KsaDir=/path/to/KSA
```

The music library is rebuilt from its sources with `tools/build_library.py`. `tools/playlist_editor.py` runs the editor outside the game for preparing the preset (`pack/assignments.json`), and `tools/make_release.py <version>` builds a release zip with the preset, the music it ships and its credits.

## Credits

- Music: Askerad (Transfer Windows) and Kevin MacLeod; see `CREDITS.md`.
- Kitten Space Agency by RocketWerkz; its own music is played from your install and isn't included.
- The playlist format and rules follow Soundtrack Editor Forked, so music packs made for KSP work.
- StarMap and Harmony.

## License

The code is MIT (see `LICENSE`). The music keeps its own licences: CC BY 3.0 (Askerad), CC BY 4.0 (Kevin MacLeod), and CC0 for the silence track.
