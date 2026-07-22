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
7. Project semantic classes only onto the safe gameplay palette. Soil classes
   always carry a living biome suffix; bare `soil_low` and `soil_high` are not
   automatic outputs. Explosives,
   lava, grey goo, corruption, tumors, biomass, cyber tiles, and other disruptive
   materials are excluded unless the full palette is explicitly requested.

## Implemented configurable automatic clustering

Automatic clustering is a first-class image-import path beside manual
classification. Each source may own an independent
`<image>.terrainlab-clustering.json` profile. The profile is not read when a
manual classification profile is active, so quick unsupervised conversion and
deliberate supervised calibration cannot silently contaminate one another.

The automatic workspace can digitize one non-self-intersecting map boundary.
Publishing it first crops processing to its bounding box, so output dimensions
and georeferencing follow the selected area instead of the complete scan. Only
cells inside the polygon contribute to adaptive percentiles, water detection,
K-means sampling, and semantic cluster fitting. Cells inside that crop but
outside the polygon are written as deep ocean. This removes paper margins,
legends, labels, and other background without teaching those colours to the
classifier.

### Versioned clustering engines

Automatic profiles now use schema 4 and carry an explicit engine descriptor:

```json
{
  "algorithm": {
    "id": "semantic_v2",
    "version": 2
  }
}
```

New profiles select `semantic_v2`. Profiles from schemas 1 through 3 migrate
to `adaptive_v1` version 1, so merely opening or saving an existing sidecar
cannot silently change its map output.

`semantic_v2` implements the first 2.0 analysis stage:

1. Crop and mask the published area of interest in source-raster coordinates.
2. Analyze the source before WorldBox-grid resizing. The configurable longest
   side defaults to 2048 pixels, is bounded to 512..4096, and also observes a
   12-megapixel safety ceiling.
3. Validate independent landform, substrate, hydrology, biotope, theme, and
   confidence layers. Water, hills, summits, and bare rock cannot carry a
   biotope.
4. Reduce the categorical result to the requested WorldBox grid by covered-area
   voting. Numeric tile IDs are never interpolated.
5. Reapply the area-of-interest boundary on the final grid as an authoritative
   mask, then publish its exterior as deep ocean.

Generated semantic maps are written to `terrainlab-semantic/` beside the map:
`landform.png`, `substrate.png`, `hydrology.png`, `biotope.png`, `theme.png`,
`hostability.png`, `confidence.png`, and `semantic.json`. Confidence stores the
winning area fraction of each reduced cell as UInt8.

This is the stable Stage-A contract for the 2.0 alpha. Fine/coarse SLICO, RAG
region features, separate open/linear-water heads, graph regularization, and
river vector topology will be added behind later `semantic_v2` algorithm
versions. They are not represented as completed by this stage.

The profile exposes 16 bounded and reproducible controls:

| Group | Controls |
| --- | --- |
| Basic | cluster count, spline radius, map smoothing passes, minimum land region, water sensitivity, source-analysis limit |
| Spectral | RGB colour, luminance, and saturation weights |
| Structure | local texture, edge/slope, and spatial-coordinate weights |
| Quality | original-detail blend, training sample limit, K-means iteration limit |
| Reproducibility | deterministic random seed |

Spline smoothing builds a multiscale feature raster before clustering. The
detail blend can retain narrow source structure while the spline component
suppresses print grain. Spatial weighting favours compact regions but remains
optional because a high value can separate distant occurrences of the same
class. Texture and edge weights are likewise opt-in expert choices for maps
whose hatching or relief boundaries carry information. Nearest-centre distances
use a bounded matrix calculation rather than allocating a
pixel-by-cluster-by-feature tensor.

A separate collapsible composition editor opens two independent game-style
icon palettes: **Morphotypes** and **Biotopes**. Native surface sprites show
what every choice produces, and native green activity lamps show its state.
The biotope palette includes every normal seed biome registered by current
WorldBox, with deterministic visible fallbacks for legacy paradox, wasteland,
and singularity assets. The final semantic projection remaps excluded results
to the nearest permitted class while preserving the water/land domain whenever
possible.

