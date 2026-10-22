# Command-line tool (mflash)

`mflash` is the command-line version of MortisFlash. It uses the same engine as the desktop application to print
information and decompiled scripts, export a file's contents to a folder, and render timeline frames to PNG. It is
suited to scripts and batch processing; it does not edit files.

## Getting mflash

Download `mflash-<version>-win-x64.exe` from the
[latest release](https://github.com/Nekuzaky/Mortis-flash-decompiler/releases/latest) and rename it to `mflash.exe`, or put it
in a folder listed in your `PATH` under that name. It is a portable single file that includes the .NET runtime.

`mflash` is built from the `src/MortisFlash.Cli` project. You can also publish it yourself (`dist\mflash.exe`) as
described in [building.md](building.md#publishing-portable-executables), or run it from the source tree:

```
dotnet run --project src/MortisFlash.Cli -- info game.swf
```

Running `mflash` without arguments prints the usage summary.

## Synopsis

```
mflash info    <file.swf>
mflash scripts <file.swf> [filter] [--pcode]
mflash export  <file.swf> [folder] [parts]
mflash frame   <file.swf> <n> <output.png>
mflash sheet   <file.swf> <output.png>
```

- The command name comes first and is case-sensitive (`info`, not `Info`).
- `<file.swf>` is always the second argument. It can be an uncompressed (`FWS`), zlib (`CWS`) or LZMA (`ZWS`) SWF,
  or a Flash projector (`.exe`), from which the SWF is extracted.
- Output is written in UTF-8, and numbers use the English format (for example `15,542 bytes`), whatever the system
  language.

## Commands

### info

```
mflash info <file.swf>
```

Prints a summary of the file: name, SWF version, compression (`None`, `Zlib` or `Lzma`), uncompressed size, stage
size, frame rate, frame count, ActionScript version, background color, the number of items of each kind, the frame
labels of the main timeline, and the number of tags of each type. This is the text of the Overview page of the
application and of `info.txt` in an export.

Example (abbreviated):

```
> mflash info debug_game_as3_scoring.swf
File: debug_game_as3_scoring.swf
SWF version: 15
Compression: Zlib
Uncompressed size: 15,542 bytes
Stage: 550 × 400 px
Frame rate: 24 fps
Frames: 1
ActionScript: 3 (AVM2)
Background: #FFFFFF

Scripts: 1
Images: 0
Sounds: 0
Shapes: 15 (+ 0 morph)
...

Tags:
  DefineShape4                     15
  PlaceObject2                      9
  ...
```

### scripts

```
mflash scripts <file.swf> [filter] [--pcode]
```

Prints the decompiled scripts. Each script starts with a line `==== <path>`, followed by its source.

| Argument | Description |
|---|---|
| `filter` | Only print the scripts whose path contains this text, ignoring case. It must be the third argument, before `--pcode`. |
| `--pcode` | Print the p-code (bytecode listing) instead of the source. For an ActionScript 3 class, each method starts with a comment line `// <name>  (method <index>)` and a line giving its maximum stack, local registers and scope depths. |

Script paths follow the structure shown in the application's tree, prefixed with `scripts/`:

| Script | Path |
|---|---|
| ActionScript 3 class | `scripts/<package folders>/<Class>.as` |
| Frame script | `scripts/frame_<n>/DoAction` (`DoAction_2`, … for further scripts of the same frame) |
| Frame script of a sprite | `scripts/DefineSprite_<id>[_<symbol name>]/frame_<n>/DoAction` |
| Clip event handler | `scripts/frame_<n>/PlaceObject_<id>_<name>_depth<d>/onClipEvent(<event>)` |
| Button handler | `scripts/DefineButton_<id>/on(<event>)` |
| ActionScript 2 class | `scripts/__Packages/<package folders>/<Class>` |
| Other initialization actions | `scripts/DefineSprite_<id>[_<symbol name>]/DoInitAction` |

Examples:

```
mflash scripts game.swf                              all scripts
mflash scripts game.swf MainTimeline                 scripts whose path contains "MainTimeline"
mflash scripts game.swf frame_1 --pcode              p-code of the scripts of frame 1
mflash scripts game.swf > game_scripts.as            save everything to a file
```

Warnings are written to the standard error stream, so redirecting the standard output captures the code only.

### export

```
mflash export <file.swf> [folder] [parts]
```

Exports the contents of the file to a folder.

| Argument | Description |
|---|---|
| `folder` | Output folder, created if needed. Default: a folder named `<file name without extension>_export` next to the input file. |
| `parts` | Comma-separated list of the parts to export (see below). Default: everything except p-code. |

The third argument is taken as the folder unless it contains a comma or is a part name. To export to a folder whose
name is also a part name, write it as a path, for example `.\images`. Part names are not case-sensitive; several
lists can be given as separate arguments; unknown names inside a list are ignored.

| Part | Exports |
|---|---|
| `scripts` | Decompiled scripts (`.as`). |
| `pcode` | P-code listings (`.pcode`); implies `scripts`. |
| `images` | Images as JPEG, PNG or GIF. |
| `sounds` | Sounds as MP3 or WAV; Nellymoser and Speex data as stored. |
| `shapes` | Shapes and morph shapes as SVG. |
| `sprites` | First frame of each sprite as PNG. |
| `fonts` | Glyph sheets (SVG), font information (TXT) and embedded OpenType fonts (OTF). |
| `texts` | Texts as plain text, plus the HTML source of HTML text fields. |
| `binary` | Binary data, with the detected extension. |
| `videos` | Videos rebuilt as FLV. |
| `frames` | Frames of the main timeline as PNG. |
| `all` | Everything above except p-code. |

While it runs, `export` prints one line per step (`  Scripts…`, `  Images…`, …), then a summary such as
`237 files exported to out (0.2 s)` and the first 20 errors, if any, prefixed with `  !`. Items that cannot be
exported are skipped; all errors are written to `errors.txt` in the output folder. Existing files with the same names
are overwritten; other files in the folder are left in place.

Examples:

```
mflash export game.swf                               everything, to game_export\ next to game.swf
mflash export game.swf out                           everything, to out\
mflash export game.swf images,sounds                 images and sounds, to game_export\
mflash export game.swf out scripts,pcode             scripts and p-code, to out\
mflash export loader.exe                             a projector: its SWF is exported
```

#### Export folder layout

```
<folder>/
    info.txt                          always written
    scripts/<script path>.as
    pcode/scripts/<script path>.pcode
    images/<id>[_<name>].jpg | .png | .gif
    sounds/<id>[_<name>].mp3 | .wav | .nellymoser | .speex | .bin
    shapes/<id>[_<name>].svg
    morphshapes/<id>[_<name>]_start.svg
    morphshapes/<id>[_<name>]_end.svg
    sprites/<id>[_<name>].png
    fonts/<id>[_<name>][_<font name>].svg | .txt | .otf
    texts/<id>[_<name>].txt
    texts/<id>[_<name>].html          HTML text fields only
    binaryData/<id>[_<name>].<detected extension>
    videos/<id>[_<name>].flv
    frames/0001.png, 0002.png, ...
    errors.txt                        only when there were warnings or errors
```

Naming rules:

- `<id>` is the character ID. `<name>` is the symbol name (from `ExportAssets` or `SymbolClass` tags), present only
  when the file defines one.
- Names are made safe for the file system: characters other than letters, digits, spaces and `_ - . ( ) ,` are
  replaced with `_`, and names are limited to 80 characters.
- Script files keep the script path and receive the `.as` extension, for example `scripts/frame_1/DoAction.as` or
  `scripts/DefineButton_6/on(press).as`.
- Stream sounds are named after their timeline, for example `sounds/stream _ main timeline.mp3`.
- A text bound to a variable ends with a `[variable: <name>]` line in its `.txt` file. The `.txt` file of an HTML
  text field contains the text without its tags.
- Font `.txt` files list the font name, style, glyph count and characters.
- Frames are rendered at stage size with the background color; only the first 500 frames are exported. Sprites are
  rendered up to 1024 pixels on their longest side.
- Folders are only created for the parts that produce files.

### frame

```
mflash frame <file.swf> <n> <output.png>
```

Renders frame `n` of the main timeline to a PNG file at stage size, with the background color, and prints
`Frame <n> → <output.png> (<width>×<height>)`. Frames are numbered from 1; a value below 1 renders frame 1. `n` must
be an integer.

### sheet

```
mflash sheet <file.swf> <output.png>
```

Renders a contact sheet of the main timeline: the first 120 frames at 35 % of the stage size, six per row, each
labeled with its frame number, on a dark gray background. Prints `<count> frames → <output.png>`.

### Rendering limits

`frame`, `sheet` and the `sprites` and `frames` export parts render the display list of the timeline without running
ActionScript, so content created by code does not appear. Masks, color transforms and morph shapes are rendered;
filters and blend modes are not applied.

## Warnings

After the command has run, the problems found while reading the file (unreadable items, truncated data) are printed
to the standard error stream, one per line, prefixed with `! `. They do not change the exit code.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | The command completed. Warnings and items that could not be exported do not change this code. |
| `1` | The usage summary was printed: fewer than two arguments, an unknown command, or missing arguments for `frame` (three needed after the command) or `sheet` (two needed). |
| `2` | The input file could not be opened: missing file, not a SWF or projector, or an encrypted SWF. The reason is printed to the standard error stream as `Cannot open file: <reason>`. |
| Other non-zero | An unexpected error, reported by the .NET runtime as `Unhandled exception.` followed by details, for example a frame number that is not an integer or an output path that cannot be written. |

The input file is opened before the command name is checked, so an unknown command used with an unreadable file
returns `2`.

## Batch processing

The following PowerShell examples assume that `mflash.exe` is in a folder listed in the `PATH` environment variable.
Export every SWF of the current folder:

```powershell
Get-ChildItem *.swf | ForEach-Object { mflash export $_.FullName }
```

Print the scripts whose path contains `MainTimeline`, file by file:

```powershell
Get-ChildItem *.swf | ForEach-Object { "## $($_.Name)"; mflash scripts $_.FullName MainTimeline }
```

## See also

- [User guide](user-guide.md)
- [Editing a SWF](editing.md)
- [Architecture](architecture.md)
- [Building](building.md)
- [Project README](../README.md)
