# TerrainLab in WorldBox

`worldbox_mod/TerrainLab` is an NML source mod. Its standalone icon on the right
opens the GIS workspace: an adaptive top toolbar styled from WorldBox's bottom
panel plus a stock internal `ScrollWindow` cloned from `windows/empty`. The
side button cycles `off -> toolbar -> general settings -> off`, so opening the
working toolbar no longer opens a modal window over it.

## Installation

- NML loader: `<WorldBox>/worldbox_Data/StreamingAssets/mods/NeoModLoader.dll`
- User mods: `<WorldBox>/Mods`
- TerrainLab: `<WorldBox>/Mods/TerrainLab`

WorldBox experimental mode must be enabled for native mods. Run
`worldbox_mod/install.cmd` after installing NML. The installer builds against
the local game assemblies and copies only TerrainLab source, locales, metadata,
and resources. It uses normal `dotnet` and file-copy operations; it does not
compile temporary PowerShell interop assemblies.

## TerrainLab 1.0 workspace

The top toolbar keeps routine commands over the map. It stretches with the
logical canvas, evenly distributes buttons, and wraps into the minimum balanced
row count allowed by the current resolution and UI scale. It reaches both
canvas edges, uses the vertically flipped bottom-panel frame, and has no status
caption. Two clipped, unflipped edge strips preserve the stock side ornaments
at the top corners. The permanent row has red menu/save actions and gray chapter
selectors; choosing Project, Terrain, Digitizing, Analysis, or Layers replaces
the contextual functional row below. Colored outlines and flag separators
identify functional groups, while native WorldBox on/off sprites report active
states and amber lamps report running work. A second click deselects a chapter,
map tool, or visible derived layer. The frame height shrinks or grows to its row
count. The internal window is limited to project/import details, numeric
parameters, layer
diagnostics, and settings.
Closing that window leaves the map toolbar active; the standalone side icon
closes or reopens the complete workspace. The stock window cross advances the
side-button cycle from settings to off.

### Map

The permanent toolbar row provides Save and the Project chapter selector. Its
contextual row validates and exports the current WBXGEO sidecar, creates a
portable package, and exports every ready raster to a timestamped GeoTIFF
directory. The internal project page imports recent packages into the first free
WorldBox `saveN` slot.

`Prepare` creates a stable project sync workspace. `Pull safe` rejects an
incoming DEM when the in-game DEM changed after the baseline. `Pull + branch`
preserves the current world DEM before applying the incoming one. There is no
silent conflict overwrite in the 1.0 UI. See [file sync](FILE_SYNC.md).

### Relief

The layer page exposes the authoritative elevation, landform, material, and
vanilla layers. Toolbar tools inspect, set, raise, lower, and smooth signed Int16
elevation. Target, step, and radius live in Parameters; 32-operation undo/redo
stays directly on the toolbar.

`9999` cannot be painted because it is reserved for `NODATA`.

The surface toolbar adds a safe eyedropper, four-connected bucket fill,
multi-vertex line, filled polygon, rectangle, and connected-region
polygonization with an explicit apply-selection command. A sampled surface
contains the vanilla base/top tile type and frozen state; it never copies DEM
height. Explosive, damaging, spreading, lava/acid, `grey_goo`, TNT, mine-like,
and other non-copyable game surfaces are rejected as targets. Undo/redo handles
both DEM and surface edits through one history capped at 32 entries and 64 MiB.

The existing WorldBox terrain morphotype stays independent, so a mountain tile
can carry any analytical height. Horn `3 x 3` analysis derives slope, aspect,
hillshade, and ruggedness in a cancellable background job. Hypsometry and all
three display derivatives can be drawn over the game map.

### Hydrology

Priority-Flood creates a non-destructive filled surface. Deterministic D8 then
calculates receiver direction, exact UInt32 contributing-cell accumulation,
thresholded streams, stable outlet-based watersheds, and Strahler order.
Changing only the stream threshold rebuilds streams and order without rerunning
Priority-Flood.

The view reports outlets, fill depth, accumulation, stream cells, watershed
count, and maximum order. Streams, accumulation, fill, watersheds, and Strahler
order have chunked overlays. Any DEM edit marks the analysis stale.

### Erosion

The 1.0 erosion baseline performs deterministic D8 downhill transfer plus
four-neighbor-pair talus relaxation using integer arithmetic. Iterations, flow
strength, thermal strength, and talus threshold are editable. Diagnostics show
changed cells, transport, cut/fill, and exact mass balance.

Net change, erosion, deposition, and result DEM can be previewed before apply.
Apply is refused unless mass balance is zero and enters the whole result as one
undoable elevation edit. This baseline is reproducible but is not presented as
a calibrated physical process model.

### Settings

Settings reports WBXGEO schema, map budget, and exchange paths. The Layers page
reports the complete runtime catalog with ready, stale, or missing state.

## Runtime rules

TerrainLab runs at most one scientific background job at a time to bound peak
memory on the maximum 1,884,160-cell map. Raster overlays use independent
`256 x 256` textures, so extreme aspect ratios do not require one world-sized
texture. The bottom strip reports coordinates and module-specific cell values.

The map budget equals a `20 x 20` WorldBox-block baseline plus 15 percent.
Aspect ratio is unrestricted. Over-budget worlds continue to use vanilla saves,
but TerrainLab does not allocate GIS state for them.

## Persistence and compatibility

WorldBox writes `map.wbox` first; TerrainLab then writes
`terrainlab.wbxgeo` beside it by temporary-file replacement. WBXGEO embeds the
same vanilla map plus core GIS arrays and optional hydrology/erosion modules.
Unknown optional module data survives load/save. Invalid optional data is
dropped without blocking the core project or vanilla map.

Normal and Workshop saves receive sidecars. Autosaves remain vanilla to avoid
duplicating large continuous layers every cycle. Workshop users without the mod
can use `map.wbox`; TerrainLab users recover the extended project when the
matching sidecar is present.

The exchange directory is
`<WorldBox persistent data>/TerrainLab/Exchange`. Direct package import validates
schema, checksums, dimensions, cell budget, embedded base-map hash, and fixed
payload names before atomically renaming a staging directory into `saveN`.

Format details: [WBXGEO overlay format](WBXGEO_FORMAT.md).

Art inventory: [TerrainLab UI assets](TERRAINLAB_UI_ASSETS.md).
