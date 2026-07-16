# WBXGEO overlay format

`WBXGEO` is TerrainLab's portable project format. It is a ZIP container with
the `.wbxgeo` extension. The package is self-contained for TerrainLab users and
keeps an unmodified WorldBox save as its compatibility payload.

## Design goals

- A recipient with TerrainLab can recover continuous and semantic GIS layers.
- A recipient without TerrainLab can still use the adjacent `map.wbox`.
- The package embeds the same `map.wbox`, so a single `.wbxgeo` file can be
  transferred and imported into a new save directory.
- Future modules can add data without changing or forking the core schema.
- Unknown optional module payloads survive a load/save round trip.
- Corrupt or stale overlays never prevent the vanilla save from loading.

## Package layout

```text
terrainlab.wbxgeo
|-- mimetype
|-- manifest.json
|-- base/
|   |-- map.wbox
|   |-- map.meta             optional
|   `-- preview.png          optional
|-- layers/
|   |-- elevation.i16
|   |-- landform.u8
|   `-- material.u8
`-- modules/
    |-- hydrology/
    |   |-- analysis.json
    |   |-- filled_elevation.i16
    |   |-- flow_direction.u8
    |   |-- flow_accumulation.u32
    |   |-- streams.u8
    |   |-- watersheds.u32
    |   `-- stream_order.u8
    |-- hydrology.water_dynamics/
    |   |-- state.json
    |   `-- managed_water.u8
    |-- erosion.hydraulic/
    |   |-- analysis.json
    |   |-- result_elevation.i16
    |   `-- net_change.i32
    `-- export.projection/... future
