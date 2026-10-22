# Changelog

All notable changes to MortisFlash are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project uses [Semantic Versioning](https://semver.org/).

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
