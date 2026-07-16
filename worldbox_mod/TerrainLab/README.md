# TerrainLab WorldBox mod

TerrainLab 1.2 is an NML source mod that adds an in-game GIS workspace without
replacing WorldBox's normal save format. Its standalone transparent side icon
opens an adaptive map toolbar styled from WorldBox's bottom panel and a stock
internal window. Routine map commands stay in the toolbar, while project/import
details, numeric parameters, layer diagnostics, and settings stay in the
window. Custom GIS data is stored in an adjacent WBXGEO project and a vanilla
`map.wbox` remains available as the fallback.

The source mod installs to `<WorldBox>/Mods/TerrainLab`. NML compiles the files
under `Code` when the game starts.

## Implemented 1.2 surface

- signed Int16 elevation in `-20000..9000 m`, sea level `0`, and reserved
  `NODATA=9999`, independent from vanilla terrain morphotypes;
- Earth-like initial DEM inference from vanilla worlds, preserving raw relief
  order while keeping ordinary land predominantly below `2000 m`, placing the
  mountain/summit median at `5000 m`, and limiting `7000 m` elevation or depth
  extremes to at most five percent of their semantic group;
- a direct translucent DEM overlay with one pixel per world cell and a fixed
  blue/cyan and yellow/red Turbo scale around zero, with incremental
  refresh during edit, undo, and redo;
- categorical landform/material overlays, 250 m contours with emphasized
  1000 m and sea-level lines, and per-cell status inspection;
- inspect, set/flatten, raise, lower, smooth, sampled elevation, a two-point
  linear DEM ramp, brush radius, and 32-operation undo/redo;
- gameplay-safe surface sampling, four-connected fill, line, polygon,
  rectangle, connected-region polygonization, and apply-selection;
- Horn 3 x 3 slope, aspect, hillshade, ruggedness, and hypsometric overlays,
  including a dedicated ruggedness map layer;
- Priority-Flood, deterministic D8, UInt32 accumulation, stream extraction,
  stable watershed IDs, and Strahler stream order;
- bounded live-water routing with finite painted sources, native geyser pulse
  replenishment, selectable D8/D-infinity/MFD channel routing, local depression
  filling, absolute 0/-5/-150-metre water classes, persistent UInt8 storage,
  evaporation/recharge, gameplay-safe dry-surface restoration, and a
  non-bypassable 50-percent valid-cell ceiling;
- persistent river/waterbody semantics at any elevation, compact UInt8
  moisture, erodibility, local slope/aspect, material resistance and retention,
  soil-to-sand-to-clay evolution, and bounded local DEM channel incision;
- live managed-water, storage, hydro-feature, moisture, erodibility, local
  slope, and local aspect overlays that update during growth, recharge, drying,
  erosion, and evaporation;
- deterministic integer hydraulic/thermal transport with exact mass balance,
  preview overlays, apply, and undo;
- optional hydrology, live-water, and erosion payloads in WBXGEO, each protected
  by layer checksums;
- strict north-first GeoTIFF export and protected Int16 DEM import;
- local file sync with baseline SHA-256, conflict rejection, branch-and-apply,
  incoming history, and `changes.jsonl`;
- a total map budget of 1,884,160 cells with unrestricted aspect ratio;
- one background scientific job at a time and `256 x 256` overlay chunks;
- derivative buttons that automatically calculate missing relief, hydrology,
  and erosion prerequisites before showing all stored derivatives, including
  filled elevation and categorical D8 direction;
- a two-level adaptive GIS toolbar with red critical actions, gray chapter
  selectors, contextual functional tools, balanced wrapping, colored semantic
  separators, native WorldBox on/off lamps, repeat-click deselection,
  availability, progress, cancellation, localized tooltips, a three-level
  side-button cycle, and a separate internal settings window whose close button
  advances that cycle to off.

Exports and sync workspaces are written below
`<WorldBox persistent data>/TerrainLab/Exchange`. File sync is a local protocol
in 1.0; a separate QGIS plugin or network transport can build on it later.

Format details:

- `docs/WBXGEO_FORMAT.md`
- `docs/FILE_SYNC.md`
- `docs/TERRAINLAB_UI_ASSETS.md`

Run the complete package, algorithm, GeoTIFF, and sync probe from the repository
root:

```powershell
dotnet run --project worldbox_mod/TerrainLab.PackageProbe/TerrainLab.PackageProbe.csproj -c Release
```

Append `-- --stress` to run the exact 1,884,160-cell relief, hydrology, and
erosion acceptance case.
