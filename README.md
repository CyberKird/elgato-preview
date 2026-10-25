# Elgato Preview

Low-latency live preview for the Elgato Game Capture HD60 S on Windows. A small D3D11 window that shows the capture card's video and nothing else, made to sit under tools like Lossless Scaling.

Unofficial. Not affiliated with or endorsed by Elgato or Corsair.

## What it does

- Reads the card through Media Foundation and renders with Direct3D 11. YUY2 is converted in the shader, so there is no extra conversion pass.
- Bicubic (Catmull-Rom) upscaling by default, with other scalers on `S`.
- Optional playback of the card's audio through WASAPI.
- Skip repeated frames (`D`): the window follows the game's real frame rate instead of the card's fixed 60, so frame generation tools see the true source rate.
- Stats overlay (`I`), vsync, exclusive fullscreen, always on top.

## Keys

| Key | Action |
| --- | --- |
| `F2` | Settings |
| `F` / `F11` / `Alt+Enter` | Fullscreen |
| `L` | Windowed |
| `S` | Cycle scaler |
| `D` | Skip repeated frames |
| `V` | Vsync |
| `E` | Exclusive fullscreen |
| `I` | Stats |
| `T` | Always on top |
| `Esc` | Quit |

Right click opens the same options as a menu. Settings are stored in `%APPDATA%\ElgatoPreview\settings.json`.

## Build

Needs the .NET 8 SDK on Windows and the Elgato driver for your card.

```
dotnet build -c Release
```

## License

[GPL-3.0-or-later](LICENSE). Uses [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) and [NAudio](https://github.com/naudio/NAudio), both MIT.
