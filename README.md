# MortisFlash

MortisFlash is a Flash (SWF) decompiler and editor for Windows, written in C# (.NET 10, Avalonia 12, SkiaSharp).
It opens `.swf` files and Flash projectors (`.exe`), reconstructs readable ActionScript 1, 2 and 3, extracts every
asset, and can run and modify the game in place.

> **Use responsibly.** Only decompile files you own, or for preservation and learning. Extracted code and assets
> remain the property of their authors: do not redistribute them.

## Features

| Area | Details |
|---|---|
| **ActionScript 3** (AVM2) | Packages, classes, interfaces, fields with initializers, constructors, getters/setters, metadata, imports; `if/else`, `while`, `do…while`, `for`, `for…in`, `for each`, `switch`, `try/catch/finally`, `&&`/`\|\|`, ternaries, closures. Debug names are used when present. |
| **ActionScript 1/2** (AVM1) | Frame scripts, sprite timelines, `onClipEvent`/`on()` handlers, buttons, `DoInitAction`, reconstructed AS2 classes (`class X extends Y { … }`, accessors, statics), `switch`, `for…in`, `with`, `try`. |
| **P-code** | Bytecode listing for every script (toggle in the app, `--pcode` on the command line). |
| **Images** | JPEG (with `JPEGTables` merged and broken markers repaired), JPEG + alpha → PNG, lossless 8/15/24/32-bit → PNG, PNG/GIF. |
| **Sounds** | MP3 as-is, PCM and ADPCM decoded to WAV, stream sounds rebuilt, Nellymoser/Speex exported raw. |
| **Vector graphics** | Shapes and morph shapes → SVG (gradients, bitmap fills); sprites, buttons and static texts rendered to PNG. |
| **Timeline** | Frame-by-frame rendering with masks, color transforms and morphs; playback in the app. |
| **Fonts and texts** | Glyph sheets, embedded OpenType fonts (DefineFont4), static and dynamic texts (HTML stripped). |
| **Other data** | Binary data with type detection (nested SWFs can be opened), video → FLV, full tag list. |
| **Containers** | `FWS`, `CWS` (zlib) and `ZWS` (LZMA) SWFs, and Flash projector executables. |
| **Playback** | The game runs inside the app (Ruffle, embedded through WebView2) with a console for its `trace()` output, and reloads after every edit. |
| **Source editing** | Edit the decompiled ActionScript and recompile it: AS3 one method body at a time, AS1/AS2 whole scripts and AS2 classes. Built-in compilers, no Flex SDK or Java needed. |
| **Asset editing** | Replace images (PNG/JPEG/GIF), sounds and stream sounds (MP3/WAV), binary data; change text fields and static texts; edit the p-code of any script; save the modified `.swf`. |

**Not supported:** encrypted SWFs (for example Harman AIR encryption), filters and blend modes in previews.
The compilers cover the language used by games — expressions, all statements, `try/catch/finally`, closures — but not
E4X, `with` in AS3, or closures that capture local variables of the enclosing AS3 method; for those, edit the p-code.

## Desktop app

```bash
dotnet run --project src/MortisFlash
```

### Browsing and exporting

- Drop a `.swf` file (or a projector `.exe`) onto the window, or use **Open…** (`Ctrl+O`).
- The tree on the left lists scripts and assets. **Filter by name** (`Ctrl+F`) narrows it down; **Also search in
  code** decompiles every script and keeps those that contain the text.
- **Export all…** writes everything to a `<name>_export/` folder.

### Playing and editing

**Play** opens the game panel on the right. `F5` restarts the game and **Capture** saves a screenshot.

To change the game:

- On an image, sound, stream sound or binary data item, use **Replace…**. Stream sounds are cut into one block per
  frame at the movie's frame rate; MP3 must be 11, 22 or 44 kHz, WAV 16-bit PCM.
