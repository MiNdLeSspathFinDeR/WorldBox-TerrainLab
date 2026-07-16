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

## Implemented runtime relief

TerrainLab 1.0 derives four rasters from the authoritative Int16 DEM with a Horn
`3 x 3` neighborhood: slope in tenths of a degree, downslope aspect in tenths
of a degree, hillshade, and local ruggedness. Edge samples are clamped and
`NODATA` neighbors fall back to the center cell. The calculation is cancellable,
revision-bound, and does not alter the DEM.

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
7. Analysis runs on a background task with cancellation. Five hydrology previews
   render in `256 x 256` chunks.
8. Filled elevation, D8 direction, accumulation, streams, watersheds, and order
   are stored under `modules/hydrology/`, tied to a checksum of elevation and
   landform.

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
Elevation import is restricted to signed Int16, exact project dimensions, and
`NODATA=9999`. File sync adds a source SHA-256 baseline, conflict policies,
branches, incoming history, and an append-only JSONL log.

## Next scientific modules

The runtime and WBXGEO elevation layer uses signed Int16 values, with `9999`
reserved as `NODATA`, rather than operating directly on WorldBox tile IDs.
Scientific modules may promote one working chunk to floating point while they
calculate, then quantize it back to the authoritative integer grid:

1. `dem.inference`: infer relative elevation from relief shading, contours, and
   semantic terrain masks.
2. `hydrology.advanced`: MFD flow, sink breaching, editable outlets, lakes, and
   vector river constraints.
3. `erosion.process`: rainfall fields, water/sediment state, transport capacity,
   spatially variable erodibility, and calibrated timestep/units.
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
