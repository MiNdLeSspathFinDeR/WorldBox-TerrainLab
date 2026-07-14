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

## Next scientific modules

The runtime and WBXGEO elevation layer uses signed Int16 values, with `9999`
reserved as `NODATA`, rather than operating directly on WorldBox tile IDs.
Scientific modules may promote one working chunk to floating point while they
calculate, then quantize it back to the authoritative integer grid:

1. `dem`: import a real DEM or infer relative elevation from relief shading and
   semantic terrain masks.
2. `hydrology`: depression filling, D8/MFD flow direction, flow accumulation,
   watershed boundaries, and stream extraction.
3. `erosion`: rainfall, hydraulic transport capacity, sediment pickup, and
   deposition with mass-balance diagnostics.
4. `thermal`: talus-angle relaxation for cliffs and scree accumulation.
5. `biomes`: classify climate/soil after the terrain process, so erosion changes
   the landscape before biome tiles are assigned.
6. `projection`: convert continuous elevation, water depth, moisture, and biome
   layers to WorldBox's discrete tile palette.

UMAP belongs between feature extraction and semantic clustering. It should be
an optional embedding backend trained on a representative pixel sample, while
the deterministic feature-space backend remains available for reproducibility
and machines without the heavier scientific dependencies.

Every physical process should expose its seed and numerical parameters, preserve
water/land masks unless explicitly allowed to transgress them, and report total
eroded and deposited mass. Those invariants make generated worlds reproducible
and keep the simulation tunable instead of turning it into an opaque filter.