- On a text, edit it and click **Apply**. Static texts keep their font, size and color and can only use the
  characters embedded in their font (the editor lists them).
- On a script, **Edit** opens the editor in **ActionScript** mode: change the decompiled code and click **Apply and
  test** to recompile it. For AS3, pick the method first; its signature stays unchanged. Compile errors point to the
  line. **P-code** mode edits the bytecode listing instead (always available, including for code the decompiler
  could not fully rebuild): labels such as `ofs0012:` and `loc00A4:` mark jump targets, and new strings and names
  are added to the constant pool automatically.

Every change rewrites the SWF, then reloads the tree and the running game. **Save SWF…** (`Ctrl+S`) writes the
result to disk. Files next to the original SWF (external movies, XML, etc.) are also served to the game.

### Requirements

Windows 10 or 11 (x64). The embedded player needs the Microsoft Edge WebView2 runtime: it ships with Windows 11 and
most up-to-date Windows 10 PCs; when it is missing, the game panel offers a link to Microsoft's installer. Everything
else (browsing, exporting, editing) works without it.

## Command line

```bash
dotnet run --project src/MortisFlash.Cli -- export game.swf out/
```

| Command | Description |
|---|---|
| `mflash info <file>` | Summary (version, compression, counts, tag statistics) |
| `mflash scripts <file> [filter] [--pcode]` | Print decompiled scripts |
| `mflash export <file> [folder] [parts]` | Export everything, or only the given parts (`scripts,pcode,images,sounds,shapes,sprites,fonts,texts,binary,videos,frames`) |
| `mflash frame <file> <n> <out.png>` | Render one timeline frame |
| `mflash sheet <file> <out.png>` | Render a contact sheet of the timeline |

## Architecture

```
src/MortisFlash.Core   Engine: SWF/LZMA reader, assets, AVM1 and AVM2 decompilers, renderer, exporter
src/MortisFlash.Cli    mflash command-line tool
src/MortisFlash        Avalonia desktop app (Mortis dark theme), embedded Ruffle player in Ruffle/
```

**Decompilation.** Both decompilers share a single structuring engine (`Decompiler/Structurer.cs`): control flow
graph, dominators and post-dominators, natural loops, short-circuit condition merging, followed by cleanup passes
(`Simplifier.cs`). Code that cannot be structured is emitted as `§§goto(...)` rather than failing.

**Compilation.** `Compiler/Parser.cs` parses ActionScript into the same syntax tree the decompiler prints;
`Avm1Compiler` and `Avm2Compiler` turn it into p-code, which the assemblers turn into bytecode. In AS3, names that are
not locals are resolved at run time through the class's namespace set, as the Flex compiler does for untyped code, so a
method body compiles without the rest of the project.

**Editing.** All edits go through `Edit/SwfEditor.cs`. The AS2 and AS3 assemblers (`Avm1Assembler`, `Avm2Assembler`)
parse the p-code listing back, and `AbcWriter` and `SwfWriter` rewrite the bytecode and the file. A hidden
`mflash selftest <file>` command checks that the ABC is rewritten byte for byte, that every script survives a
disassemble → assemble round trip, and that decompiled source compiles back to the same code
(on the test suite, among the scripts that compile, about 96 % of AS3 method bodies and 85 % of AS2 scripts come
back identical and most of the others are equivalent rewrites such as `i = i + 1` → `i++`; scripts that do not
compile are mostly the ones the decompiler shows with `§§` placeholders).

## Building

Requires the .NET 10 SDK.

```bash
dotnet build MortisFlash.slnx
```

Warnings are treated as errors and the .NET analyzers are enabled.

Ruffle (MIT / Apache-2.0, https://ruffle.rs) is bundled in `src/MortisFlash/Ruffle/` and embedded in the executable
as resources, which the player serves from memory.

To produce a portable, single-file Windows executable (no installation, nothing to keep next to it):

```bash
dotnet publish src/MortisFlash -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist
```
