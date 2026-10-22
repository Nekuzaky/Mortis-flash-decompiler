# MortisFlash user guide

This guide explains how to install MortisFlash, open Flash files, browse and search their contents, run the game in
the built-in player and export everything to a folder. Modifying a file is covered in [editing.md](editing.md), and
the `mflash` command-line tool in [command-line.md](command-line.md).

Only decompile files that you own or that you have permission to analyze.

## Installing

MortisFlash is distributed as a single portable executable.

1. Download `MortisFlash-<version>-win-x64.exe` from the latest release:
   <https://github.com/Nekuzaky/Mortis-flash-decompiler/releases/latest>. `SHA256SUMS.txt`, on the same page, lists the checksums
   of the executables.
2. Put it in any folder (you can rename it, for example to `MortisFlash.exe`) and run it. There is no installer, and
   the .NET runtime does not need to be installed: it is included in the executable.

Requirements:

- Windows 10 or Windows 11, 64-bit (x64).
- The Microsoft Edge WebView2 runtime, **only for the embedded player** (the **Play** button). It is built into
  Windows 11 and most up-to-date Windows 10 installations. Browsing, exporting and editing work without it. When it
  is missing, the game panel shows "WebView2 runtime missing" and an **Install WebView2…** button that opens
  Microsoft's WebView2 page; restart MortisFlash after installing the runtime.

The executable is not code-signed, so Windows SmartScreen may show "Windows protected your PC" the first time you run
it. Click **More info**, then **Run anyway**.

MortisFlash writes to two folders outside its own location:

| Folder | Content |
|---|---|
| `%LOCALAPPDATA%\MortisFlash\WebView2` | Data of the embedded player (WebView2 user data folder). |
| `%TEMP%\MortisFlash` | Sounds and videos opened with a **Play** button in the preview. |

To remove MortisFlash, delete the executable and these two folders.

## Opening files

MortisFlash opens:

- SWF files, uncompressed (`FWS`), zlib-compressed (`CWS`) or LZMA-compressed (`ZWS`);
- Flash projectors (`.exe`): the SWF stored inside the executable is extracted, the executable itself is never run.

There are four ways to open a file:

- Drag and drop it onto the MortisFlash window.
- Click **Open…** in the top bar (or **Open file…** on the start page) and choose the file. The dialog lists
  `*.swf`, `*.spl` and `*.exe` files by default; select "All files" to pick any other file.
- Press `Ctrl+O`.
- Pass the file on the command line, for example `MortisFlash.exe "C:\Games\game.swf"`. The first argument that is an
  existing file is opened at startup, which also covers dropping a file onto the executable's icon and "Open with".

If the file cannot be read, the status bar shows "Could not open this file:" followed by the reason. SWF files
encrypted with the Harman AIR SDK are recognized but not supported. When only some items of a file are unreadable,
the rest is still available and the problems are listed under **Warnings** in the tree.

## Window layout

| Area | Description |
|---|---|
| Top bar | File name, SWF version, ActionScript version (1/2 or 3), stage size, frame rate and frame count. Buttons: **← Parent SWF** (only inside a nested SWF), **Open…**, **Save SWF…** (only after a change), **▶ Play** and **Export all…**. |
| Tree (left) | The **Filter by name…** box, the **Also search in code** check box and the list of scripts and assets. The splitter on its right edge resizes it. |
| Preview (center) | A header with the selected item's name, details and actions (buttons), and the preview itself below. |
| Game panel (right) | Shown while **Play** is on: the game, its status, the **Restart**, **Capture** and **Clear** buttons, and the console with the game's `trace()` output. Splitters resize the panel and the console. |
| Status bar (bottom) | Messages and progress, the **Open folder** button after an export, and a reminder about the ownership of extracted content. |

The window title shows the file name, followed by "• modified" once the file has been changed.

## Browsing scripts and assets

Select an item in the tree to preview it. After a file is opened, the preview shows its overview.

