# TerrainLab in WorldBox

`worldbox_mod/TerrainLab` is an NML source mod. It adds a clickable icon on the
right side of the UI and opens a stock `ScrollWindow` created from the game's
own `windows/empty` prefab.

## Local paths

- NML loader: `<WorldBox>/worldbox_Data/StreamingAssets/mods/NeoModLoader.dll`
- User mods: `<WorldBox>/Mods`
- TerrainLab: `<WorldBox>/Mods/TerrainLab`

WorldBox experimental mode must be enabled for native mods. Run
`worldbox_mod/install.cmd` after installing NML. The installer builds the source
against the local game assemblies and copies only TerrainLab files. The CMD
wrapper also works when the system PowerShell execution policy blocks local
scripts.

## Current menu

The UI exposes five stable module entries: map, relief, hydrology, erosion, and
settings. The map view provides working commands to save, validate, and export
the current WBXGEO project. It lists the three newest packages in the TerrainLab
exchange directory and imports a selected package into the first free WorldBox
`saveN` directory.

The relief view is a read-only inspector for the authoritative elevation,
landform, surface-material, and embedded vanilla layers. Hydrology and erosion
remain explicit reserved slots until their processing backends are added.

The exchange directory is
`<WorldBox persistent data>/TerrainLab/Exchange`. Import first validates the
manifest, all core-layer checksums, the embedded `map.wbox`, dimensions, and
the `NODATA=9999` contract. Extraction happens in a temporary directory that is
renamed into place, so an interrupted import does not leave a partial save
slot. Imported worlds are opened through WorldBox's normal save list.

## Extended project persistence

TerrainLab patches the normal save lifecycle without replacing it. WorldBox
writes `map.wbox` first, then TerrainLab writes `terrainlab.wbxgeo` beside it.
The WBXGEO package embeds the same vanilla save plus signed Int16 elevation,
landform, surface material, and future module payloads. `9999` is always
elevation `NODATA`.

WorldBox autosaves remain vanilla and do not receive GIS sidecars. This avoids
duplicating the continuous layers on every autosave cycle. TerrainLab emits a
sidecar for completed normal or Workshop saves that contain `map.wbox`.

The project budget is 1,884,160 game cells: a `20 x 20` block baseline plus
15%. Aspect ratio is unrestricted. Over-budget maps continue to save as vanilla
WorldBox maps, but TerrainLab does not allocate or write GIS layers for them.

Steam Workshop uploads the complete save directory, so the vanilla map and the
sidecar are transferred together. Square custom maps within the TerrainLab
budget pass the patched size validator. Direct `.wbxgeo` exchange supports
rectangular and extremely elongated projects through the project view.

Format details: [WBXGEO overlay format](WBXGEO_FORMAT.md).

Art and toolbar plan: [TerrainLab UI assets](TERRAINLAB_UI_ASSETS.md).
