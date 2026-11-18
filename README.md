<p align="center">
  <img src="docs/banner.png" alt="LGMods Map Doctor" width="100%">
</p>

<p align="center">
  <a href="https://github.com/LGModsFiveM/MapDoctor/releases/latest/download/LGModsMapDoctor.exe"><img src="https://img.shields.io/github/v/release/LGModsFiveM/MapDoctor?label=Download&style=for-the-badge&color=5AD2C9&labelColor=0D1A1D" alt="Download"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-5AD2C9?style=for-the-badge&labelColor=0D1A1D" alt="Windows 10 and 11">
  <img src="https://img.shields.io/badge/Price-Free-5AD2C9?style=for-the-badge&labelColor=0D1A1D" alt="Free">
  <a href="https://discord.gg/lgmods"><img src="https://img.shields.io/badge/Discord-Get%20help-5AD2C9?style=for-the-badge&labelColor=0D1A1D&logo=discord&logoColor=white" alt="Discord"></a>
</p>

<p align="center">
  <b>See which maps on your FiveM server are fighting over the same files,<br>
  what your players will notice, and what to do about it.</b>
</p>

<p align="center">
  <a href="https://github.com/LGModsFiveM/MapDoctor/releases/latest/download/LGModsMapDoctor.exe"><b>Download Map Doctor</b></a>
  &nbsp;·&nbsp;
  <a href="https://store.lgmods.co.uk/">LGMods store</a>
  &nbsp;·&nbsp;
  <a href="https://discord.gg/lgmods">Discord</a>
</p>

---

## Why you need it

Install enough maps and sooner or later two of them ship a file with the same name. FiveM only loads one
of them, so the other map breaks in ways that are hard to trace back:

- players fall through floors or walk through walls
- buildings vanish or flicker as you drive past
- interiors load empty or in the wrong place
- distant lights, trees or props go missing

Map Doctor scans your whole server in a few seconds, finds every clash, and tells you in plain English
which maps are involved and what to do. No modding knowledge needed.

## Get started

1. **[Download `LGModsMapDoctor.exe`](https://github.com/LGModsFiveM/MapDoctor/releases/latest/download/LGModsMapDoctor.exe)**. It's a single file, there's nothing to install.
2. **Open it** and drop your server folder onto the window (the one with `server.cfg` in it). Your `resources` folder works too.
3. **Read the results.** Problems players will notice are at the top, things worth checking come after.
4. **Resolve** anything you want sorted, or **Save report** to keep a copy or send it to a map maker.

> [!NOTE]
> The first time you run it, Windows may say it *protected your PC*. That's SmartScreen being cautious
> with a new download. Click **More info**, then **Run anyway**.

Server hosted on Linux or a game panel? Download your server folder to your PC and scan the copy.

## What it checks

| What your players notice | What Map Doctor finds |
|---|---|
| Falling through floors, walking through walls | Two maps with different collision for the same spot |
| Buildings disappearing or flickering | Clashing occlusion files |
| Missing lights, trees or props in the distance | Clashing LOD and detail files |
| Invisible objects, interiors in the wrong place | Missing model lists, or two interiors built in the same spot |
| Locals missing or standing in odd places | Clashing scenario files |
| A map not loading at all | Missing dependencies, duplicate folders, wrong game version, maps switched off |
| Slow loading | Very large streamed files |

It also spots clashes another resource is already fixing, harmless identical copies (so you don't
worry about them), and start-up lines in `server.cfg` that point at resources that aren't there.

## Fixing clashes

When two maps clash, the **Resolve** screen lets you pick which map should win in each spot.

For some files Map Doctor can combine both versions, so every map keeps working. Combined files go
in their own resource, `LGMods_MapDoctor`, and your maps aren't touched. It needs to start after your
maps, so Map Doctor can add `ensure LGMods_MapDoctor` to the end of your `server.cfg` for you, or you
can add it yourself.

Your files are safe:

- **Nothing changes until you confirm.** Scanning only reads your files.
- **Everything is backed up.** Files that get taken out are moved to a backup folder outside
  `resources`, so FiveM never loads them, along with a note of what changed.
- **You can undo it.** Open **Undo fixes** to put any change back exactly how it was. Once nothing
  combined is left, `LGMods_MapDoctor` and its `server.cfg` line are removed too.

Restart your server afterwards so players get the change. If the server is running while you fix,
Windows may not let Map Doctor move the files, so stop it first.

## Privacy

No login, no account, nothing uploaded. Map Doctor runs entirely on your PC.

## Need a hand?

Join the [LGMods Discord](https://discord.gg/lgmods). The **Copy for support** button in the app copies
everything we need to help you. If a clash needs the map makers to sort out, **Message map makers**
writes the message for you.

## Licence

Map Doctor is open source under the [MIT licence](LICENSE). The LGMods name and logo are not covered
by the licence.

<p align="center">
  <br>
  <img src="src/LGMods.MapDoctor.App/Assets/logo.png" alt="LGMods" height="48"><br>
  <sub>Made by <a href="https://store.lgmods.co.uk/">LGMods</a>. Free for every FiveM server.</sub>
</p>

---

<details>
<summary><b>For developers</b></summary>

<br>

Built with .NET 9 and WPF.

| Folder | What it is |
|---|---|
| `src/LGMods.MapDoctor.Core` | The scanner: finds resources, reads `server.cfg` and `fxmanifest.lua`, runs the checks, builds the HTML report. No UI. |
| `src/LGMods.MapDoctor.App` | The desktop app (`LGModsMapDoctor.exe`). |
| `src/LGMods.MapDoctor.Cli` | Command-line scan, for support and testing. |
| `tests/LGMods.MapDoctor.Tests` | Automated tests using small fake servers. |

Run the app:

```
dotnet run --project src/LGMods.MapDoctor.App
```

Command line:

```
dotnet run --project src/LGMods.MapDoctor.Cli -- "C:\path\to\server-data" --details --html report.html
```

Screenshots of every screen:

```
LGModsMapDoctor.exe --snapshot "C:\path\to\server-data" "C:\path\to\output-folder"
```

Tests:

```
dotnet test
```

Releasing: push a version tag and GitHub Actions builds the exe and attaches it to a release.

```
git tag v0.1.0
git push origin v0.1.0
```

Every finding answers what players notice, where, and what to do. File names only appear under
"Show details", and titles avoid jargon (ymap, ybn, occlusion, LOD, escrow).

Brand: background `#070F11`, surface `#0D1A1D`, teal `#5AD2C9`, text `#E8F3F2`, Bebas Neue for headings
and Inter for text, falling back to Bahnschrift and Segoe UI.

</details>