Manual and automatic profiles both store `settings.long_side_blocks`. It fixes
the longer side of the generated map in `64 x 64`-cell WorldBox blocks; the
shorter side is rounded from the current processing aspect. Before a boundary
is published this is the source aspect; afterwards it is the published
boundary's bounding-box aspect. The in-game editor immediately recomputes the
selected `width x height`, source/extent dimensions, and largest recommended
pair under the `1,884,160`-cell budget with the same algorithm used by the
converter. This pair is advisory: explicit sizes begin at `1 x 1` block and
may exceed it, in which case the editor warns without blocking conversion.
Existing schema-1 and schema-2 profiles migrate to a 20-block long side.

## Implemented manual calibration

TerrainLab can display a workspace raster over the live map and store point,
line, or polygon annotations in
`<image>.terrainlab-classification.json`. Geometry is drafted first and does
not alter the profile. A point completes on its first click; a line or polygon
uses desktop-GIS digitizing with a live rubber band and explicit completion.
Only **Publish object** commits the feature. It remains visible after commit,
while geometry and all object attributes reset to an unselected state. Every
annotation contains these independent observations:

- a surface morphotype such as shelf, river/lake, plain, hill, rock, summit, or
  local depression;
- a playable WorldBox biotope for seedable soil surfaces, selected from an
  in-canvas native-sprite palette;
- signed Int16 elevations on its vertices in `-20000..9000 m`, excluding reserved
  `NODATA=9999`.

Lines additionally store an authoritative `1..32`-cell output width. Completed
polygon drafts and saved polygons use a repeated morphotype pattern; points and
all line/polygon vertices use the shared Turbo DEM palette. Both update live as
the draft attributes change.

The editor no longer offers a bare-soil biotope. Schema version 3 stores a
height on every vector vertex plus the requested output long side. Version 1
profiles still load and copy their object-level height to each vertex; version
2 profiles retain their vertex heights and receive the 20-block size default.
Legacy `none` soil is projected to grass. When an
ImageToMap world is opened for the first time, the mod incrementally calls
WorldBox's native biome vegetation selector for trees, plants, and bushes. A
custom-data flag makes this initial seed pass idempotent.

The guided classifier normalizes the source's own colour range and combines
colour, local gradient/roughness, and normalized image position. Spatial
distance breaks ties between equal-colour samples, so a repeated cartographic
colour can describe different regional morphotypes or biotopes. Every target
cell inside a polygon and every rasterized line cell is authoritative. Up to 32
spatially distributed cells per vector object feed propagation outside it; the
combined effective training set remains capped at 512. Profiles accept at most
128 simple non-self-intersecting polygons, 128 lines, 256 vertices per object,
and 8192 vector vertices in total, so broad areas do not become millions of
JSON samples. Later vectors own overlap, while precise point controls are
applied last. Calculations run in bounded chunks.

Point and vector-vertex heights form a bounded inverse-distance-weighted DEM on
a coarse grid. Coincident controls are averaged, then the surface is
bicubically expanded and receives deterministic multiscale relief. Polygon
interiors and line corridors therefore follow their vertex controls instead of
becoming flat platforms. Ordinary terrain is limited to approximately
20 degrees at the default 1000-metre cell scale. Hills receive only a rare
tail up to 50 degrees; rocks and summits can approach 84-88 degrees only in
their extreme tail. Low-amplitude broad waves keep the median slope in ordinary
terrestrial single digits.
Deep ocean, shelf, and marine shallows retain their defined depth intervals;
the river/lake class is intentionally independent of absolute elevation so
high-altitude water remains possible. The generated save contains the
uncompressed signed Int16 `terrainlab-elevation.tif`. On first load TerrainLab
combines that DEM with the vanilla-safe surface map; a subsequent normal save
promotes the state into WBXGEO.

