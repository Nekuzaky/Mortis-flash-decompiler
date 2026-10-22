# Changelog

All notable changes to MortisFlash are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Performance

- Images and sounds are decoded when they are first shown or exported instead of when the file is opened: opening a
  game with many images is several times faster and uses less memory (Incubattle: 975 ms → 218 ms, 279 MB → 185 MB).
- Stopping the game hides the player instead of destroying it, and one browser environment is shared: **Play** after
  **Stop** shows the game in about 0.3 s instead of 0.7 s.
- Ruffle is extracted once to `%LOCALAPPDATA%\MortisFlash\Ruffle` and read by WebView2 directly, instead of being
  streamed through the UI thread on every load.
- Previews and timeline playback copy pixels directly instead of encoding and decoding a PNG for every frame.
- The console batches `trace()` output (at most ten updates per second) and keeps its text short.
- Published executables are precompiled (ReadyToRun): the app window appears about 30 % sooner.

### Player

- The game receives the keyboard as soon as it is loaded, so its controls respond without clicking it first.
- Browser shortcuts are disabled inside the game (no "Save page as" on Ctrl+S, no page reload on F5); F5, Ctrl+O and
  Ctrl+S are forwarded to the app.

## [1.0.0] — 2026-10-22

First public release.

### Decompilation

- SWF reader for `FWS`, `CWS` (zlib) and `ZWS` (LZMA) files, and extraction of SWFs embedded in Flash projector executables.
- ActionScript 3 decompiler: packages, classes, interfaces, namespaces, fields, constructors, accessors, metadata, imports,
  every control structure including `switch`, `for each` and `try`/`catch`/`finally`, short-circuit conditions and closures.
- ActionScript 1/2 decompiler: frame scripts, sprite timelines, clip and button handlers, `DoInitAction`, `switch`,
  `for…in`, `with`, `try`, and AS2 classes rebuilt from their prototype code.
- One structuring engine shared by both decompilers (dominators, post-dominators, natural loops, condition merging).
- P-code listing for every script.

### Editing

- Built-in ActionScript compilers: AS3 method bodies, AS1/AS2 scripts and AS2 classes, with compile errors pointing to the line.
- P-code assemblers for AVM1 and AVM2; the ABC writer reproduces unmodified bytecode byte for byte.
- Replacement of images (PNG, JPEG, GIF), event sounds and stream sounds (MP3, WAV) and binary data.
- Editing of text fields and static texts (static texts use the glyphs embedded in their font).
- Modified files are saved as a new SWF.

### Assets and export

- Images, sounds, shapes and morph shapes (SVG), sprites and buttons (PNG), fonts, texts, binary data and video (FLV).
- Timeline rendering with masks, color transforms and morphs; frame export and contact sheets.
- Export of everything, or of selected parts, to a folder.

### Desktop app

- Avalonia interface in the Mortis dark style: file tree with name and code search, code view with syntax highlighting,
  previews for every asset type.
- Embedded Ruffle player (WebView2) with a `trace()` console, restart after every edit and screenshot capture.
- Prompt to install the WebView2 runtime when it is missing.
- Distributed as a single portable executable with Ruffle embedded.

### Command line

- `mflash` with the `info`, `scripts`, `export`, `frame` and `sheet` commands.

[1.0.0]: https://github.com/Nekuzaky/Mortis-flash-decompiler/releases/tag/v1.0.0
