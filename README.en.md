# CaseLight

*[Русский](README.md)*

Drives the lighting inside a PC case from what is on the screen.

The case usually stands right next to the monitor, so the picture can honestly be continued past its edge: a fan to the right of the panel shows that edge at its own height, a strip along the bottom shows the bottom. To make that work, every lit thing is described where it physically stands, and takes its colour from the part of the screen nearest to it.

The hardware is driven through [OpenRGB](https://openrgb.org): the motherboard, strips and fans on its headers, the graphics card, the memory modules.

## How it works

1. **Layout.** Fixtures are placed on a plane measured in millimetres, next to a rectangle standing for the monitor - one fixture per lit thing, dragged into position, sized and rotated.
2. **Binding.** Each fixture points at a controller, a header and a range of LEDs within it.
3. **Shape.** LEDs inside a fixture run as a strip, as a closed contour (round or rectangular), or collapse into a single point. A closed contour needs a starting LED, and a fan standing edge-on gets a flag of its own - its ring then flattens into a vertical line, which is exactly how it looks from the side.
4. **Painting.** The screen frame is averaged over a patch around each LED, corrected for colour, and sent to OpenRGB.

Strip length has to be found by hand: an ARGB header has no idea how many LEDs are soldered onto it. `tools/RgbCalibrator` exists for that - a window with a plus button that lights LEDs one at a time.

## Features

* Placement test: a patch you drag with the mouse in place of the screen, to check that what lights up is what should.
* Its own screen capture (DDA / WGC / GDI), or frames received from [Rimlight](https://github.com/Wa1den/Rimlight).
* A per-fixture update divider - memory sits on the slow SMBus, and writing to it every frame holds everything else up.
* Blanking on exit, lock, sleep and display off; recovery after the machine wakes.
* Starts the OpenRGB server itself, lives in the tray, exports and imports its settings as one file.

## Rimlight is optional

Frames can come from [Rimlight](https://github.com/Wa1den/Rimlight), the monitor bias lighting, in which case the screen is captured once for both. But that is only one of two sources: CaseLight captures on its own as well, and is meant to work on a machine with no strip behind the monitor and no controller for one.

The shared code - capture, colour pipeline, zone sampling, frame bus, power handling - lives inside this repository under `src/CaseLight.Core`. It is a copy of the same code Rimlight uses, deliberately kept standalone. The name of the frame bus is unchanged, so frames from Rimlight are still read as before.

## Installing

A built `CaseLight.exe` is in [releases](https://github.com/Wa1den/CaseLight/releases). It needs the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0).

To build it yourself:

```
dotnet publish src/CaseLight/CaseLight.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

## Privileges

Administrator rights are needed only for the memory modules: they sit on the SMBus, which is out of reach without them. The motherboard, fans and graphics card are reachable from an ordinary session, and then OpenRGB starts without a UAC prompt.

## Tools

`tools` holds the utilities used to find out what could be controlled at all:

* `RgbProbe` - what the OpenRGB SDK server offers: devices, zones, modes.
* `RgbCalibrator` - measuring the real length of a strip on a header.
* `LampProbe` - what Windows itself sees through the built-in LampArray.

## Licence

[MIT](LICENSE).
