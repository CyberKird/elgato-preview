# Elgato Preview

Low-latency live preview for Elgato capture cards on Windows (tested on the Game Capture HD60 S). A small D3D11 window that shows the capture card's video and nothing else, made to sit under tools like Lossless Scaling.

<a href="https://ko-fi.com/joltarise"><img src="https://raw.githubusercontent.com/CyberKird/.github/main/assets/ko-fi-button.svg" alt="Support me on Ko-fi" width="200"></a>

Unofficial. Not affiliated with or endorsed by Elgato or Corsair.

## Download

Get `ElgatoPreview-win-x64.zip` from the [latest release](https://github.com/CyberKird/elgato-preview/releases/latest), unzip it anywhere and run `ElgatoPreview.exe`. No installer and no .NET needed. Keep the `models` folder next to the exe (only frame generation uses it). You still need the Elgato driver for your card.

Windows may show a SmartScreen warning because the exe is not code-signed. The release has a `.sha256` file to check the download.

Tested with the HD60 S. Cards that appear as a normal Windows camera, like the 4K S, should work, but I have not tried them.

## What it does

- Reads the card through Media Foundation and renders with Direct3D 11. YUY2 is converted in the shader, so there is no extra conversion pass.
- Bicubic (Catmull-Rom) upscaling by default, with other scalers on `S`.
- Optional playback of the card's audio through WASAPI.
- Skip repeated frames (`D`): the window follows the game's real frame rate instead of the card's fixed 60, so frame generation tools see the true source rate.
- Frame generation 30 to 60 (`G`, off by default, experimental): RIFE 4.22 lite through ONNX Runtime DirectML, for 1080p sources. It builds the missing frame between two real ones on the GPU. Early, not tested yet in long play sessions.
- Stats overlay (`I`), vsync, exclusive fullscreen, always on top.

## Keys

| Key | Action |
| --- | --- |
| `F2` | Settings |
| `F` / `F11` / `Alt+Enter` | Fullscreen |
| `L` | Windowed |
| `S` | Cycle scaler |
| `D` | Skip repeated frames |
| `G` | Frame generation 30 to 60 |
| `H` | Steady HUD (frame generation) |
| `V` | Vsync |
| `E` | Exclusive fullscreen |
| `I` | Stats |
| `T` | Always on top |
| `Esc` | Quit |

Right click opens the same options as a menu. Settings are stored in `%APPDATA%\ElgatoPreview\settings.json`.

## Build

Needs the .NET 8 SDK on Windows and the Elgato driver for your card. Copy the `models` folder from the release zip into the project folder first (the RIFE model is not in the repository).

```
dotnet build -c Release
```

## License

[GPL-3.0-or-later](LICENSE). Uses [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows), [NAudio](https://github.com/naudio/NAudio) and [ONNX Runtime](https://github.com/microsoft/onnxruntime), all MIT. The frame generation model is [RIFE](https://github.com/hzwer/Practical-RIFE) 4.22 lite (MIT), exported to ONNX.
