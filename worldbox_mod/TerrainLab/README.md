# TerrainLab WorldBox mod

TerrainLab 2.0 alpha is an NML source mod that adds an in-game GIS workspace without
replacing WorldBox's normal save format. Its standalone transparent side icon
opens an adaptive map toolbar styled from WorldBox's bottom panel and a stock
internal window. Routine map commands stay in the toolbar, while project/import
details, numeric parameters, layer diagnostics, and settings stay in the
window. Custom GIS data is stored in an adjacent WBXGEO project and a vanilla
`map.wbox` remains available as the fallback.

The source mod installs to `<WorldBox>/Mods/TerrainLab`. NML compiles the files
under `Code` when the game starts.

## Implemented 2.0 alpha surface

- signed Int16 elevation in `-20000..9000 m`, sea level `0`, and reserved
  `NODATA=9999`, independent from vanilla terrain morphotypes;
- Earth-like initial DEM inference from vanilla worlds, preserving raw relief
  order while keeping ordinary land predominantly below `2000 m`, placing the
  mountain/summit median at `4500 m`, and limiting `7000 m` elevation or depth
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
- bounded live-water routing with finite painted sources, auto-starting native
  geyser replenishment, selectable D8/D-infinity/MFD channel routing through
  connected three-cell line/corner fragments without obstacle jumps, local
  depression filling and connected terminal lakes, one-time bounded growth at
  independent river confluences, cleanup of enclosed one-to-two-cell dry
  islands while preserving stable triplets, absolute
  0/-5/-150-metre water classes, persistent UInt8 storage,
  evaporation/recharge, gameplay-safe dry-surface restoration, and a
  user-selectable 1-to-100-percent valid-cell ceiling;
- persistent river/waterbody semantics at any elevation, compact UInt8
  moisture, full-grid erodibility and local slope/aspect, material resistance
  and retention, soil-to-sand-to-clay evolution, one-to-two-cell alluvial bank
  degradation, bounded local DEM channel incision, and sandy ravine formation
  after a destroyed geyser stops feeding its connected river;
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
- a persisted in-game image workspace watcher that calls the Python converter
  directly without PowerShell, processes one stable raster at a time with the
  safe adaptive palette, fits its aspect ratio inside the shared map budget,
  and atomically publishes a complete new vanilla `saveN` slot. Its explicit
  input contract is `PNG`, `JPG/JPEG/JFIF`, `TIFF/TIF`, `WebP`, `BMP`, `GIF`,
  `TGA`, `DDS`, and `JP2`;
- a manual source-raster canvas with zoom, pan, and explicit publish semantics
  for point, line, and QGIS-style polygon training geometry. Morphotype,
  per-vertex Int16 height, and line width are selected only after geometry
  completion and reset after publication. A compact in-canvas palette begins
  with native water and plain-through-summit morphotype shortcuts, followed by
  native biome surface sprites for every one of the 23 normal WorldBox seed
  biomes. Biotope choices lock for water, sand, rock, hill, and summit
  morphotypes.
  Polygon fills use live morphotype patterns; points and vector vertices use
  Turbo DEM colours. Vertices snap only when an existing control is within
  40 source pixels, so free placement remains available, and coincident
  controls from differently elevated objects meet near their mean height. A
  separate map boundary excludes exterior noise from learning while assigning
  those cells a configurable safe surface, biotope, and base elevation or a
  Voronoi-like Auto continuation of the nearest interior class. The
  compact per-image schema-v3 JSON profile guides adaptive
  colour/texture/spatial classification, while bounded, non-flat DEM
  interpolation uses ordinary 20-degree limits plus rare morphotype-dependent
  hill, rock, and summit tails and is transferred into the save as signed Int16
  `terrainlab-elevation.tif` and adopted on first world load;
- a collapsible automatic-clustering class-composition editor. Players can
  open separate game-style Morphotype and Biotope icon palettes, enable any safe
  morphotype and any of the 23 normal seed biomes before clustering, and read
  state from native green lamps. A live counter shows the effective class limit,
  requested total budget, and deduplicated candidate-pool size. Deep water,
  shelf, shallow water, and land share the same hard budget; when the pool is
  wider, source-pixel coverage gives dominant clusters first choice and rare
  groups merge into the nearest retained class. Invalid numeric model inputs
  are named and highlighted in red before conversion;
- clustering profile schema 4 with an explicit engine selector. Existing
  schema-1..3 projects stay on the bit-compatible `adaptive_v1` path, while new
  projects default to `semantic_v2`: bounded source-resolution analysis,
  categorical area-vote reduction, hostability constraints, and diagnostic
  landform, substrate, hydrology, biotope, theme, hostability, and confidence
  rasters;
- a shared final-size control in both raster workspaces. It fixes the longer
  map side in 64-cell WorldBox blocks, derives the shorter side from the source
  aspect or the published boundary bounding box, accepts sizes from `1 x 1`
  upward, and displays both the selected dimensions and the recommendation
  derived from the 1,884,160-cell memory budget. Larger selections warn but are
  not blocked;
- corrected save-gallery labels: vanilla presets retain their localized names,
  while custom or unresolved legacy sizes display actual world-cell dimensions
  instead of `-1`;
- extent-aware GeoTIFF round-trip: a published image boundary crops processing,
  translates the original six-coefficient affine before resampling, and keeps
  every exported layer aligned with the selected source area in QGIS;
- an in-game save form that edits the WorldBox map name before saving the
  ordinary map and WBXGEO sidecar, or hands a new world to the native slot
  picker;
- a total map budget of 1,884,160 cells with unrestricted aspect ratio;
- one background scientific job at a time and `256 x 256` overlay chunks;
- derivative buttons that automatically calculate missing relief, hydrology,
  and erosion prerequisites before showing all stored derivatives, including
  filled elevation and categorical D8 direction;
- an adaptive left legend for every active map layer: exact continuous
  renderer ramps with localized min/max/unit labels, or one framed,
  sprite-filled row per categorical surface class;
- a two-level adaptive GIS toolbar with red critical actions, gray chapter
  selectors, contextual functional tools, balanced wrapping, colored semantic
  separators, native WorldBox on/off lamps, repeat-click deselection,
  availability, progress, cancellation, localized tooltips for every command
  and numeric parameter, a three-level
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
