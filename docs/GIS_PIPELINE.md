# GIS terrain pipeline

ImageToMap now treats conversion as a sequence of terrain-analysis stages rather
than a direct RGB-to-tile lookup. Each stage produces a layer that can later be
inspected, replaced, or controlled independently.

## Implemented baseline

1. Normalize luminance and saturation against the source image's own percentile
   range. This keeps bright BOTW-style maps and muted Elden Ring-style maps on
   comparable scales.
2. Detect conventional blue/cyan water before land clustering. Local continuity
   suppresses isolated blue labels and markers.
3. On muted maps, detect low-saturation, low-roughness water connected to the
   map boundary. This separates grey seas from similarly dark mountain shading.
4. Derive shallow, coastal, and deep water by morphological distance from shore.
   Narrow rivers therefore remain shallow or coastal instead of becoming deep
   ocean along their whole length.
5. Remove land components below a configurable gameplay area. This suppresses
   labels and sea hatching while preserving real islands above the threshold.
6. Cluster land in an adaptive feature space containing normalized luminance,
   saturation, cyclic hue, relative slope, vegetation, warm-soil, and coolness
   signals. Cluster assignment is deterministic.
7. Project semantic classes only onto the safe gameplay palette. Explosives,
   lava, grey goo, corruption, tumors, biomass, cyber tiles, and other disruptive
   materials are excluded unless the full palette is explicitly requested.

## Implemented Earth-like initial DEM

When no WBXGEO project exists, TerrainLab infers the first metric DEM from the
vanilla WorldBox height cache and the categorical surface morphotypes. Raw
height order is preserved inside each morphotype, but empirical ranks are
projected onto nonlinear Earth-like profiles instead of stretching every land
cell across the complete Int16 domain.

| Profile | Median | 95th-percentile boundary | Natural inferred range |
| --- | ---: | ---: | ---: |
| Lowland | `350 m` | `1200 m` | `0..1800 m` |
| Upland | `900 m` | `1800 m` | `300..2400 m` |
| Hill | `1600 m` | `2800 m` | `600..3800 m` |
| Mountain and summit | `5000 m` | `7000 m` | `2200..9000 m` |
| Shallow water | n/a | n/a | `-5..0 m` |
| Shelf | n/a | n/a | `-149..-6 m` |
| Deep ocean | `-5000 m` | `-7000 m` | `-11000..-150 m` |

The mountain and deep-ocean extreme budgets use integer cell counts, so no
more than five percent of either group can reach `7000 m` elevation or depth.
The inferred grid then receives a metric `1000 m` horizontal cell size and a
spatial grade constraint: same-domain cardinal neighbors may differ by at most
`500 m`, diagonal neighbors by `707 m`, and coast cells approach the zero datum
through a bounded transition. This removes isolated one-cell spikes without
changing imported or manually edited DEMs.
The natural deep-ocean floor approximates terrestrial bathymetry; the full
`-20000..9000 m` storage domain remains available to imported GeoTIFFs, manual
editing, and non-Earth projects. Existing WBXGEO and imported metric DEM values
are never rank-normalized.

## Implemented runtime relief

TerrainLab 1.1 derives four rasters from the authoritative Int16 DEM with a Horn
`3 x 3` neighborhood: slope in tenths of a degree, downslope aspect in tenths
of a degree, hillshade, and local ruggedness. Edge samples are clamped and
`NODATA` neighbors fall back to the center cell. The calculation is cancellable,
revision-bound, and does not alter the DEM. Horizontal derivatives divide by
the project's metric cell size, so slope is a physical angle rather than a
ratio of metres to an unspecified game tile.

## Implemented DEM visualization

The authoritative signed Int16 DEM has a direct map overlay independent of the
Horn derivatives. It is rendered in `256 x 256` point-filtered chunks with one
texture pixel per WorldBox cell and fixed alpha `156/255`. `NODATA=9999` is fully
transparent.

The color mapping uses the polynomial Turbo palette over two semantic ranges.
Values from `-20000` to sea level `0` occupy the blue-to-cyan interval, while
values from `0` to `9000` occupy the yellow-to-red interval. The fixed scale
makes a color comparable between projects instead of rescaling it to each
map's observed extrema. Incremental DEM edits rewrite only affected chunks.

Additional direct displays classify the stored landform and material rasters
and derive 250-metre contours on demand. Contour edits refresh the touched cells
and their four neighbors, preserving the one-cell boundary calculation without
rebuilding unrelated chunks.

The two-point ramp editor samples the first endpoint elevation and linearly
interpolates to the configured second endpoint. The current circular brush
radius widens the graded corridor; all affected cells are committed as one
revision-bound undo operation.

