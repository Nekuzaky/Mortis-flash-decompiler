First public release of MortisFlash, a Flash (SWF) decompiler, editor and ActionScript 2/3 compiler for Windows.

![MortisFlash](https://raw.githubusercontent.com/Nekuzaky/Mortis-flash-decompiler/main/docs/images/screenshot-main.png)

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

## Highlights

- **Decompile** SWF files and Flash projectors to readable ActionScript 3 and ActionScript 1/2, including AS2 classes.
- **Edit and recompile** with built-in compilers: AS3 method bodies, AS1/AS2 scripts and classes, or the p-code of any script.
- **Play** the game inside the app with Ruffle; it restarts after every change and its `trace()` output goes to a console.
- **Replace** images, sounds, stream sounds, binary data and texts, then save the modified SWF.
- **Export** scripts, images, sounds, shapes (SVG), sprites, fonts, texts, video and timeline frames.

See the [README](https://github.com/Nekuzaky/Mortis-flash-decompiler#readme), the [user guide](https://github.com/Nekuzaky/Mortis-flash-decompiler/blob/main/docs/user-guide.md)
and the [changelog](https://github.com/Nekuzaky/Mortis-flash-decompiler/blob/main/CHANGELOG.md) for details.

Only decompile files you own or have permission to analyze. Extracted code and assets remain the property of their authors.
