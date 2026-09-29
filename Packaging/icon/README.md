# RasterField app icon

`build_icons.py` generates everything from code: the SVG masters in this folder, and
`Source/RasterField/Assets/RasterField.icns` (macOS), `RasterField.ico` (Windows) and
`RasterField-256.png` / `-1024.png` (window icon, Linux, store listings).

```bash
python3 Packaging/icon/build_icons.py            # needs Chromium (headless) and Pillow
```

## Concept

A terrain summit in nested elevation bands. Its **lower-left half is raster cells**, the ER
Mapper / BIL grid RasterField reads. Its **upper-right half is smooth bands** whose outlines are
Catmull-Rom → Bézier curves, the Bézier-patch subdivision the app performs. The staircase along
the diagonal is where the grid turns into a surface. It is one picture of what the app does.

* **Colour:** the app's own palette. A slate plate (`#26343F → #121920`), the teal accent, and
  elevation bands running teal → green → sand → cream like the built-in *Elevation* palette.
  Nothing clashes with the UI, and the icon stays calm next to other Dock and taskbar icons.
* **Light:** a soft top highlight on the plate. There is no gloss and there are no hard
  reflections.
* **The summit is off-centre** (the bands drift up-left), so it reads as terrain, not a target.

## Platform conventions

| | macOS (Apple Human Interface Guidelines, Big Sur and later) | Windows (Fluent) |
|---|---|---|
| Canvas | 1024 × 1024 | 256 × 256 master |
| Shape | 824 px continuous-corner rounded rectangle (superellipse), centred with 100 px margins, the same outline at every size | rounded square filling the frame (radius ≈ 1/6), no extra margin |
| Depth | baked drop shadow (0 12 28, 32 % black), as the system expects on the icon grid | no baked shadow; Windows adds its own |
| Small sizes | 16 / 32 px use the simplified drawing | 16–32 px use the simplified drawing |
| Sizes shipped | 16, 32, 64, 128, 256, 512, 1024 (`icp4…ic10`, @1x and @2x) | 16, 20, 24, 32, 40, 48, 64, 128, 256 (PNG-compressed entries) |

**Per-size simplification.** Both guidelines ask for a drawing designed for small sizes,
not a large image scaled down. At 16–32 px the icon keeps the same idea with three large raster
cells per side instead of dozens. The cells are 192 units wide, exactly 3 px at 16 px and 6 px at
32 px, so their edges land on whole pixels and stay sharp. Outlines, the index contour and the
background grid are dropped at these sizes.

## Where it is used

* **Windows:** `<ApplicationIcon>` in `RasterField.csproj` embeds the `.ico` in `RasterField.exe`
  (Explorer, taskbar, Alt-Tab).
* **All platforms:** the window icon is set from `RasterField-256.png` (an `AvaloniaResource`).
* **macOS:** `Packaging/macOS/make-app.sh` wraps the published build into `RasterField.app`.
  The `.icns` goes in `Contents/Resources` and `Info.plist` names it; the plist also declares the
  `.rfproj`, `.ers`, `.erv` and `.geojson` document types. The release workflow ships the macOS
  builds as zipped `.app` bundles.
