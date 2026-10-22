Performance and player responsiveness update for MortisFlash, the Flash (SWF) decompiler, editor and ActionScript 2/3
compiler for Windows.

![MortisFlash](https://raw.githubusercontent.com/Nekuzaky/Mortis-flash-decompiler/main/docs/images/screenshot-main.png)

## What's new

**Faster and lighter**

- Images and sounds are decoded when they are first shown or exported instead of when a file is opened. Opening a game
  with many images is several times faster and uses less memory (for example 975 ms → 218 ms and 279 MB → 185 MB on a
  game with 93 images).
- The executables are precompiled (ReadyToRun): the window appears about 30 % sooner and first uses no longer pause
  for compilation.
- Previews and timeline playback copy pixels directly instead of encoding and decoding a PNG for every frame.
- The console batches `trace()` output, so games that trace every frame no longer slow the interface down.

**Player**

- **Play** after **Stop** is more than twice as fast: the player is kept instead of being destroyed, and one browser
  environment is shared.
- Ruffle is extracted once to `%LOCALAPPDATA%\MortisFlash\Ruffle` and read directly by WebView2 instead of going
  through the interface thread on every load.
- The game receives the keyboard as soon as it is loaded, so its controls respond without clicking it first.
- Browser shortcuts are disabled inside the game (no "Save page as" on Ctrl+S, no page reload on F5); F5, Ctrl+O and
  Ctrl+S are forwarded to the app.

See the [changelog](https://github.com/Nekuzaky/Mortis-flash-decompiler/blob/main/CHANGELOG.md) for the full history.

## Download

| File | Description |
|---|---|
| `MortisFlash-<version>-win-x64.exe` | The desktop app. Portable single file: download it and run it, no installation needed. |
| `mflash-<version>-win-x64.exe` | The command-line tool, for exports and scripting. |
| `SHA256SUMS.txt` | Checksums of both executables. |

**Requirements:** Windows 10 or 11, 64-bit. The embedded game player uses the Microsoft Edge WebView2 runtime, included
in Windows 11 and most up-to-date Windows 10 installations; the app offers a link to install it when it is missing.

**First launch:** the executables are not code-signed. If Windows SmartScreen shows *Windows protected your PC*, choose
**More info**, then **Run anyway**.

See the [README](https://github.com/Nekuzaky/Mortis-flash-decompiler#readme) and the
[user guide](https://github.com/Nekuzaky/Mortis-flash-decompiler/blob/main/docs/user-guide.md) to get started.

Only decompile files you own or have permission to analyze. Extracted code and assets remain the property of their authors.
