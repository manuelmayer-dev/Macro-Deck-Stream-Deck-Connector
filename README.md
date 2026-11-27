# Stream Deck Connector

<a href="https://macro-deck.app/store/com.suchbyte.streamdeck-connector"><img alt="Open in the Macro Deck Store" src="https://assets.macro-deck.app/branding/Open%20in%20the%20Macro%20Deck%20Store%20Badge%20Dark.png" height="56"></a>

A Macro Deck 3 plugin that turns an Elgato Stream Deck into a Macro Deck device. Macro Deck draws
every key exactly as it draws a button on any other client, and the plugin moves the finished pictures
to the hardware and the key presses back. This is an independent community plugin. It is not made or
endorsed by Elgato or Corsair, and it does not use or need the Elgato Stream Deck software.

## What it does

- Every attached Stream Deck shows up in Macro Deck's device settings, where it gets a name and a
  startup profile. Unplugging and replugging keeps it the same device.
- Keys show what the profile holds: icons, animated icons, labels, borders, background colours,
  clocks, weather, music and any other widget Macro Deck can draw.
- A key press runs the widget's actions. Short press, long press and double tap are recognised by
  Macro Deck from the press and the release.
- A Slider moves one step further on every press and wraps back to the start at the top.
- A profile that a Stream Deck uses is locked to the size of its key grid, and every widget on it is
  one key large: a widget cannot be stretched over several keys.
- Screensavers and folder views are not offered for a Stream Deck, because separate key displays
  cannot show them.

Dials, touch strips, the touch keys and info screen of the Stream Deck Neo and the Stream Deck Studio
are not supported.

## Requirements

- A Macro Deck 3 version that renders device key images. That support is not in a published release
  yet (Macro Deck pull requests 1873, 1874 and 1877). On an older Macro Deck the keys work but the
  displays stay dark, and the plugin reports that as an issue on its integration page.
- Windows x64, macOS arm64 or Linux x64.
- The Elgato Stream Deck software must not be running while Macro Deck uses the device. Both
  programs would draw on the same keys.

### Linux

Linux gives a normal user no access to the USB device by default. Create
`/etc/udev/rules.d/70-streamdeck.rules` with this content:

```text
KERNEL=="hidraw*", ATTRS{idVendor}=="0fd9", TAG+="uaccess"
SUBSYSTEM=="usb", ATTRS{idVendor}=="0fd9", TAG+="uaccess"
KERNEL=="hidraw*", ATTRS{idVendor}=="1b1c", ATTRS{idProduct}=="2b18", TAG+="uaccess"
SUBSYSTEM=="usb", ATTRS{idVendor}=="1b1c", ATTRS{idProduct}=="2b18", TAG+="uaccess"
```

Then reload the rules and replug the device:

```bash
sudo udevadm control --reload-rules
```

The plugin also needs `libudev`, which every desktop distribution installs.

## Supported devices

Only the Stream Deck MK.2 was tested on real hardware. Every other model is implemented from the
publicly documented USB protocol and should work, but nobody has confirmed it. Macro Deck shows a
note on the integration page while an untested model is connected. If yours misbehaves, please
[open an issue](https://github.com/manuelmayer-dev/Macro-Deck-Stream-Deck-Connector/issues).

| Model | Keys | Tested on hardware |
| --- | --- | --- |
| Stream Deck MK.2 | 15 | Yes |
| Stream Deck MK.2 (Scissor) | 15 | No |
| Stream Deck Original (v2) | 15 | No |
| Stream Deck Original (v1) | 15 | No |
| Stream Deck 15-Key Module, also the Scissor variant | 15 | No |
| Stream Deck XL, XL (2022), 32-Key Module | 32 | No |
| Stream Deck Mini, Mini (2022), Mini Discord Edition, 6-Key Module | 6 | No |
| Stream Deck + (keys only) | 8 | No |
| Stream Deck + XL (keys only) | 36 | No |
| Stream Deck Neo (the 8 display keys only) | 8 | No |
| Stream Deck Pedal (presses only, no displays) | 3 | No |
| Corsair Galleon K100 SD (the 12 display keys only) | 12 | No |

A Stream Deck that is not in this list is never opened. Macro Deck shows it as an issue on the
integration page with a link to report it. Other Elgato products are ignored.

## Actions

| Action | What it does |
| --- | --- |
| Set Stream Deck brightness | Sets the display brightness of every connected Stream Deck, from 0 to 100 percent. |
| Write Stream Deck diagnostics to the log | Writes a snapshot of everything the plugin sees to the Macro Deck log. With "Trace raw USB reports" switched on it also keeps logging raw USB reports until the action runs again with the option off. |

## Troubleshooting

**The keys stay dark but presses work.** Macro Deck is too old to render key images. Update it.

**The device does not appear.** Close the Elgato Stream Deck software. On Linux, add the udev rule
above and replug. The integration page in Macro Deck shows an issue when the device is attached but
cannot be opened, and the plugin tries again every few seconds.

**Pictures are rotated, mirrored or garbled, or keys are mapped to the wrong position.** This can
happen on a model that was never tested. Please report it.

### Reporting a problem

1. Add the action "Write Stream Deck diagnostics to the log" to a button and press it, or press
   "Write diagnostics to the log" on one of the plugin's issues on its integration page.
2. Copy the block that starts with "Stream Deck diagnostics" from the Macro Deck log.
3. [Open an issue](https://github.com/manuelmayer-dev/Macro-Deck-Stream-Deck-Connector/issues/new) and paste
   it, together with what you expected and what happened. A photo of the device helps for picture
   problems.

The snapshot holds the model, USB ids, firmware version, operating system, counters and the last
error. Serial numbers are masked to their last four characters. The optional raw trace logs USB report
headers and key states, never the pictures themselves.

## Privacy

The plugin talks to the USB device and to Macro Deck on the same computer. It opens no network
connection, collects nothing and stores nothing.

## Development

Requires the .NET 10 SDK.

```bash
dotnet build StreamDeckConnector.slnx -warnaserror
```

```bash
dotnet test StreamDeckConnector.slnx
```

The Macro Deck SDK is pinned in `Directory.Packages.props` (`MacroDeckSdkVersion`). The CLI version in
both workflows and `compatibility.macroDeck` in `manifest.json` follow it; change all of them in one
change.

The code has three parts. `Hid/` is the only place that touches HidSharp. `Devices/` is the USB
transport: the model catalogue, one protocol per device family, and a device object with one reader
thread and one writer that sends only the newest picture per key. `PluginIntegration`, `DeviceBridge`
and `StreamDeckLayouts` connect that transport to Macro Deck's device and layout provider contracts.
The plugin contains no renderer and no image library.

To add a model, add it to `Devices/StreamDeckModels.cs` and a row to the table above.

`THIRD-PARTY-NOTICES` is generated from the published output. Regenerate it after a dependency change.

## License

MIT, see [LICENSE](LICENSE). Third-party components are listed in
[THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES).

Stream Deck is a trademark of Corsair Memory, Inc. The name is used here only to say which hardware
the plugin works with.
