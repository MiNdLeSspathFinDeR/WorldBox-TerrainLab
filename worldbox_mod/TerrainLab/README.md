# TerrainLab WorldBox mod

NML mod that adds a transparent side button around the TerrainLab icon. The
button opens a window cloned from WorldBox's `windows/empty` prefab. The
project view saves, validates, exports, and imports WBXGEO packages. The relief
view inspects all core layers and edits the independent Int16 elevation grid.
Hydrology and erosion remain reserved module slots.

The source mod is installed into `<WorldBox>/Mods/TerrainLab`. NML compiles the
files under `Code` when the game starts.

Version 0.4 exposes WBXGEO persistence and the first DEM editor through the game UI:

- the vanilla `map.wbox` remains the fallback save;
- `terrainlab.wbxgeo` embeds that save and the authoritative GIS arrays;
- elevation is signed Int16 with `9999` reserved as `NODATA`;
- map size is limited by total cell count, not aspect ratio;
- optional module payloads have isolated namespaces and survive round trips;
- exports are written to `<WorldBox persistent data>/TerrainLab/Exchange`;
- imports are validated and atomically installed into the first free `saveN`.
- inspect, set, raise, lower, and smooth tools operate directly over the map;
- the bottom strip reports cell coordinates, elevation, CRS, and budget usage;
- elevation edits have a 32-operation undo/redo history and preserve vanilla
  terrain morphotypes.

See `docs/WBXGEO_FORMAT.md` for the package contract and
`docs/TERRAINLAB_UI_ASSETS.md` for the UI art list.

Run the package round-trip probe from the repository root:

```powershell
dotnet run --project worldbox_mod/TerrainLab.PackageProbe/TerrainLab.PackageProbe.csproj -c Release
```
