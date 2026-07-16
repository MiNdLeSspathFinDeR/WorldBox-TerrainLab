# TerrainLab UI and asset brief

This is the drawing list for the GIS interface. It separates the minimum first
release from later scientific modules so assets can be produced in batches.

## Interface structure

The game map remains the central workspace. TerrainLab adds:

1. The existing standalone side button that opens TerrainLab.
2. A compact top toolbar with grouped icon tools, similar to desktop GIS.
3. A collapsible left panel for the layer tree.
4. A collapsible right panel for active-tool and layer properties.
5. A bottom status strip for coordinates, elevation, CRS, scale, progress, and
   cell-budget usage.
6. Stock WorldBox window and nine-slice panel graphics for every panel body.

The UI should not place the map inside a decorative card. Panels overlay or dock
against screen edges and can be collapsed independently.

## Implementation status

Version 1.0.0 implements the standalone side button, an adaptive top GIS
toolbar, and a stock WorldBox internal window. The toolbar copies the bottom
WorldBox panel and button sprites, stretches to the logical canvas width, and
balances commands across as few rows as the current UI scale permits. Its frame
is vertically flipped for top docking, reaches both canvas edges, and follows
the actual row count without a redundant status caption. Unflipped, clipped
copies of the stock frame restore the side ornament up to the top canvas edge.
The permanent control row uses red buttons for critical menu/save actions and
gray chapter buttons for Project, Terrain, Digitizing, Analysis, and Layers.
Selecting a chapter replaces the functional row below it. Functional groups
retain distinct outline colors and matching flag separators; selection and
readiness use WorldBox's native `ToggleIcon.spriteON/spriteOFF` lamps, while
running jobs and the settings level use amber. Re-clicking an active chapter,
map tool, or derived layer switches it off, matching the base-game power bar.
Row breaks prefer semantic group boundaries.
Active tools and overlays are highlighted, unavailable operations are disabled,
and running jobs switch from play to pause.

Surface digitizing includes a gameplay-safe eyedropper, four-connected bucket
fill, multi-vertex line, filled polygon, rectangle, connected-region
polygonization, and apply-selection. The eyedropper rejects explosive,
damaging, spreading, lava, acid, `grey_goo`, TNT, and mine-like surfaces. These
operations copy vanilla surface type and frozen state but never overwrite the
independent Int16 DEM.

Hydrology exposes Priority-Flood/D8, streams, accumulation, fill, watersheds,
and Strahler order. Erosion exposes integer process parameters, exact mass
diagnostics, four preview layers, and explicit apply. All analysis is cancellable
and all raster overlays use `256 x 256` chunks. The internal window now contains
only project/import details, algorithm parameters, the runtime layer catalog,
and format/exchange settings; it does not duplicate routine map commands.

The standalone side button cycles through three levels: off, toolbar only, and
toolbar plus general settings. Another press from settings closes the complete
workspace. Closing the settings window with its stock cross performs that same
next transition instead of falling back to toolbar-only mode. Three compact
native lamps below the side button expose the current level.

The docked layer tree, custom cursors, legends, and advanced layer styling remain
later interface batches. Their semantic command IDs below are unchanged, so
delivering art will not require WBXGEO or runtime API changes.

The first transparent 64 px draft pack has been audited. TerrainLab loads the
needed project, exchange, identify, elevation, history, visibility, layer,
GeoTIFF, sync, and module sprites through NML `GameResources`. Analysis jobs
reuse WorldBox's play/pause sprites. Compact ASCII badges disambiguate grouped
commands until the remaining purpose-drawn relief, hydrology, and erosion icons
are delivered; every toolbar control has a localized tooltip.

## Batch A: core command inventory

### Project and exchange

| Asset name | Visual idea | Command |
|---|---|---|
| `project_new` | blank folded map | New WBXGEO project |
| `project_open` | open map folder | Import WBXGEO |
| `project_save` | map with small disk | Save project |
| `export_wbxgeo` | map leaving a box | Export portable project |
| `export_vanilla` | WorldBox map sheet | Export vanilla fallback |
| `project_validate` | map with check mark | Validate package |
| `module_manager` | three connected blocks | Modules |

