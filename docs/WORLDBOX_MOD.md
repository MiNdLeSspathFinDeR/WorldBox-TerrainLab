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

The first UI revision exposes five module entries: map, relief, hydrology,
erosion, and settings. They are stable integration points for the GIS pipeline;
the buttons currently select a module but do not mutate the world.

## Extended project persistence

TerrainLab patches the normal save lifecycle without replacing it. WorldBox
writes `map.wbox` first, then TerrainLab writes `terrainlab.wbxgeo` beside it.
The WBXGEO package embeds the same vanilla save plus signed Int16 elevation,
landform, surface material, and future module payloads. `9999` is always
elevation `NODATA`.

The project budget is 1,884,160 game cells: a `20 x 20` block baseline plus
15%. Aspect ratio is unrestricted. Over-budget maps continue to save as vanilla
WorldBox maps, but TerrainLab does not allocate or write GIS layers for them.

Steam Workshop uploads the complete save directory, so the vanilla map and the
sidecar are transferred together. Square custom maps within the TerrainLab
budget pass the patched size validator. Direct `.wbxgeo` transfer also supports
rectangular and extremely elongated projects; a dedicated import/export UI is
the next interface increment.

Format details: [WBXGEO overlay format](WBXGEO_FORMAT.md).

Art and toolbar plan: [TerrainLab UI assets](TERRAINLAB_UI_ASSETS.md).
