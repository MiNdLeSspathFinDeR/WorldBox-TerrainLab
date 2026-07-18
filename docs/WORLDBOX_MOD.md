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

## TerrainLab 1.6 workspace

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
parameters, layer diagnostics, and settings. The bottom coordinate/elevation
strip is hidden unless Inspector is the active map tool; selecting another tool
or clicking Inspector again removes it.
The standalone side icon closes or reopens the complete workspace. The stock
window cross advances the side-button cycle from settings to off.

### Map

The permanent toolbar row provides Save and the Project chapter selector. Its
contextual row validates and exports the current WBXGEO sidecar, creates a
portable package, and exports every ready raster to a timestamped GeoTIFF
directory. The internal project page imports recent packages into the first free
WorldBox `saveN` slot.

The same chapter opens and toggles
`<WorldBox persistent data>/TerrainLab/ImageWorkspace`. When enabled, the
watcher waits for an image to have the same size and modification time across
two scans, then invokes the installed `imagetomap` command directly without
PowerShell. Only one conversion runs at once. The adaptive terrain algorithm
uses the gameplay-safe palette and fits the source aspect ratio to the largest
grid inside the 1,884,160-cell budget; elongated projections are not forced to
a square. The watched-folder contract is exactly `PNG`, `JPG/JPEG/JFIF`,
`TIFF/TIF`, `WebP`, `BMP`, `GIF`, `TGA`, `DDS`, and `JP2`. SVG, PDF, PSD,
archives, and every unlisted extension are ignored.

Two adjacent Project commands interpret a selected workspace raster. Automatic
clustering is the quick path: it has its own preview, explicit file
confirmation, optional area-of-interest polygon, five basic controls, and ten
collapsible expert controls. The boundary removes exterior noise from fitting
and converts it to deep ocean. Its settings persist per image in
`<image>.terrainlab-clustering.json`. Manual classification remains the
deliberate point/line/polygon path with a separate
`<image>.terrainlab-classification.json` profile. The converter never combines
the two profiles in one run.

The converter writes a non-`saveN` staging directory and publishes the finished
map with one directory rename. A game slot therefore appears only after
`map.wbox`, `map.meta`, both previews, and `map_stats.s3db` are complete. The
current world is never replaced. Processed and failed file fingerprints persist
in `.terrainlab-workspace.json`; changing a source queues it again, and the
Project page can explicitly retry unchanged failures. Its status shows the
queue, active file, selected Python backend, successes, and errors.

Save opens a TerrainLab name form instead of writing silently. Existing worlds
are renamed and saved to their current slot after confirmation. For a new world,
TerrainLab applies the name and then opens WorldBox's native slot picker; the
save patch writes the WBXGEO sidecar alongside the normal map.

`Prepare` creates a stable project sync workspace. `Pull safe` rejects an
incoming DEM when the in-game DEM changed after the baseline. `Pull + branch`
preserves the current world DEM before applying the incoming one. There is no
silent conflict overwrite in the 1.0 UI. See [file sync](FILE_SYNC.md).

### Relief

The layer page exposes the authoritative elevation, landform, material, and
vanilla layers. Toolbar tools inspect, set/flatten, raise, lower, smooth, and
grade the `-20000..9000 m` signed Int16 DEM. The two-click ramp samples its
start height and interpolates to the configured endpoint across the current
brush width. Target, step, and radius live in Parameters; 32-operation
undo/redo stays directly on the toolbar.

`9999` cannot be painted because it is reserved for `NODATA`.

The surface toolbar adds a safe eyedropper, four-connected bucket fill,
multi-vertex line, filled polygon, rectangle, and connected-region
polygonization with an explicit apply-selection command. A sampled surface
contains the vanilla base/top tile type and frozen state. Sampling also updates
the DEM target height, while applying the sampled surface never alters DEM
height. Explosive, damaging, spreading, lava/acid, `grey_goo`, TNT, mine-like,
and other non-copyable game surfaces are rejected as targets. Undo/redo handles
both DEM and surface edits through one history capped at 32 entries and 64 MiB.

The existing WorldBox terrain morphotype stays independent, so a mountain tile
can carry any analytical height. Horn `3 x 3` analysis derives slope, aspect,
hillshade, and ruggedness in a cancellable background job. Derivatives use the
project's horizontal metric cell size (`1000 m/cell` by default), so vertical
metre differences produce physical slope angles. Hypsometry and all four
display derivatives can be drawn over the game map. Core landforms,
materials, and DEM contours at 250-metre intervals are direct overlays that do
not require relief analysis. Selecting a missing or stale derivative starts its
calculation and opens it when ready.

### Hydrology