An optional map boundary excludes scan margins, legends, and other exterior
noise from clustering and training and controls the processing crop and output
aspect after publication. Its exterior can use an independent safe morphotype,
biotope, and elevation, or **Auto**. Auto propagates the nearest classified
interior surface and biotope in a deterministic Voronoi-like pass while the
interpolated DEM continues smoothly through the exterior. Existing profiles
remain compatible and default to `deep_ocean`, `none`, and `-4000 m`.

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
| Mountain and summit | `4500 m` | `7000 m` | `2200..9000 m` |
| Shallow water | n/a | n/a | `-5..0 m` |
| Shelf | n/a | n/a | `-149..-6 m` |
| Deep ocean | `-5000 m` | `-7000 m` | `-11000..-150 m` |

The mountain and deep-ocean extreme budgets use integer cell counts, so no
more than five percent of either group can reach `7000 m` elevation or depth.
The inferred grid then receives a metric `1000 m` horizontal cell size and a
spatial grade constraint: same-domain cardinal neighbors may differ by at most
`364 m`, diagonal neighbors by `515 m`, and coast cells approach the zero datum
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
3. The selected routing front first creates managed shallow channels as up to
   three consecutive receiver cells. The strongest route therefore appears as
   one connected straight or turning triplet instead of several sibling pixels.
   A blocked receiver is discarded without searching beyond it, so a channel
   cannot jump across an unavailable gameplay cell. Secondary D-infinity and
   MFD receivers remain queued as later branches, capped at 512 fronts per
   source. Local depression filling is interleaved only among adjacent cells at
   the same positive-depth spill elevation; it never runs an unrestricted flood
   fill over a tilted plane. If the last connected front cannot reach another
   river, lake, or ocean, it selects the lowest, deepest-fill,
   lowest-resistance adjacent seed and grows a connected terminal lake no
   higher than its local Priority-Flood spill level.
   A runtime UInt16 owner grid distinguishes independently supplied channels.
   The first junction between a source pair receives one bounded discharge
   bonus and queues at most four safe cells around the confluence for basin
   occupation. The rewarded source-origin pair survives in-session routing
   rebuilds and cannot generate the same bonus repeatedly.
4. Source volume is integer. Every member of a triplet is charged separately;
   a cell cost combines Priority-Flood depth with a
   material/feature/moisture resistance term. Clay and an established channel
   pass flow farther than dry soil, organic cover, rock, or artificial material.
   An ordinary contact still has a finite configurable volume.
5. A Harmony postfix observes the native `geyser` building's own
   `Building.spawnBurstSpecial(int)` call. Its first pulse starts a pristine
   routing state automatically; every burst adds configurable volume at the
   lowest safe outlet on the nearest available ring, searching up to four cells
   outside the building footprint. A live geyser therefore behaves as a
   continuing river source without replacing itself. Destroying that building
   removes its pending source immediately. A merged channel remains supplied
   when another live geyser still reaches the same connected component.
6. TerrainLab-created water may occupy any user-selected `1..100%` share of
   valid DEM cells. Existing ocean cells do not consume that budget, and
   hazardous/non-copyable surfaces and protected buildings are never converted
   by ordinary channel routing.
   Volume, substrate resistance, obstacles, and the per-tick work limit still
   bound actual spread.
   Incremental topology cleanup additionally converts only fully
   water-enclosed one- or two-cell four-connected dry components. Corner-only
   contact does not merge raster islands, preventing diagonal checkerboard
   chains. Side-connected dry triplets and all larger components remain stable.
   Liquid-sensitive vegetation and minerals on tiny islands may be eroded;
   settlements, creatures, creep, geysers, anomalies, and other structures
   remain protected.
7. Marine class follows absolute bed elevation on the zero datum: `0..-5 m` is
   shallow, `-6..-149 m` is shelf, and `-150 m` or lower is deep ocean. Water
   painted onto a known land substrate or reached by routing first receives a
   persistent `River` or `Waterbody` class and remains shallow freshwater at any
   elevation. Unmanaged positive-elevation legacy water is restored to land.
   Dry negative depressions remain dry until reached by a source.
8. Managed water stores one UInt8 depth/reserve value and one UInt8 palette code
   for the pre-water surface. Uniform configurable evaporation runs every 30
   seconds; routed flow recharges cells, and a zero store restores that surface.
   A connected component orphaned by geyser destruction also loses a
   configurable `1..64` reserve units per climate step, even when uniform
   evaporation is zero.
   The mask and store export as `managed_water.tif` and `water_storage.tif`.
