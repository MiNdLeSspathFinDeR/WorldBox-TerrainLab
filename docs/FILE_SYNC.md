# TerrainLab file sync 1.0

TerrainLab 1.0 exposes a local, tool-neutral exchange contract for QGIS and
other GIS software. It does not open a network port or launch an external
process. A future QGIS plugin can automate these same files without changing
the game-side data model.

## Workspace

The project view creates:

```text
<persistent data>/TerrainLab/Exchange/Sync/<project-id>/
|-- baseline.json
|-- changes.jsonl                 created after the first processed input
|-- README.txt
|-- outgoing/
|   |-- elevation.tif
|   |-- elevation.tfw
|   `-- elevation.prj
|-- incoming/
|   `-- elevation.tif            supplied by the GIS editor
|-- history/
|   `-- incoming-<utc>.tif       consumed inputs
`-- branches/
    |-- world-<utc>.tif          local DEM preserved during conflict resolution
    `-- world-<utc>.json
```

`Prepare` refuses to replace the baseline while an incoming TIFF exists. This
prevents an accidental refresh from hiding a real edit conflict.

## Raster contract

The elevation exchange raster is:

- classic little-endian TIFF;
- one uncompressed band;
- signed Int16 samples;
- every data sample in `-20000..9000` metres;
- exact project width and height;
- GDAL NODATA tag `9999`;
- pixel-is-area, one WorldBox tile per pixel;
- file row zero at the north/top edge;
- user-defined local model in GeoTIFF and a WorldBox WKT2 `ENGCRS` in `.prj`;
- world-file origin at the center of the north-west pixel.

TerrainLab's in-memory and WBXGEO row order starts at the south-west. Export and
import reverse rows exactly once. Landform and material do not change when an
external elevation grid is applied.

The protected importer rejects a missing/wrong NODATA tag, samples outside the
DEM domain, another sample type, compression, extra bands, inconsistent strips,
unexpected dimensions, and any offset outside the file. The source file is
copied to a private staging file and must remain unchanged during that copy.

## Baseline and conflicts

`baseline.json` records format/schema, project ID, source revision, dimensions,
data type, NODATA, UTC export time, and SHA-256 of the south-first little-endian
Int16 elevation array.

On pull, TerrainLab compares three DEMs:

1. baseline: the DEM last exported to `outgoing`;
2. world: the current in-game DEM;
3. incoming: the externally edited DEM.

A world hash different from the baseline hash is a conflict. The UI exposes two
policies:

- `Pull safe`: reject the input and leave it in `incoming`;
- `Pull + branch`: write the current world DEM to `branches`, then apply the
  incoming DEM.

The backend also defines `prefer_world` and `prefer_incoming` policies for a
future integration layer, but the 1.0 UI does not offer silent overwrite.

After a successful or no-op pull, the input moves to `history`, `outgoing` and
the baseline are refreshed, and one compact JSON object is appended to
`changes.jsonl`. The record contains policy, outcome, baseline/world/incoming
and final hashes, changed-cell count, conflict flag, archive, and branch path.

Applying an incoming DEM increments the project revision, invalidates relief,
hydrology, and erosion previews, updates WorldBox's height cache where possible,
and enters the complete grid replacement as one undoable TerrainLab edit.

## Manual QGIS workflow

1. In TerrainLab, select `Map` and run `Prepare`.
2. Open `outgoing/elevation.tif` in QGIS.
3. Edit or process the raster while preserving the profile above.
4. Export to a temporary file in `incoming`, then rename it to `elevation.tif`
   after the write completes.
5. Use `Pull safe`. If TerrainLab reports a conflict, inspect it and use
   `Pull + branch` only when the incoming DEM should win.
6. Recalculate relief and hydrology before running erosion again.

For a one-way analytical export, use `GeoTIFF` in the project view. It writes a
timestamped directory containing every currently ready core, relief, hydrology,
and erosion raster plus `terrainlab-gis.json` with checksums.

## 1.0 boundary

This release synchronizes the authoritative DEM only. It does not yet import
vector Simple Features, assign a real-world CRS, reproject rasters, merge cells,
or maintain a live socket connection. Those are separate modules that can use
the stable project ID, revision, layer catalog, and baseline protocol introduced
here.