### Navigation and history

| Asset name | Visual idea | Command |
|---|---|---|
| `undo` | curved left arrow | Undo |
| `redo` | curved right arrow | Redo |
| `pan` | four-way hand or arrows | Pan |
| `zoom_in` | magnifier plus | Zoom in |
| `zoom_out` | magnifier minus | Zoom out |
| `zoom_extent` | map inside four corners | Full extent |
| `identify` | cursor with information dot | Inspect cell/layer |
| `measure` | ruler | Measure |

### Layers

| Asset name | Visual idea | Command |
|---|---|---|
| `layer_add_raster` | pixel grid plus | Add raster layer |
| `layer_add_vector` | nodes and line plus | Add vector layer |
| `layer_group` | stacked sheets | Add group |
| `layer_remove` | sheet minus | Remove layer |
| `layer_duplicate` | two sheets | Duplicate layer |
| `layer_up` | sheet and up arrow | Move up |
| `layer_down` | sheet and down arrow | Move down |
| `layer_properties` | sheet with sliders | Layer properties |
| `layer_style` | sheet with color swatch | Symbology |
| `layer_filter` | funnel | Layer filter |
| `visibility_on` | open eye | Visible |
| `visibility_off` | crossed eye | Hidden |
| `lock` | closed padlock | Lock edits |
| `unlock` | open padlock | Unlock edits |

### Elevation editing

| Asset name | Visual idea | Command |
|---|---|---|
| `elevation_set` | brush with level marker | Set elevation |
| `elevation_raise` | terrain and up arrow | Raise |
| `elevation_lower` | terrain and down arrow | Lower |
| `elevation_smooth` | sharp profile becoming smooth | Smooth |
| `elevation_flatten` | terrain under horizontal rule | Flatten |
| `elevation_ramp` | rising diagonal profile | Build ramp |
| `elevation_sample` | eyedropper over contour | Sample elevation |
| `sea_level` | water line with vertical ruler | Set sea level |
| `hillshade` | lit and shaded hill | Hillshade view |
| `hypsometry` | stepped color mountain | Elevation colors |
| `contours` | nested contour lines | Contour view |
| `slope` | triangle with angle mark | Slope view |
| `aspect` | hill with compass arrow | Aspect view |

### Surface digitizing

| Asset name | Visual idea | Command |
|---|---|---|
| `surface_sample` | eyedropper over one terrain pixel | Save safe surface |
| `surface_fill` | bucket entering a bounded region | Fill connected surface |
| `surface_line` | linked vertices over pixels | Draw multi-vertex line |
| `surface_polygon` | closed nodes over filled cells | Draw filled polygon |
| `surface_rectangle` | rectangular selection over cells | Draw rectangle |
| `surface_polygonize` | raster region becoming a boundary | Polygonize region |
| `selection_apply` | selected boundary with check mark | Apply sampled surface |
| `digitizing_cancel` | open polyline with cross | Cancel active geometry |

## Batch B: georeferencing and projection

| Asset name | Visual idea | Command |
|---|---|---|
| `crs_assign` | globe with label | Assign CRS |
| `crs_reproject` | two globes and arrow | Reproject |
| `projection_settings` | graticule with sliders | Projection settings |
| `gcp_add` | crosshair plus | Add control point |
| `gcp_move` | crosshair with arrows | Move control point |
| `gcp_remove` | crosshair minus | Remove control point |
| `warp` | bent grid | Warp raster |
| `grid` | coordinate grid | Grid overlay |
| `north_arrow` | compass north arrow | North indicator |
| `sync_qgis` | Q and map with two arrows | QGIS synchronization |
| `export_geotiff` | raster sheet marked TIF | GeoTIFF export |
| `export_geopackage` | database cylinder and geometry | GeoPackage export |

## Batch C: hydrology