| Tree node | Preview | Actions |
|---|---|---|
| Overview | Summary of the file: version, compression, sizes, item counts, frame labels, tag statistics. | **Export all…** |
| Timeline | The main timeline, rendered frame by frame, with a slider, a play button (`▶`) and the frame number. Frame labels appear in the header. | **Export frame as PNG…** (current frame, at twice the stage size) |
| Scripts | Decompiled ActionScript with syntax highlighting. | **Edit**, **P-code** / **ActionScript** (toggle), **Copy**, **Save…** |
| Images | The image, with its format and size. | **Replace…**, **Save…** |
| Sounds | Codec, sample rate, channels, duration and size. | **Play** (MP3 and WAV), **Replace…**, **Save…** |
| Shapes, Morph shapes | Rendered shape. | **Export as SVG…**, **Export as PNG…** |
| Sprites | Rendered sprite, with a frame slider when it has several frames. | **Export as PNG…** |
| Buttons | Rendered button. | |
| Fonts | Sheet of the font's glyphs, each labeled with its character. | **Save…** (embedded OpenType fonts, `DefineFont4`) |
| Texts | Text fields and static texts, with an editor. | **Save…** (plain text) |
| Binary data | Image preview, text preview (XML, JSON, text) or hexadecimal dump of the first 64 KB. | **Open this SWF** (nested SWFs), **Replace…**, **Save…** |
| Videos | Codec, size and frame count. The video is rebuilt as an FLV file. | **Play**, **Save…** |
| Tags | Every tag of the file with its offset, size, character ID and symbol name. | |
| Warnings | Items that could not be read completely. Only present when there are any. | |

Notes:

- Items are labeled with their character ID, followed by their symbol name when the file defines one (for example
  "Image 12 — logo").
- **Play** on a sound or a video writes it to a temporary file and opens it in the program associated with its type
  in Windows. Nellymoser and Speex sounds are saved as they are stored in the file; a tool such as ffmpeg can convert
  them.
- Stream sounds (timeline music) are listed under Sounds as "stream — main timeline" or "stream — sprite N".
- Binary data is labeled with the detected type: `.swf`, `.png`, `.gif`, `.jpg`, `.zip`, `.mp3`, `.ogg`, `.wav`,
  `.flv`, `.amf`, `.xml`, `.json`, `.txt`, or `.bin` when the type is unknown.
- Previews are rendered by MortisFlash itself, without running ActionScript: content that the game creates from code
  does not appear, and filters and blend modes are not applied.

### Scripts

Scripts are grouped in folders that follow the structure of the file:

| Script | Location in the tree |
|---|---|
| ActionScript 3 class | Its package path, for example `as3_scoring_fla` > `MainTimeline.as`. |
| Frame script of the main timeline | `frame_N` > `DoAction` (`DoAction_2`, … when a frame has several). |
| Frame script of a sprite | `DefineSprite_ID` (followed by the symbol name, if any) > `frame_N` > `DoAction`. |
| Clip event handler | `frame_N` > `PlaceObject_ID_NAME_depthD` > `onClipEvent(...)`. |
| Button handler | `DefineButton_ID` > `on(...)`. |
| ActionScript 2 class | `__Packages` > package folders > class name. |
| Other initialization actions | `DefineSprite_ID` > `DoInitAction`. |

A script is decompiled the first time it is displayed. The **P-code** button switches to the bytecode listing and
**ActionScript** switches back. **Copy** and **Save…** use the view currently displayed.

Some notations come from the decompiler rather than from the original source:

- An identifier that is not valid ActionScript (for example an obfuscated name) is shown between section signs:
  `§name§`.
- Code that could not be rebuilt into structured statements is shown with placeholders that start with `§§`, such as
  `§§goto(addr00A4);`. The P-code view always shows the exact bytecode.

## Filtering and searching in code

Type in **Filter by name…** (or press `Ctrl+F` to move the cursor there). The tree is filtered shortly after you stop
typing: it keeps the items whose label, or whose path for scripts, contains the text, ignoring case. The Overview,
Timeline, Tags and Warnings nodes are hidden while a filter is active.

Check **Also search in code** to search inside the scripts. With at least two characters in the box, MortisFlash
decompiles every script in the background and keeps those whose path or decompiled source contains the text,
ignoring case. Only scripts are shown, and the status bar reports how many contain the text. Changing the text
restarts the search. The search covers the ActionScript source, not the p-code.