9. Five additional one-byte fields describe river-valley state: persistent
   hydro feature, moisture, nonlinear erodibility, local Horn slope, and local
   downslope aspect. Erodibility and the metric terrain derivatives are ready
   for every valid DEM cell before water arrives. Slope maps `0..254` to
   `0..pi/2` radians; aspect maps the same range to `0..2*pi`; `255` means
   undefined. This costs five bytes per map cell and avoids floating-point
   derivative grids.
10. Every 30-second climate pass updates moisture and erodibility only for wet,
    hydro-feature, or still-moist cells. Saturated soil/organic material may
    degrade to sand; saturated convergent alluvium may become gameplay-safe
    clay. An established river moistens and degrades safe erodible terrain in a
    configurable one- or two-cell bank strip into sand or fine clay/silt
    alluvium. A bounded stream-power proxy may incise an active river by
    `1..3 m`, guarded against cutting more than 24 m below its local neighbor
    floor. After its last source disappears, the stored river class remains:
    drainage exposes a sandy channel and deterministic sparse hill/rare
    mountain shoulders as a game-scale ravine. Routing is rebuilt on the
    changed DEM while active source volume is retained.
11. Managed mask, store, hydro feature, moisture, erodibility, local slope, and
    local aspect are visible as live overlays and export as UInt8 GeoTIFFs.
    A valid all-zero layer remains active and starts drawing incrementally when
    its first non-zero water cell appears.

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

Connected terminal filling follows the seed-and-target-level contract described
by [GRASS `r.lake`](https://grass.osgeo.org/grass-stable/manuals/r.lake.html).
The climate extension point follows the standard precipitation/inflow versus
ET/outflow/storage accounting summarized by the
[USGS water-budget framework](https://pubs.usgs.gov/circ/2007/1308/pdf/C1308_508.pdf).

The design follows the same separation used by SAGA's
[Channel Network](https://saga-gis.sourceforge.io/saga_tool_doc/9.12.0/ta_channels_0.html)
and GRASS
[`r.watershed`](https://grass.osgeo.org/grass-stable/manuals/r.watershed.html):
condition the DEM, derive flow connectivity and accumulation, then extract or
evolve channels. TerrainLab uses its own bounded integer runtime implementation
rather than embedding SAGA or GRASS binaries.

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

When the imported TIFF is georeferenced, ImageToMap now preserves:

- the source WKT and PROJJSON plus an identified horizontal EPSG code;
- the complete six-coefficient GDAL affine transform, including rotation;
- `PixelIsArea` or `PixelIsPoint`;
- the source GeoKey directory, double parameters, ASCII parameters, and
  vertical EPSG/name when present;
- the original raster size, published processing extent, and the affine
  translated to that extent before it is resampled to the WorldBox grid;
- a `5 x 5` local-cell/source/WGS84 control grid.

`terrainlab-georeference.json` travels beside the generated save and inside the
WBXGEO manifest. The affine chain is explicit:

```text
WorldBox cell (south-west origin)
  -> resized raster pixel/line (north-west origin)
  -> source CRS
  -> EPSG:4326 through PROJ
```

The first two operations are exact affine transforms. A projected source CRS
usually cannot be converted to EPSG:4326 by one affine, so TerrainLab does not
invent one. It stores the source CRS definition and WGS84 control grid; QGIS or
PROJ performs the actual nonlinear operation. Grid-shift resources required by
the source CRS must also exist in the receiving PROJ installation.

Every GIS export layer receives the same source CRS and resized affine. When a
map boundary was published, the source transform is first translated by the
boundary bounding-box pixel offset and only then scaled to the output grid.
Consequently a returned GeoTIFF overlays the selected part of the original in
QGIS even when the source was rotated. A ModelTransformationTag is used so
rotated rasters remain rotated. Each TIFF also gets `.tfw`, `.prj`, and
`.terrainlab-georef.json` sidecars. When the world has no source georeference,
export retains the synthetic WorldBox metric `ENGCRS` fallback.

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