Priority-Flood creates a non-destructive filled surface. Deterministic D8 then
calculates receiver direction, exact UInt32 contributing-cell accumulation,
thresholded streams, stable outlet-based watersheds, and Strahler order.
Changing only the stream threshold rebuilds streams and order without rerunning
Priority-Flood.

The view reports outlets, fill depth, accumulation, stream cells, watershed
count, and maximum order. Filled elevation, D8 direction, streams,
accumulation, fill depth, watersheds, and Strahler order have chunked overlays.
Any DEM edit marks the analysis stale.

The Analysis toolbar also has a persistent `Live DEM water` toggle. A normal
water-layer contact receives a finite integer budget and follows the dedicated
Priority-Flood drainage rank. The Parameters page offers D8, D-infinity, and
MFD channel routing: respectively one receiver, at most two triangular-facet
receivers, or every strict downslope neighbor. Flats use the stable
Priority-Flood receiver. Marine water type is determined by bed elevation on
the zero-metre datum: `0..-5 m` is shallow water, `-6..-149 m` is shelf, and
`-150 m` or lower is deep ocean. Water painted onto land or created by a live
source first receives a persistent `River` or `Waterbody` attribute, so
freshwater remains shallow at any absolute elevation. A dry negative DEM cell
is never made water merely because it lies below zero; it must be reached by an
active source. The simulation
cannot convert more than the configured 1-100 percent of valid DEM cells, never
overwrites hazardous surfaces or ordinary buildings, pauses with WorldBox and
modal windows, and does not count the pre-existing ocean against its limit. The
selector does not alter analytical D8 watersheds, stream order, or erosion
products.

The native `geyser` building is observed at its real
`Building.spawnBurstSpecial(int)` event. Each burst injects volume at the
lowest safe outlet found within four cells, so the building is not replaced by
its own puddle. Repeated pulses restart an exhausted route and replenish
existing cells, making the geyser a continuing river source without bypassing
the same area cap. Destroying the geyser removes its pending source. A merged
river remains supplied if another registered geyser still feeds its connected
component.

When a route reaches another managed river, it merges into and recharges that
connected network; an existing lake or ocean terminates it without jumping over
the sink. A network with no reachable sink seeds a connected local lake at the
vulnerable terminal depression and grows only through cells at or below its
local spill level.

Each managed cell also has a saturating UInt8 water store. A configurable
uniform loss (`0..16` units per 30-second climate step) models the first
evaporation baseline. Flow traversal and geyser pulses recharge cells. At zero,
TerrainLab restores the compactly saved pre-water surface. This is a water
budget extension point for later precipitation, PET, infiltration, and climate
rasters, not a calibrated climate model. Disabling the toggle stops routing and
climate updates but leaves valid current WorldBox water visible.
An orphaned geyser component loses an additional configurable `1..64` storage
units per climate step even when ordinary evaporation is zero.

Each reached freshwater cell also stores UInt8 moisture and nonlinear
erodibility plus compact local Horn slope and downslope aspect. The angular
fields decode to radians but occupy one byte per cell. Material controls flow
resistance, retention, drying, and detachment: saturated soil or organic cover
can degrade into sand, and low-energy saturated alluvium can become a
gameplay-safe clay substrate. Established rivers also convert a configurable
one- or two-cell bank strip into sandy or fine clay/silt alluvium. Active river
cells use a bounded stream-power rule to lower the local Int16 DEM bed by
`1..3 m` per climate step, with a local guard 24 m below the current neighbor
floor. Routing is rebuilt after a cut while source head and remaining volume
are retained, so subsequent geyser pulses follow the evolving valley. Once an
orphaned channel drains, its persistent river attribute produces a sandy dry
bed with deterministic sparse hill and rare mountain shoulders instead of
erasing the valley history.

The Layers chapter can display the managed-water mask, UInt8 reserve,
river/waterbody class, moisture, erodibility, local slope, and local aspect
above the world. Routing updates touched cells; the 30-second climate pass
refreshes dynamic water layers because it may alter moisture, substrate, or DEM.

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
texture. While Inspector is active, the bottom strip reports coordinates and
module-specific cell values.

The map budget equals a `20 x 20` WorldBox-block baseline plus 15 percent.
Aspect ratio is unrestricted. Over-budget worlds continue to use vanilla saves,
but TerrainLab does not allocate GIS state for them.

## Persistence and compatibility

WorldBox writes `map.wbox` first; TerrainLab then writes
`terrainlab.wbxgeo` beside it by temporary-file replacement. WBXGEO embeds the
same vanilla map plus core GIS arrays and optional hydrology, live-water, and
erosion modules.
Live-water configuration and all eight state rasters use optional
`hydrology.water_dynamics` schema `1.7.0`. The managed mask, store,
river/waterbody class, moisture, erodibility, local slope, and local aspect also
export to GeoTIFF; restore-surface codes remain package-internal.
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