## Nested SWFs

Games are often wrapped in a loader that stores the real game as binary data. When a binary data item contains a SWF,
its preview header shows **Open this SWF**. Click it to open the inner SWF in place: the top bar shows the parent
file's name followed by "› data N.swf", and **← Parent SWF** appears. Nested SWFs can themselves contain SWFs; each
**← Parent SWF** goes back one level.

Changes made inside a nested SWF are not written back into its parent; see
[Nested SWFs in editing.md](editing.md#nested-swfs) for the procedure.

## Playing the game

Click **▶ Play** to open the game panel and run the SWF in the embedded player (Ruffle). The button becomes
**■ Stop**; click it to stop the game and close the panel. **Play** is disabled when the player is not available on
the system.

- The status next to the "Game" title shows "Loading…", then the stage size, frame rate and SWF version once the
  game is loaded, or "Error".
- **Restart**, or `F5`, reloads the game from the beginning.
- **Capture** saves a PNG screenshot of the player area (the suggested name is `<file>_capture.png`).
- The console below the game shows the game's `trace()` output. A line "— Running <file> —" marks each start, and
  "— Running modified version of <file> —" each start after a change. Player errors are prefixed with `!`.
  **Clear** empties the console; when it grows very large, only the most recent output is kept.
- Sound starts without a click in the game.
- The game is restarted automatically after each change made to the file (see [editing.md](editing.md)).

Files next to the SWF: when the game loads a file by relative path (another SWF, XML, text, JSON, an image, an MP3),
the player serves it from the folder that contains the file you opened, including its subfolders. Files outside that
folder are never served. Keep the game's files together, as they were distributed, so that it can find them.

Links: web pages opened by the game (`getURL`, `navigateToURL`) are blocked; nothing is opened, neither in
MortisFlash nor in the web browser. MortisFlash only serves the local game and its files; requests that the game
sends to Internet servers are not blocked by MortisFlash.

Playback relies on Ruffle, a Flash Player emulator, so its compatibility limits apply.

## Exporting

Click **Export all…** in the top bar (or in the Overview header) and choose a folder. MortisFlash creates a
`<file name>_export` folder inside it and writes every script, its p-code, and every asset into it. The status bar
shows the progress, then the number of files written; **Open folder** opens the result in Explorer. When some items
could not be exported, the status bar says so and the details are written to `errors.txt`.

Layout of the export folder:

```
<file name>_export/
    info.txt                       summary of the file (same text as the Overview)
    scripts/...                    decompiled ActionScript (.as), same structure as the tree
    pcode/scripts/...              p-code listings (.pcode)
    images/                        JPEG, PNG or GIF files
    sounds/                        MP3, WAV, or raw Nellymoser / Speex data
    shapes/                        SVG files
    morphshapes/                   SVG files of the start and end states
    sprites/                       PNG renderings (first frame)
    fonts/                         glyph sheets (SVG), font information (TXT), OpenType fonts (OTF)
    texts/                         plain text (TXT), and the HTML source of HTML text fields
    binaryData/                    raw data, with the detected extension
    videos/                        FLV files
    frames/                        main timeline frames: 0001.png, 0002.png, ... (first 500 frames)
    errors.txt                     only when warnings or export errors occurred
```

Folders are only created when the file contains items of that kind. File names start with the character ID, followed
by the symbol name when there is one. [command-line.md](command-line.md#export-folder-layout) describes the naming in
detail.

Single items can also be saved from their preview header (**Save…**, **Export as SVG…**, **Export as PNG…**,
**Export frame as PNG…**).

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+O` | Open a file. |
| `Ctrl+F` | Move the cursor to the **Filter by name…** box. |
| `Ctrl+S` | **Save SWF…** (only after the file has been changed). |
| `F5` | Restart the game (only while the game panel is open). |

## See also

- [Editing a SWF](editing.md)
- [Command-line tool](command-line.md)
- [Architecture](architecture.md)
- [Building](building.md)
- [Project README](../README.md)
