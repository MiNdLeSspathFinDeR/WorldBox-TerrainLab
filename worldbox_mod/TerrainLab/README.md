# TerrainLab WorldBox mod

NML mod that adds a transparent side button around the TerrainLab icon. The
button opens a window cloned from WorldBox's `windows/empty` prefab. The
first menu contains placeholders for the map, relief, hydrology, erosion, and
settings modules.

The source mod is installed into `<WorldBox>/Mods/TerrainLab`. NML compiles the
files under `Code` when the game starts.

Version 0.2 adds the WBXGEO persistence foundation:

- the vanilla `map.wbox` remains the fallback save;
- `terrainlab.wbxgeo` embeds that save and the authoritative GIS arrays;
- elevation is signed Int16 with `9999` reserved as `NODATA`;
- map size is limited by total cell count, not aspect ratio;
- optional module payloads have isolated namespaces and survive round trips.

See `docs/WBXGEO_FORMAT.md` for the package contract and
`docs/TERRAINLAB_UI_ASSETS.md` for the UI art list.

Run the package round-trip probe from the repository root:

```powershell
dotnet run --project worldbox_mod/TerrainLab.PackageProbe/TerrainLab.PackageProbe.csproj -c Release
```
