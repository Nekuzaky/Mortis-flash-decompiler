# Building MortisFlash

This page explains how to build MortisFlash from source, run the desktop application and the `mflash` tool during
development, publish the portable executables, update the bundled Ruffle player, and set up the test files. The code
structure is described in [architecture.md](architecture.md).

## Prerequisites

- The [.NET 10 SDK](https://dotnet.microsoft.com/download). The projects target `net10.0` and the solution uses the
  `.slnx` format.
- Windows 10 or 11 (x64) with the Microsoft Edge WebView2 runtime to use the desktop application's embedded player.
  The player is the only Windows-specific part: on other systems it reports itself unavailable and **Play** stays
  disabled. The engine (`MortisFlash.Core`) and the command-line tool contain no Windows-specific code.
- Optionally, test SWF files (see [Test SWF files](#test-swf-files)).

## Building

From the repository root:

```
dotnet build MortisFlash.slnx
```

Add `-c Release` for a release build. The outputs are in `src/<project>/bin/<configuration>/net10.0/`.

Shared settings live in `Directory.Build.props` (target framework, nullable reference types, language version,
version number) and package versions in `Directory.Packages.props`. Warnings are treated as errors and the .NET
analyzers run at the latest analysis level, so the build must be free of warnings.

## Running from source

Desktop application, optionally with a file to open at startup:

```
dotnet run --project src/MortisFlash
dotnet run --project src/MortisFlash -- C:\Games\game.swf
```

Command-line tool (arguments after `--` go to `mflash`):

```
dotnet run --project src/MortisFlash.Cli -- info C:\Games\game.swf
dotnet run --project src/MortisFlash.Cli -- export C:\Games\game.swf out
```

## Publishing portable executables

Each tool is published as a single self-contained Windows executable: no installation, no .NET runtime required, and
nothing to keep next to it. Run from the repository root:

```
dotnet publish src/MortisFlash -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist
dotnet publish src/MortisFlash.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist
```

The first command produces `dist\MortisFlash.exe` (the desktop application, with Ruffle embedded), the second
`dist\mflash.exe`.

| Option | Effect |
|---|---|
| `-r win-x64 --self-contained` | Includes the .NET runtime for 64-bit Windows. |
| `-p:PublishSingleFile=true` | Bundles the application into one executable. |
| `-p:IncludeNativeLibrariesForSelfExtract=true` | Bundles the native libraries (such as SkiaSharp's) too; they are extracted when the program starts. |
| `-p:EnableCompressionInSingleFile=true` | Compresses the bundled files. |
| `-p:DebugType=none` | Produces no `.pdb` files for MortisFlash's own assemblies. |
| `-o dist` | Writes the result to `dist/` (git-ignored). |

The SkiaSharp and HarfBuzz packages may still copy native `.pdb` files into `dist`. They are not needed and can be
deleted.

The version number of both executables is the `Version` property of `Directory.Build.props`; it can be overridden
with `-p:Version=<version>`. The executables are not code-signed, which is why Windows SmartScreen warns users on
first launch (see the [user guide](user-guide.md#installing)).

## Continuous integration and releases

Two GitHub Actions workflows run on Windows with the .NET 10 SDK:

- `.github/workflows/build.yml`, on every push to `main`, every pull request, and on demand: builds the solution in
  Release configuration, then runs `mflash` without arguments and checks that it prints its usage and exits with
  code 1.
- `.github/workflows/release.yml`, when a tag starting with `v` is pushed (for example `v1.0.0`): publishes both
  executables with the options above and `-p:Version=` set to the tag without its `v`, names them
  `MortisFlash-<version>-win-x64.exe` and `mflash-<version>-win-x64.exe`, writes their SHA-256 checksums to
  `SHA256SUMS.txt`, and creates the GitHub release with these files. The release notes are
  `.github/release-notes.md` followed by the checksums.

## Updating the bundled Ruffle

The embedded player is the web build of [Ruffle](https://ruffle.rs) (MIT or Apache-2.0), stored in
`src/MortisFlash/Ruffle/`:

| File | Origin |
|---|---|
| `ruffle.js`, `*.wasm` | Ruffle web build. The `.wasm` file names contain a hash that changes with each version. |
| `package.json`, `LICENSE_MIT`, `LICENSE_APACHE` | Ruffle web build: version information and licenses. |
| `player.html` | MortisFlash's own page: Ruffle configuration, loading of the SWF, and forwarding of `trace()` output and status messages to the application. |

The project file embeds every file of this folder in the executable:

```xml
<EmbeddedResource Include="Ruffle\**" LogicalName="Ruffle/%(Filename)%(Extension)" />
```

Each file becomes a resource named `Ruffle/<file name>`, which `FlashPlayerView` serves from memory on
`https://player.mortisflash/`. Since the logical name only contains the file name, subfolders are flattened: keep all
files directly in `Ruffle/`.

To update Ruffle:

1. Download the self-hosted web build of the Ruffle version you want: the `@ruffle-rs/ruffle` npm package, or the
   `web-selfhosted` archive of a release on <https://github.com/ruffle-rs/ruffle/releases>.
2. In `src/MortisFlash/Ruffle/`, delete the old `ruffle.js` and `.wasm` files. Do not delete `player.html`.
3. Copy the new JavaScript and WebAssembly files, `package.json` and the license files into the folder. Other files of
   the package, such as its README, are not needed.
4. Rebuild, open a few games, and check that they play and that their `trace()` output reaches the console.

Points to check after an update:

- The entry file must still be named `ruffle.js`: the application treats the player as unavailable when the
  `Ruffle/ruffle.js` resource is missing.
- `FlashPlayerView` sends the content type of `.html`, `.js` and `.wasm` files, and `text/plain` for any other
  extension. If a new Ruffle version needs other file types, extend `OnResourceRequested`.
- `player.html` relies on Ruffle's JavaScript API: `window.RufflePlayer.config`, `RufflePlayer.newest()`,
  `createPlayer()`, `player.ruffle()` (falling back to the element itself), `load({ url })`, `traceObserver`,
  `metadata` and the `loadedmetadata` event. If a version changes this API, adapt `player.html`.

## Test SWF files

Development and testing use the SWF files of the test suite of the JPEXS Free Flash Decompiler
(<https://github.com/jindrapetrik/jpexs-decompiler>). They are not part of MortisFlash and are not redistributed with
it: they are kept in the `samples/` folder, which is git-ignored. The development copy uses `samples/jpexs/` for the
SWF files (for example `as2_as2.swf`, `as3_new_as3_new.flex.swf` and `debug_game_as3_scoring.swf`, used in the
[editing example](editing.md#worked-example)) and `samples/jpexs_src/` for ActionScript sources of test cases.

To set them up, copy the test SWFs from the JPEXS repository into `samples/`. Open them in the application or run
`mflash` over them; [architecture.md](architecture.md#testing) describes the checks used during development. The
`out/` folder, convenient for export output, is git-ignored as well.

## See also

- [User guide](user-guide.md)
- [Editing a SWF](editing.md)
- [Command-line tool](command-line.md)
- [Architecture](architecture.md)
- [Project README](../README.md)