| Asset name | Visual idea | Command |
|---|---|---|
| `sink_fill` | basin filling with water | Fill depressions |
| `sink_breach` | basin with cut channel | Breach depression |
| `flow_direction` | arrows descending a slope | Flow direction |
| `flow_accumulation` | tributary arrows merging | Flow accumulation |
| `stream_extract` | blue branching line | Extract streams |
| `watershed` | basin boundary | Watershed |
| `river_edit` | river line and node | Edit river network |
| `water_depth` | water with vertical ruler | Water depth |
| `water_surface` | level water plane | Water-surface elevation |
| `flood` | expanding water edge | Flood simulation |

## Batch D: erosion and process simulation

| Asset name | Visual idea | Command |
|---|---|---|
| `rainfall` | cloud over grid | Rainfall forcing |
| `hydraulic_erosion` | water cutting a slope | Hydraulic erosion |
| `thermal_erosion` | falling scree | Thermal erosion |
| `sediment` | particles settling in water | Sediment layer |
| `deposition` | growing delta | Deposition |
| `process_run` | play triangle over terrain | Run |
| `process_pause` | pause bars | Pause |
| `process_step` | step arrow | One iteration |
| `process_reset` | circular arrow over terrain | Reset process |
| `mass_balance` | scales with sediment piles | Mass-balance report |

## Non-icon assets

- Brush cursors: circular hard, circular soft, square, line, polygon, and stamp.
- Layer badges: raster, vector point, vector line, vector polygon, table, virtual,
  derived, locked, missing source, and stale cache.
- Status markers: clean, modified, calculating, warning, invalid, and offline.
- Color ramps: elevation, bathymetry, slope, flow accumulation, moisture,
  temperature, sediment, and categorical landform/material palettes.
- Compact legends for continuous gradients and categorical swatches.
- Progress strip and cancellable background-job indicator.
- CRS selector row, numeric elevation field, unit selector, sea-level marker,
  opacity slider, blend-mode selector, and layer search field.
- Split handles and collapse tabs for left/right panels.

Reuse from WorldBox rather than redraw:

- window background and borders;
- button backgrounds;
- close button;
- checkboxes and toggles;
- sliders and scrollbars;
- text fields;
- warning dialog frame;
- generic plus/minus controls when no GIS meaning is lost.

Confirmed reusable game assets in WorldBox 0.51.2:

| TerrainLab slot | WorldBox asset |
|---|---|
| `project_save` | `ui/Icons/iconSaveLocal` |
| `layer_remove` | `ui/Icons/iconDeleteWorld` with TerrainLab tooltip |
| `layer_up` | `ui/Icons/iconArrowUP` |
| `layer_down` | `ui/Icons/iconArrowDOWN` |
| `lock` | `PrefabLibrary.iconLock` |
| `process_run` | `ui/Icons/iconPlay` |
| `process_pause` | `ui/Icons/iconPause` |
| `surface_fill` | `ui/Icons/iconBucket` |
| generic close | `ui/Icons/iconClose` |
| generic warning | `ui/Icons/iconWarning` |
| brush-size selectors | existing `ui/Icons/brushes/` family |

These slots remain in the semantic inventory because the toolbar needs them,
but they are not part of the custom art order. Inspect actual sprites in-game
before reusing any less exact metaphor.

## Drawing rules

- Master icon canvas: `64 x 64` transparent PNG.
- Verify downscaled use at `32 x 32` and `24 x 24` without interpolation.
- Pixel art only; no antialiasing or fractional-pixel strokes.
- Keep a two-pixel clear margin at the 32-pixel reference size.
- Use one readable silhouette and at most one small semantic badge.
- Do not bake button backgrounds into toolbar icons.
- Normal, active, disabled, and warning states should normally be generated by
  UI tinting. Draw a second sprite only when tinting cannot communicate state.
- File names use lowercase snake case and match the names in this document.

The next custom-art delivery should prioritize `elevation_set`,
`elevation_lower`, `elevation_smooth`, `flow_accumulation`, `stream_extract`,
`sink_fill`, six brush cursors, layer badges, elevation/bathymetry ramps, and
status markers. The broader Batch A concepts can receive a final hand-cleaned
24 px pass when their corresponding controls are implemented.