```

The `mimetype` value is:

```text
application/vnd.terrainlab.wbxgeo+zip
```

## Core layers

All arrays are dense, row-major, and use WorldBox tile order:

```text
index = y * width + x
origin = south-west
rows = south-to-north
```

GeoTIFF export must reverse the raster row order because conventional raster
row zero is the north/top row.

| Layer | Storage | Meaning |
|---|---|---|
| `core.elevation` | little-endian signed Int16 | Authoritative elevation |
| `core.landform` | UInt8 | Plain, hill, mountain, channel, and related form |
| `core.material` | UInt8 | Soil, sand, rock, ice, lava, and related material |

Valid elevation is `-20000..9000` metres on a zero-metre sea-level datum.
Elevation value `9999` is reserved globally as `NODATA`; it is never a valid
height and must be masked out of rendering, statistics, interpolation,
hydrology, and erosion. A GeoTIFF export writes the same value as the band
NoData value. Signed Int16 keeps negative bathymetry and uses half the memory of
Float32 or Int32. Readers migrate legacy packages with a nonzero declared sea
level by shifting valid samples onto the zero datum.

`WorldTile.Height` is only a normalized runtime cache. It is not the
authoritative elevation representation, and `NODATA` cells are not copied into
that cache.

## Map budget

The budget is based on a `20 x 20` WorldBox-block map. One block is `64 x 64`
game cells.

```text
baseline = 20 * 64 * 20 * 64 = 1,638,400 cells
hard limit = baseline * 1.15 = 1,884,160 cells
```

Only total cell count is constrained. Aspect ratio is unrestricted because a
long, narrow canvas may be a valid result of a projection. Rendering and
analysis code must therefore use chunked textures and chunked processing, not a
single texture whose dimensions match the whole world.

Examples:

| WorldBox blocks | Game cells | Result |
|---|---:|---|
| `20 x 20` | 1,638,400 | accepted |
| `21 x 21` | 1,806,336 | accepted |
| `23 x 20` | 1,884,160 | accepted |
| `40 x 10` | 1,638,400 | accepted by WBXGEO |
| `22 x 22` | 1,982,464 | rejected |

WorldBox or Steam Workshop may impose additional UI restrictions. Those are
compatibility restrictions, not WBXGEO geometry restrictions.

## Manifest contract

The current core schema version is `1.1.0`. Major versions are compatibility
boundaries; readers accept compatible `1.x` packages and preserve unknown data.

```json
{
  "format": "wbxgeo",
  "schema_version": "1.1.0",
  "package_role": "worldbox-overlay",
  "project_id": "2f43d7d9-0d3f-41f8-9eb0-16576633200d",
  "base_map": {
    "entry": "base/map.wbox",
    "sha256": "...",
    "worldbox_save_version": 17
  },
  "canvas": {
    "width_cells": 1280,
    "height_cells": 1280,
    "width_blocks": 20,
    "height_blocks": 20,
    "cell_count": 1638400,
    "maximum_cell_count": 1884160,
    "origin": "south-west",
    "row_order": "south-to-north",
    "cell_size": 1000.0
  },
  "crs": {
    "type": "ENGCRS",
    "name": "WorldBox / 2f43d7d9-0d3f-41f8-9eb0-16576633200d",
    "horizontal_unit": "metre"
  },
  "vertical_reference": {
    "datum": "worldbox-local",
    "unit": "metre",
    "sea_level": 0,
    "storage_type": "int16",
    "nodata": 9999
  },
  "layers": [],
  "modules": [],
  "compatibility": {
    "vanilla_fallback": true,
    "external_map_name": "map.wbox",
    "unknown_optional_modules": "preserve"
  }
}
```

Every core and module layer has a SHA-256 checksum. The embedded base map hash
must match the adjacent `map.wbox` before TerrainLab applies the overlay.
`canvas.cell_size` is the horizontal ground distance represented by one raster
cell. New projects default to `1000 m`; schema `1.0.x` manifests using
`worldbox_tile` are migrated with one legacy tile equal to `1000 m`.

## Module extension contract

A module owns exactly one entry prefix:

```text
modules/<module-id>/
```

Module IDs use lowercase ASCII letters, digits, dots, underscores, and hyphens.
They start with a letter or digit and are at most 64 characters long.

Manifest entry:

```json
{
  "id": "export.projection",
  "schema_version": "1.0.0",
  "required": false,
  "entry_prefix": "modules/export.projection/",
  "metadata": {}
}
```

Registered modules may:

- write private files under their prefix;
- declare raster or analytical layers in the shared layer catalog;
- store their own schema version and metadata;
- mark themselves required only when the project cannot be interpreted safely
  without them.

Unknown optional modules and their ZIP entries are copied unchanged when the
project is saved. Unknown required modules make the extended project
unavailable, but the embedded or adjacent vanilla map remains recoverable.

First-party and reserved module IDs:

```text
hydrology
hydrology.water_dynamics
erosion.hydraulic
erosion.thermal
export.gis
export.projection
georeference
qgis.sync
```

### Hydrology module

TerrainLab 1.0 implements optional module `hydrology` schema `1.1.0`. Its
`analysis.json` records the `priority-flood-d8` algorithm version, source grid
SHA-256, threshold, timestamp, dimensions, and diagnostics. A ready result adds:

| Layer | Storage | NODATA | Meaning |
|---|---|---:|---|
| `hydrology.filled_elevation` | Int16 | `9999` | Non-destructive Priority-Flood surface |
| `hydrology.flow_direction` | UInt8 | `255` | D8 receiver: E, NE, N, NW, W, SW, S, SE = 0..7 |
| `hydrology.flow_accumulation` | UInt32 | `0` | Contributing cells including the current cell |
| `hydrology.streams` | UInt8 | `255` | `1` stream, `0` valid non-stream cell |
| `hydrology.watersheds` | UInt32 | `0` | Stable outlet-based watershed ID |
| `hydrology.stream_order` | UInt8 | `255` | Strahler order on stream cells, `0` off-stream |

The source checksum covers little-endian elevation plus the landform grid.
TerrainLab ignores an optional hydrology payload when that checksum, dimensions,
layer lengths, or per-layer checksums do not match. A stale or uncomputed module
keeps only `analysis.json`; it never modifies the authoritative DEM during save.

Readers migrate valid hydrology schema `1.0.x` payloads by deriving watersheds
and stream order from the stored D8 graph. Invalid optional data is discarded as
one module; it does not invalidate the core project.

### Live water module

TerrainLab implements optional module `hydrology.water_dynamics` schema `1.4.0`.
`state.json` stores whether routing is enabled, normalized integer parameters,
managed-cell count, injected/consumed volume counters, and observed native
geyser pulse count. Parameter `routing_algorithm` is one of `d8`, `dinf`, or
`mfd`; valid `1.0.x` payloads without it migrate to `d8`.

| Layer | Storage | NODATA | Meaning |
|---|---|---:|---|
| `hydrology.water_dynamics.managed_mask` | UInt8 | `255` | `1` for water cells created and budgeted by TerrainLab, `0` otherwise |
| `hydrology.water_dynamics.water_storage` | UInt8 | none | Saturating local water depth/reserve; `0` is dry |
| `hydrology.water_dynamics.restore_surface` | UInt8 | none | One-based index into the saved pre-water surface palette; `0` is fallback |
| `hydrology.water_dynamics.hydro_feature` | UInt8 | `255` | Persistent `0` none, `1` river, `2` waterbody class, including dry channels |
| `hydrology.water_dynamics.moisture` | UInt8 | none | Dynamic substrate moisture from dry `0` to saturated `255` |
| `hydrology.water_dynamics.erodibility` | UInt8 | `255` | Dynamic detachment coefficient; `0` is not initialized and valid values end at `254` |
| `hydrology.water_dynamics.local_slope` | UInt8 | `255` | Metric Horn slope: `0..254` maps to `0..pi/2` radians |
| `hydrology.water_dynamics.local_aspect` | UInt8 | `255` | Downslope aspect: `0..254` maps to `0..2*pi` radians |

The loader requires exact project ID, dimensions, layer length, SHA-256, binary
mask and hydro-feature values, mutually consistent storage/restore rasters, and
a managed-cell count matching the raster. Invalid data drops only this optional
module. Source queues and remaining finite volume are not serialized: loading
restores managed water, dry hydro features, dynamic substrate fields, and the
pre-water surface but does not inject another ordinary source budget. Valid
`1.0.x` and `1.1.x` payloads migrate with shallow default storage and safe
river/waterbody values; `1.2.x` adds those values to its existing water balance.
Schema `1.3.x` local slope and aspect values are recalculated with the core
metric cell size during load.
Later native geyser pulses can continue replenishing a source. The configured
flood percentage is normalized to `1..50`, and runtime
code always enforces the same hard 50-percent ceiling over valid DEM cells.
The algorithm choice controls only new live-water channel fronts; analytical
hydrology and erosion remain on their independently versioned D8 graphs.

### Erosion module

TerrainLab 1.0 implements optional module `erosion.hydraulic` schema `1.0.0`.
Its `analysis.json` records the `fixed-d8-mass-transfer` algorithm, parameters,
source SHA-256, dimensions, timestamps, and exact mass diagnostics.

| Layer | Storage | NODATA | Meaning |
|---|---|---:|---|
| `erosion.result_elevation` | Int16 | `9999` | Preview DEM after integer transport |
| `erosion.net_change` | Int32 | `-2147483648` | Result minus source elevation |

The source checksum binds elevation and landform to the D8 algorithm version.
The loader verifies every cell (`result - source = net change`), NODATA masks,
checksums, dimensions, and zero initial/final mass difference. Loading a valid
preview never applies it to the authoritative DEM automatically.

## External GIS exchange

GeoTIFF exports and file-sync state are intentionally outside WBXGEO. This keeps
the portable game project independent from transient QGIS working files. See
[TerrainLab file sync](FILE_SYNC.md) for the strict raster profile, baseline
hashes, conflict policies, branch files, and change log.

## Save and load lifecycle

Save:

1. WorldBox writes its normal `map.wbox`, metadata, and preview.
2. TerrainLab captures or updates its authoritative arrays.
3. TerrainLab hashes `map.wbox` and writes a temporary WBXGEO package.
4. The package replaces the previous sidecar atomically.
5. Failure in steps 2-4 is logged and does not invalidate the vanilla save.

Load:

1. WorldBox loads `map.wbox` normally.
2. TerrainLab opens the adjacent `terrainlab.wbxgeo`.
3. Schema, dimensions, cell budget, lengths, and checksums are validated.
4. The base map hash is compared with the map WorldBox loaded.
5. Valid continuous layers are attached to the world state.
6. On any mismatch, TerrainLab bootstraps a lossy state from vanilla tile types.

Steam uploads the whole map directory, so the standard files and sidecar travel
together. A direct WBXGEO import extracts only the fixed `base/*` payload names;
arbitrary ZIP paths are never extracted. TerrainLab validates the embedded base
map hash before extracting into a temporary directory, then renames that
directory into the first free `saveN` slot.