## Implemented runtime hydrology

The deterministic hydrology module operates over the same DEM:

1. Map-edge cells, vanilla water/depression morphotypes, and cells bordering
   `NODATA` act as drainage outlets.
2. Priority-Flood builds a non-destructive filled surface. The source DEM and
   WorldBox terrain morphotypes remain unchanged.
3. Every non-outlet cell receives a deterministic D8 receiver, including cells
   on flats introduced by depression filling.
4. Reverse traversal calculates upstream contributing area as an exact UInt32
   cell count. A user threshold derives the stream mask without rerunning D8.
5. Every valid cell receives a stable watershed ID propagated from its outlet.
   Stream cells receive deterministic Strahler order.
6. Editing the DEM increments its revision and immediately marks previous
   hydrology results stale.
7. Analysis runs on a background task with cancellation. Seven hydrology previews
   render in `256 x 256` chunks, including filled elevation and D8 direction.
8. Filled elevation, D8 direction, accumulation, streams, watersheds, and order
   are stored under `modules/hydrology/`, tied to a checksum of elevation and
   landform.

## Implemented live water routing

The optional `hydrology.water_dynamics` runtime turns water placement into a
bounded DEM process without replacing WorldBox's own ocean behavior:

1. Enabling the tool samples at most 96 spatially distributed contacts between
   existing water and lower-or-equal DEM cells. Painting a deep, coastal, or
   shallow water layer creates one debounced finite source at its best downhill
   contact.
2. A dedicated Priority-Flood surface provides depression spill levels and an
   acyclic drainage rank. Live channel routing is selectable: D8 follows one
   stable receiver; D-infinity evaluates the steepest direction over eight
   triangular facets and divides flow between at most two adjacent cells; MFD
   distributes flow among every strict downslope neighbor with Freeman's slope
   exponent `1.1`. Flats fall back to the Priority-Flood receiver.
3. The selected routing front first creates managed shallow channels, then interleaves
   local filling only among cells in the same positive-depth depression and at
   the same spill elevation. It does not run an unrestricted flood fill over a
   tilted plane. D-infinity and MFD fronts are capped at 512 queued branches per
   source; all branches consume the same finite integer source budget.
4. Source volume is integer. A cell cost combines Priority-Flood depth with a
   material/feature/moisture resistance term. Clay and an established channel
   pass flow farther than dry soil, organic cover, rock, or artificial material.
   An ordinary contact still has a finite configurable volume.
5. A Harmony postfix observes the native `geyser` building's actual `rain`
   submission to `DropManager.spawnParabolicDrop`. Every real drop adds
   configurable volume at the lowest safe adjacent cell, so a live geyser
   behaves as a continuing river source without replacing its building tile.
6. TerrainLab-created water may occupy at most the configured 1-50 percent of
   valid DEM cells. Fifty percent is a non-bypassable hard maximum. Existing
   ocean cells do not consume that budget, and hazardous/non-copyable surfaces
   or non-geyser buildings are never converted.
7. Marine class follows absolute bed elevation on the zero datum: `0..-5 m` is
   shallow, `-6..-149 m` is shelf, and `-150 m` or lower is deep ocean. Water
   painted onto a known land substrate or reached by routing first receives a
   persistent `River` or `Waterbody` class and remains shallow freshwater at any
   elevation. Unmanaged positive-elevation legacy water is restored to land.
   Dry negative depressions remain dry until reached by a source.
8. Managed water stores one UInt8 depth/reserve value and one UInt8 palette code
   for the pre-water surface. Uniform configurable evaporation runs every 30
   seconds; routed flow recharges cells, and a zero store restores that surface.
   The mask and store export as `managed_water.tif` and `water_storage.tif`.
9. Five additional one-byte fields describe river-valley state: persistent
   hydro feature, moisture, nonlinear erodibility, local Horn slope, and local
   downslope aspect. Slope maps `0..254` to `0..pi/2` radians; aspect maps the
   same range to `0..2*pi`; `255` means undefined. This costs five bytes per map
   cell and avoids persistent floating-point derivative grids.
10. Every 30-second climate pass updates moisture and erodibility only for wet,
    hydro-feature, or still-moist cells. Saturated soil/organic material may
    degrade to sand; saturated convergent alluvium may become gameplay-safe
    clay. A bounded stream-power proxy may incise an active river by `1..3 m`,
    guarded against cutting more than 24 m below its local neighbor floor.
    Routing is rebuilt on the changed DEM while active source volume is retained.
