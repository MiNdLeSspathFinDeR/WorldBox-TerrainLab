# WorldBox TerrainLab
TerrainLab combines an adaptive image-to-map converter with an extensible GIS
project layer for WorldBox.

> [!IMPORTANT]
> TerrainLab 1.6 keeps WorldBox compatibility first: WBXGEO and scientific
> layers are additive, while every normal save retains its vanilla `map.wbox`.

## Table of contents
- [Key Features](#key-features)
- [Installation](#installation)
  - [PC](#pc)
  - [Android](#android)
- [WorldBox mod](#worldbox-mod)
- [Usage](#usage)
  - [Simple](#simple)
  - [Config File](#config-file)
  - [Parameters](#parameters)

## Key Features
1. Adaptive terrain: water, shorelines, relief, and land classes are inferred against each source image's own color and contrast range.
2. Gameplay-safe by default: explosives, lava, grey goo, corrupted terrain, and other disruptive tiles are excluded.
3. Direct WorldBox saves: complete `saveN` folders can be created in the game's save directory.
4. Auto sizing: if only one size parameter is set, the other is calculated from the image ratio.
5. Configurable: the legacy direct-palette algorithm and custom tile sets remain available.
6. In-game GIS: editable `-20000..9000 m` Int16 DEM, fixed-scale translucent
   Turbo height display, Earth-like morphotype-aware initial elevations with a
   metric `1000 m/cell` default and spatial grade limiting,
   relief derivatives, Priority-Flood/D8, watersheds, Strahler order,
   selectable D8/D-infinity/MFD live-water channels grown as connected
   three-cell line/corner fragments, absolute 0/-5/-150 m water
   classes, persistent river/waterbody zones, connected terminal lakes,
   bounded one-time confluence growth, stable three-cell land components with
   cleanup of enclosed one-to-two-cell islands,
   geyser-fed recharge and source removal, one-to-two-cell alluvial banks,
   dry sandy ravines, moisture, material resistance, soil degradation,
   bounded DEM channel incision, deterministic erosion, two-point DEM grading,
   contours, categorical core layers, and chunked live overlays.
7. GIS exchange: strict GeoTIFF export/import with baseline hashes, conflict
   detection, branches, and an append-only change log.
8. QGIS-style workspace: routine project, DEM, analysis, and overlay commands
   live in a compact three-row top toolbar; parameters and diagnostics remain in
   the internal window.
9. In-game image import: a persisted watched folder queues stable rasters,
   converts one at a time with the safe adaptive classifier, preserves extreme
   aspect ratios inside the shared cell budget, and atomically creates complete
   new WorldBox save slots without changing the open world.

The implemented terrain-analysis stages are described in
[GIS terrain pipeline](docs/GIS_PIPELINE.md). The
in-game NML mod stores extended projects in the portable
[WBXGEO overlay format](docs/WBXGEO_FORMAT.md), while preserving a normal
WorldBox map for users without the mod. The QGIS-facing local exchange contract
is documented in [TerrainLab file sync](docs/FILE_SYNC.md).

## Installation
### PC
1. Download and install [Python](https://www.python.org/downloads/)  (Ver. 3.8+)
2. Install the tool:
```sh
pip install git+https://github.com/MiNdLeSspathFinDeR/WorldBox-TerrainLab
```

### Android
1. Download and Install [Termux from F-Droid](https://f-droid.org/packages/com.termux/)
2.  Run the following commands to install Python and the tool's requirements:
```sh
pkg upgrade -y && pkg install python python-pillow git -y
```
3. Install the tool:
```sh
pip install git+https://github.com/MiNdLeSspathFinDeR/WorldBox-TerrainLab
```

## WorldBox mod

TerrainLab requires NeoModLoader and WorldBox experimental mode. Install the
source mod from a repository checkout with:

```powershell
worldbox_mod\install.cmd
```

The in-game three-row toolbar saves and validates WBXGEO, watches an image
workspace, edits the Int16 DEM, runs relief/hydrology/erosion jobs, switches
derived map layers, exports GeoTIFF, and operates protected file sync. The
coordinate/elevation status strip appears only with the Inspector tool. The
internal Project page opens the watched folder and reports its queue,
converter, active file, and failures. The in-game watcher accepts `PNG`,
`JPG/JPEG/JFIF`, `TIFF/TIF`, `WebP`, `BMP`, `GIF`, `TGA`, `DDS`, and `JP2`;
it does not accept SVG, PDF, PSD, or archives. Drop one of those rasters there
after enabling the watcher; each stable image becomes a new `saveN` slot while
the open world remains untouched. The Project chapter's image-folder command
only opens `ImageWorkspace` in the operating-system file browser; selecting a
file there does not select it in WorldBox.

The same image-import group offers two independent paths. **Automatic
clustering** is the fast path: confirm a source raster, optionally outline one
area-of-interest boundary, tune five basic controls or reveal ten expert
controls, then build the map. The boundary excludes legends, margins, and other
background from cluster fitting and turns its exterior into deep ocean.
Settings and the boundary persist per source in
`<image>.terrainlab-clustering.json`. The 15 controls cover cluster count,
multiscale spline radius, cleanup, water sensitivity, colour, luminance,
saturation, texture, edge and spatial weights, retained detail, sample budget,
K-means iterations, and deterministic seed.

**Manual classification** opens a compact previous/next file selector for
deliberate training. Choose the source raster and press
**Open selected** before editing controls unlock. Choose Point, Line, or
Polygon and digitize its geometry first. A point completes on click; a line or
polygon completes by right-click, double-click, or **Finish geometry**.
Morphotype, biotope, elevation, and line width remain disabled until geometry
is complete. **Publish object** is the only operation that writes the feature:
the saved object stays visible while all choices reset for the next one.
Completed polygon drafts update their pixel-art morphotype fill immediately,
and every point or vector vertex uses the Turbo DEM colour for its selected
`-20000..9000 m` elevation.
Boundary mode digitizes one independent area-of-interest polygon. Pixels
outside it are removed from adaptive clustering and manual learning. A
separate outside-extent block chooses their safe morphotype, biotope, and
elevation; `deep_ocean` at `-4000 m` remains the default. This keeps scan
margins, legends, labels, and other surrounding noise out of the playable map.
Points, lines, polygons, and the optional boundary persist beside the source in
`<image>.terrainlab-classification.json`; Build map reruns the adaptive
colour/texture/spatial classifier. Polygon interiors are authoritative and
provide bounded distributed training pixels without bloating the profile.
Lines are authoritative at their selected `1..32`-cell output width.
Each raster has its own sidecar, and the current profile is saved before the
file selector switches to another raster. Delete one removes the training
polygon clicked on the canvas; Delete all immediately removes every training
polygon while preserving point samples and the map boundary.
The conversion publishes `terrainlab-elevation.tif` with the new save so
TerrainLab can restore the interpolated Int16 DEM when that world is opened.
Save opens a name form before writing the
ordinary map and WBXGEO sidecar. The other pages hold numeric parameters, layer
diagnostics, and settings. Erosion provides a deterministic,
exact-mass-balance preview that can be applied as one undoable DEM edit. See
[TerrainLab in WorldBox](docs/WORLDBOX_MOD.md) for paths and lifecycle details.

## Usage
### Simple
Every image format that [PIL (pillow) supports](https://pillow.readthedocs.io/en/stable/handbook/image-file-formats.html#image-file-formats) is supported by this tool. You can quickly convert an image with this command:
```sh
imagetomap image.png
```

> [!Note]
Please note that the WorldBox map's dimensions need to be a multiple of 64, so for some images' sizes that are not multiples of 64, resizing will be necessary. This can lead to the images having unintended artifacts.

The converted image will then be stored in a folder with the image's name in the current directory.

To save the converted map directly into WorldBox saves as a new `saveN` slot:
```sh
imagetomap image.png --save-to-game
```

For a maximum-baseline 20 by 20 world using adaptive terrain and the safe palette:
```sh
imagetomap image.png --save-to-game -W 20 -H 20 --algorithm terrain --palette safe
```

TerrainLab limits maps to 1,884,160 game cells: the cell count of a 20 by 20
WorldBox-block map plus 15%. Aspect ratio is unrestricted, so a 40 by 10 map is
valid while a 22 by 22 map is rejected.
To select the largest allowed dimensions while retaining the source aspect:
```sh
imagetomap image.png --fit-budget --save-to-game
```

On Windows the tool auto-detects the default WorldBox saves folder:
`%USERPROFILE%\AppData\LocalLow\mkarpenko\WorldBox\saves`.
You can override it:
```sh
imagetomap image.png --game-saves-dir "C:\Path\To\WorldBox\saves"
```

To keep a workspace folder open and convert images when they are added or changed:
```sh
imagetomap Workspace/ --watch --save-to-game
```

You can convert multiple images in one go by simply giving more to the program:
```sh
imagetomap image.png image_two.png
```

Or by giving the program a folder, of which it will search for images inside of it:
```sh
imagetomap folder/
```

There are some parameters that be set. See:
```sh
imagetomap --help
```

### Config File
You can configure tiles you want or do not want the program to use with the config file. Run the following command to generate one:
```sh
imagetomap --generate-config
```

After that, the program will create an `itm_config.json` file in the current directory. The file should look like this:
```json
{
    "output": "./",
    "algorithm": "terrain",
    "palette": "safe",
    "terrain_clusters": 14,
    "terrain_smooth": 1,
    "terrain_min_region": 32,
    "tiles": {
        "deep_ocean": true,
        "close_ocean": true,
        "shallow_waters": true,
        "sand": true,
        "soil_low": true,
        "soil_high": true,
        "soil_low:grass_low": true,
        "soil_high:grass_high": true,
        "soil_low:mushroom_low": false,
        "soil_high:mushroom_high": false,
        "soil_low:corrupted_low": false,
        "soil_high:corrupted_high": false,
        "soil_low:infernal_low": false,
        "soil_high:infernal_high": false,
        "soil_low:candy_low": false,
        "soil_high:candy_high": false,
        "soil_low:crystal_low": false,
        "soil_high:crystal_high": false,
        "soil_low:permafrost_low": true,
        "soil_high:permafrost_high": true,
        "soil_low:savanna_low": true,
        "soil_high:savanna_high": true,
        "soil_low:enchanted_low": true,
        "soil_high:enchanted_high": true,
        "soil_low:swamp_low": true,
        "soil_high:swamp_high": true,
        "soil_low:jungle_low": true,
        "soil_high:jungle_high": true,
        "soil_low:desert_low": true,
        "soil_high:desert_high": true,
        "soil_low:lemon_low": false,
        "soil_high:lemon_high": false,
        "soil_low:waste_low": false,
        "soil_high:waste_high": false,
        "soil_low:tumor_low": false,
        "soil_high:tumor_high": false,
        "soil_low:biomass_low": false,
        "soil_high:biomass_high": false,
        "soil_low:pumpkin_low": false,
        "soil_high:pumpkin_high": false,
        "soil_low:cybertile_low": false,
        "soil_high:cybertile_high": false,
        "lava3": false,
        "lava2": false,
        "lava1": false,
        "lava0": false,
        "pit_deep_ocean:tnt": false,
        "pit_deep_ocean:water_bomb": false,
        "pit_deep_ocean:tnt_timed": false,
        "pit_deep_ocean:landmine": false,
        "pit_deep_ocean:fuse": false,
        "pit_deep_ocean:fireworks": false,
        "pit_deep_ocean:field": false,
        "pit_deep_ocean:road": false,
        "hills": true,
        "mountains": true,
        "grey_goo": false
    }
}
```

You can exclude tiles by changing their values to `false`.
When `palette` is `safe`, unsafe entries remain excluded even if an older config enables them. Use `"palette": "full"` only for intentionally destructive or experimental maps.

The program will use the `itm_config.json` file in the current directory by default. But if you want to, you can make it ignore the file by adding the`--no-config` option:
```sh
imagetomap image.png --no-config
```

### Parameters
#### `images`
Image files to convert to maps. If this option is supplied with folders, the program will search for images inside them.
```sh
imagetomap image.png
imagetomap image.png image_two.png
imagetomap folder/
```

#### `--no-config`
Ignore the config file.
```sh
imagetomap image.png --no-config
```

#### `--generate-config`
Generate a config file in the current directory.
```sh
imagetomap --generate-config
```

#### `--algorithm`
Choose `terrain` for adaptive semantic conversion (default), or `palette` for the original direct RGB matching.
```sh
imagetomap image.png --algorithm terrain
```

#### `--palette`
Choose the gameplay-safe tile set (default) or explicitly opt into every known tile.
```sh
imagetomap image.png --palette safe
```

#### `--terrain-clusters`
Number of adaptive land clusters, from 4 to 64. Default is `14`.
```sh
imagetomap image.png --terrain-clusters 18
```

#### `--terrain-smooth`
Number of isolated-tile cleanup passes, from 0 to 8. Default is `1`.
```sh
imagetomap image.png --terrain-smooth 2
```

#### `--terrain-min-region`
Convert isolated land components below this tile area back to water. Default is `32`; use `0` to preserve every source mark. Scale the value by four when width and height are doubled and you want the same visual cleanup.
```sh
imagetomap image.png --terrain-min-region 128
```

#### `-D, --dither`
Enable dithering for smoother colour transitions. Not recommended for maps designed to actually be played with.
```sh
imagetomap image.png --dither
```

#### `-W, --width`
Target width of the map(s). Default to auto.
```sh
imagetomap image.png --width 8
```

#### `-H, --height`
Target height of the map(s). Default to auto.
```sh
imagetomap image.png --height 8
```

#### `-R, --recursive`
Enable recursive searching inside folders.
```sh
imagetomap folder/ --recursive
```

#### `-O, --output`
Where to save the converted maps and previews. Default to the current directory.
```sh
imagetomap image.png --output Converted/
```

#### `--map-name`
World name stored in the generated map metadata. Defaults to the image file name.
```sh
imagetomap image.png --map-name "Imported World"
```

#### `--save-to-game`
Save converted maps directly into the WorldBox saves folder as new `saveN` slots.
The tool writes `map.wbox`, `map.meta`, `preview.png`, `preview_small.png`, and
`map_stats.s3db` into a staging folder, then publishes the complete slot with
one directory rename.
```sh
imagetomap image.png --save-to-game
```

#### `--fit-budget`
When width and height are automatic, choose the largest block dimensions that
preserve the source aspect ratio within TerrainLab's 1,884,160-cell budget.
Square images become 21 by 21 blocks; extreme projection aspect ratios remain
valid.
```sh
imagetomap image.png --fit-budget
```

#### `--game-saves-dir`
Override the WorldBox saves folder. This also enables `--save-to-game`.
```sh
imagetomap image.png --game-saves-dir "C:\Path\To\WorldBox\saves"
```

#### `--watch`
Keep running and convert images when they are added or changed in the supplied folder.
If no image or folder is supplied, the current directory is watched.
```sh
imagetomap Workspace/ --watch --save-to-game
```

#### `--watch-interval`
Seconds between workspace scans in `--watch` mode. Default is `2.0`.
```sh
imagetomap Workspace/ --watch --watch-interval 1
```

#### `--clustering-profile`
Load an automatic-clustering profile containing all 15 settings and an optional
area-of-interest boundary. When neither profile option is supplied,
`<image>.terrainlab-clustering.json` is discovered automatically only if no
manual profile is selected.
```sh
imagetomap image.tif --clustering-profile image.tif.terrainlab-clustering.json
```

#### `--classification-profile`
Load a manual point/line/polygon classification and Int16 DEM profile. Manual
classification and automatic clustering profiles are intentionally mutually
exclusive.
```sh
imagetomap image.tif --classification-profile image.tif.terrainlab-classification.json
```
