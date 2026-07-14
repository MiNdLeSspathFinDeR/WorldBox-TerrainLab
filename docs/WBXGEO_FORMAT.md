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
    |-- hydrology/...        future
    |-- erosion/...          future
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

Elevation value `9999` is reserved globally as `NODATA`. It is never a valid
height and must be masked out of rendering, statistics, interpolation,
hydrology, and erosion. A GeoTIFF export writes the same value as the band
NoData value. Signed Int16 keeps negative bathymetry and uses half the memory of
Float32 or Int32.

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

The initial schema version is `1.0.0`. Major versions are compatibility
boundaries; readers may accept newer minor versions and preserve unknown data.

```json
{
  "format": "wbxgeo",
  "schema_version": "1.0.0",
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
    "cell_size": 1.0
  },
  "crs": {
    "type": "ENGCRS",
    "name": "WorldBox / 2f43d7d9-0d3f-41f8-9eb0-16576633200d",
    "horizontal_unit": "worldbox_tile"
  },
  "vertical_reference": {
    "datum": "worldbox-local",
    "unit": "worldbox-height",
    "sea_level": 98,
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

Planned first-party module IDs:

```text
hydrology
erosion.hydraulic
erosion.thermal
export.gis
export.projection
georeference
qgis.sync
```

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
arbitrary ZIP paths are never extracted.
