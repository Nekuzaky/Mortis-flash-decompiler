# Third-party notices

MortisFlash is released under the [MIT License](LICENSE). It includes or depends on the following components, each
distributed under its own license.

| Component | Use | License |
|---|---|---|
| [Ruffle](https://ruffle.rs) (web build) | Flash Player emulator embedded in the app for playback; bundled in `src/MortisFlash/Ruffle/` and embedded in the executable | MIT or Apache-2.0, at your option ([LICENSE_MIT](src/MortisFlash/Ruffle/LICENSE_MIT), [LICENSE_APACHE](src/MortisFlash/Ruffle/LICENSE_APACHE)) |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) | User interface framework | MIT |
| [SkiaSharp](https://github.com/mono/SkiaSharp) and HarfBuzzSharp | 2D rendering and text shaping | MIT |
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) | Hosts the embedded player | BSD-3-Clause (see the package's `LICENSE.txt`) |
| [.NET runtime](https://github.com/dotnet/runtime) | Runtime included in the self-contained executables | MIT |

The Microsoft Edge WebView2 runtime is not distributed with MortisFlash; it is part of Windows or installed separately
from Microsoft.

## Test files

The screenshots and demo media show sample SWF files from the public test suite of the
[JPEXS Free Flash Decompiler](https://github.com/jindrapetrik/jpexs-decompiler) project. These files are not
included in this repository.