11. Managed mask, store, hydro feature, moisture, erodibility, local slope, and
    local aspect are visible as live overlays and export as UInt8 GeoTIFFs.

The selector affects only live channel creation. Stored watershed,
flow-accumulation, Strahler, and erosion products retain their deterministic D8
contracts.

Finite source queues are intentionally runtime-only and are not replenished on
reload. Managed mask, water store, and dry-surface palette survive reload,
while native geysers resume through later real pulses. This prevents save/load
from turning one finite placement into an infinite source. The current model is
a gameplay drainage, substrate-evolution, and uniform-loss water budget, not a
calibrated shallow-water or climate solver. Moisture and resistance are bounded
state proxies; velocity, physical infiltration, spatial precipitation, and
potential evapotranspiration are not yet calibrated.

Connected filling follows the seed-and-target-level contract described by
[GRASS `r.lake`](https://grass.osgeo.org/grass-stable/manuals/r.lake.html).
The climate extension point follows the standard precipitation/inflow versus
ET/outflow/storage accounting summarized by the
[USGS water-budget framework](https://pubs.usgs.gov/circ/2007/1308/pdf/C1308_508.pdf).

The design follows the same separation used by SAGA's
[Wang & Liu depression filling](https://saga-gis.sourceforge.io/saga_tool_doc/7.8.1/ta_preprocessor_4.html)
and [D8 channel-network](https://saga-gis.sourceforge.io/saga_tool_doc/9.11.1/ta_channels_5.html)
tools: first condition the DEM and derive flow connectivity, then extract or
evolve the channel process. TerrainLab uses its own bounded integer runtime
implementation rather than embedding SAGA binaries.

The detachment/resistance split follows the same modeling separation exposed by
[GRASS `r.sim.sediment`](https://grass.osgeo.org/grass-stable/manuals/r.sim.sediment.html):
terrain, water state, detachment capacity, transport capacity, and roughness
remain distinct inputs. TerrainLab quantizes those controls and limits local
incision for game-scale stability.

## Implemented erosion baseline

The `erosion.hydraulic` module is a reproducible integer process baseline, not a
calibrated physical erosion model. It traverses the acyclic D8 graph to transfer
material downhill, then relaxes four unique neighbor pairs above a configurable
talus threshold. Every transfer subtracts and adds the same integer amount, so
initial and final mass must match exactly. The result remains a preview until
the player applies it as one undoable DEM edit.

Parameters are iterations, flow strength, thermal strength, and talus threshold.
Diagnostics include transported, eroded, and deposited mass, changed cells,
maximum cut/fill, and exact mass balance. Results and net change persist in the
optional WBXGEO module and export to GeoTIFF.

## Implemented GIS exchange

All ready core and derived rasters export as uncompressed single-band GeoTIFF.
Files use conventional north-first row order, while WBXGEO remains south-first.
Elevation import is restricted to signed Int16 values in `-20000..9000`, exact
project dimensions, and `NODATA=9999`. File sync adds a source SHA-256 baseline,
conflict policies, branches, incoming history, and an append-only JSONL log.

## Next scientific modules

The runtime and WBXGEO elevation layer uses signed Int16 values from `-20000`
to `9000` metres, with sea level `0` and `9999` reserved as `NODATA`, rather
than operating directly on WorldBox tile IDs.
Scientific modules may promote one working chunk to floating point while they
calculate, then quantize it back to the authoritative integer grid:

1. `dem.inference`: infer relative elevation from relief shading, contours, and
   semantic terrain masks.
2. `hydrology.advanced`: sink breaching, editable outlets, calibrated lake
   levels, vector river constraints, and physical water depth/velocity.
3. `erosion.process`: rainfall fields, water/sediment state, transport capacity,
   and calibrated timestep/units on top of the one-byte erodibility baseline.
4. `biomes`: classify climate/soil after the terrain process, so erosion changes
   the landscape before biome tiles are assigned.
5. `projection`: convert continuous elevation, water depth, moisture, and biome
   layers to WorldBox's discrete tile palette.
6. `qgis.plugin`: automate the stable 1.0 file protocol, styling, validation,
   and optional later transport without changing the core sync contract.

UMAP belongs between feature extraction and semantic clustering. It should be
an optional embedding backend trained on a representative pixel sample, while
the deterministic feature-space backend remains available for reproducibility
and machines without the heavier scientific dependencies.

Every physical process should expose its seed and numerical parameters, preserve
water/land masks unless explicitly allowed to transgress them, and report total
eroded and deposited mass. Those invariants make generated worlds reproducible
and keep the simulation tunable instead of turning it into an opaque filter.
