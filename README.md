# Gestura

**macOS-style three-finger app switching for Windows precision touchpads.**

Swipe three fingers left or right to cycle through your open apps in a fixed, circular order. Keep your fingers down and keep swiping to keep switching, with a smooth slide animation between windows.

## Features

- **Circular switching.** With apps 1, 2 and 3 open, swiping left goes 1 → 2 → 3 → 1. Swiping right goes the other way.
- **Continuous swiping.** Don't lift your fingers between switches. Keep moving and Gestura keeps switching, and you can reverse direction mid-gesture.
- **Stable order.** Apps are ordered by launch time, so the sequence doesn't shuffle as you switch (unlike Alt+Tab's most-recently-used order).
- **Slide animation (experimental).** The old window slides out and the new one slides in, using live DWM thumbnails.
- **Lightweight.** Runs in the system tray, with no windows and no configuration files.

## Requirements

- Windows 10 or 11
- A **precision touchpad** (most modern laptops have one)
- [.NET 8 SDK](https://dotnet.microsoft.com/download) to build and run

## Getting started

1. **Turn off Windows' built-in three-finger swipe** so the two don't conflict:
   **Settings → Bluetooth & devices → Touchpad → Three-finger gestures → Swipes → Nothing**
2. Clone the repo and run it:
   ```bash
   git clone https://github.com/h-sidali/gestura.git
   cd gestura
   dotnet run
   ```
3. Swipe three fingers left or right on your touchpad.
4. To quit, right-click the Gestura icon in the system tray and choose **Exit**.

## Configuration

Settings are constants in the source, since touchpad units vary by hardware.

| Constant | Where | What it does |
|---|---|---|
| `FirstThreshold` | `SwitchGesture` | Distance your fingers must move to trigger the first switch. |
| `StepThreshold` | `SwitchGesture` | Distance for each further switch while your fingers stay down. |
| `DurationMs` | `SlideOverlay` | Length of the slide animation in milliseconds. |

Lower thresholds make swipes more sensitive. If left and right feel backwards, swap `+1` and `-1` where `dir` is set in `SwitchGesture`.

## How it works

1. Registers for raw HID input from the touchpad (Raw Input API) and parses finger positions from each report.
2. Averages the horizontal position of three fingers and tracks how far they have moved.
3. Each time movement passes the threshold, it picks the next or previous window in launch order and brings it to the front.
4. A temporary borderless overlay slides live DWM thumbnails of the old and new windows, then disappears to reveal the real window.

## Known limitations

- **Animation backdrop.** The overlay is black behind the sliding windows. It looks best with maximized windows, and smaller windows may show a brief dark flash.
- **No animation** when switching to or from a minimized window, or between windows on different monitors. Those switch instantly.
- **Touchpad compatibility.** Different manufacturers report finger data slightly differently, so some touchpads may need tweaks to the parsing code.
- **Windows' own gesture must be disabled**, or it will fight with Gestura.
- Switching relies on `SetForegroundWindow`, which Windows restricts. Gestura works around this, but it can occasionally fail.

## Status

Early prototype. Switching and continuous swiping work. The animation is experimental. Issues and pull requests are welcome, especially reports for specific laptop models.


## Contributing

Bug reports with your laptop and touchpad model are the most useful contribution. For code changes, open an issue first to discuss what you'd like to change.

## License

MIT