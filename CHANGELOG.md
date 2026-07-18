# Changelog

## Unreleased

## 1.8.1 - 2026-07-18

- Replaced implicit newest-image loading with an in-game workspace file
  selector and a separate, explicit Open selected confirmation command.
- Classification controls now remain locked until the chosen raster has loaded
  successfully. Changing the candidate clears the old preview instead of
  silently editing the wrong image.
- Clarified that Open image folder only opens the operating-system file
  browser; selecting a file there does not pass a selection back to WorldBox.

## 1.8.0 - 2026-07-18

- Added QGIS-style polygon training to the manual image classifier. Point and
  Polygon modes are explicit; polygon vertices use a live rubber band and can
  be completed by right-click, double-click, or the panel command.
- Classification profiles now store up to 128 validated simple polygons, 256
  vertices per polygon, and 8192 total polygon vertices without expanding the
  sidecar into millions of pixel samples. Existing point-only schema-1
  profiles remain compatible.
- Every output cell covered by a polygon receives its surface, biotope, and
  elevation authoritatively. Up to 32 spatially distributed interior pixels
  per polygon feed the bounded colour/texture/spatial classifier and IDW DEM,
  giving broad training areas stronger influence outside their exact boundary.
- Added translucent filled polygon rendering, active mode highlighting,
  unfinished-ring undo/cancel, point-or-polygon removal, scrollable controls,
  and complete Russian/English hover guidance.

## 1.7.0 - 2026-07-18

- Added an in-game manual raster-classification canvas over the WorldBox map.
  Source pixels can be labelled with an independent surface morphotype,
  playable biotope, and signed Int16 elevation. The canvas supports zoom, pan,
  persistent coloured sample markers, point removal, profile undo/clear, image
  cycling, and localized plain-language tooltips.
- Manual samples are stored in a validated
  `<image>.terrainlab-classification.json` sidecar. The Project toolbar can
  force a previously processed source back into the conversion queue after the
  profile changes.
- Added a bounded adaptive nearest-sample classifier combining the source
  image's normalized colour scale, local texture, and spatial position. This
  allows equal or similar colours to represent different morphotypes or
  biotopes when the player provides separate regional samples.
- Added IDW elevation interpolation with exact sample preservation,
  configurable smoothing, explicit marine depth constraints, and
  `NODATA=9999` protection. Generated saves carry the result as an uncompressed
  signed Int16 `terrainlab-elevation.tif`.
- TerrainLab now imports the generated DEM when the new world is first opened,
  applies it to the WorldBox height cache, and preserves the ordinary
  `map.wbox` fallback. The next normal save writes the full WBXGEO project.

## 1.6.1 - 2026-07-18

- Added an explicit in-game watched-folder format contract: PNG,
  JPG/JPEG/JFIF, TIFF/TIF, WebP, BMP, GIF, TGA, DDS, and JP2.
- Save now opens a world-name form before writing the current WorldBox map and
  WBXGEO sidecar. New worlds continue through WorldBox's native slot picker.
- Added native success/error notifications and a full-button watcher state
  color so toolbar commands no longer appear to do nothing.
- Added a Russian and English fallback for NML's `mod_configure_title`
  localization key.

## 1.6.0 - 2026-07-17

- Added an in-game image workspace under the WorldBox persistent-data folder.
  The Project toolbar can open it and toggle a persisted watcher that waits for
  stable raster files, queues them, and runs only one converter process at a
  time without PowerShell or temporary compiled assemblies.
- Image imports use the adaptive terrain classifier, gameplay-safe palette,
  and a new aspect-preserving `--fit-budget` mode that selects the largest map
  inside TerrainLab's 1,884,160-cell budget. Extremely elongated source
  projections remain supported.
- Every successful source image is published as a new vanilla-compatible
  `saveN` slot. The converter writes a staging directory first and renames the
  complete `map.wbox`, metadata, previews, and statistics database into place,
  so WorldBox never observes a partially written slot.
- Added Project-window queue, converter, active-file, success, and failure
  diagnostics plus open, enable/disable, and retry controls. The top watcher
  button uses play/pause state and the native green/amber activity lamp.
- Persisted processed and failed file fingerprints prevent duplicate imports
  across game restarts while allowing a changed file or explicit retry to run
  again. Converter failures return a nonzero status through the internal
  `--strict` bridge and do not modify the open world.
- Added Python sizing/save tests and C# workspace quoting, directory, and state
  persistence checks.

## 1.5.1 - 2026-07-17

- Fixed a WorldBox pathfinding-region desynchronization after TerrainLab
  changed a surface between ground and water. Every direct surface edit now
  passes its previous tile layer to the native `MapAction.checkTileState`
  invalidation path, which rebuilds affected chunk regions, island links, and
  path caches instead of leaving actors on a stale `MapRegion` graph.

## 1.5.0 - 2026-07-17

- Added runtime-only UInt16 source ownership for managed water. When two
  independently owned river systems first meet, that source pair receives one
  bounded confluence bonus covering at most four available cells around the
  junction. Reward history survives in-session routing rebuilds, so erosion
  cannot repeatedly mint the same bonus.
- Added incremental topology cleanup for fully water-enclosed dry components.
  One- and two-cell four-connected islands become managed shallow water, so
  diagonal pixel contact cannot stabilize a checkerboard; every side-connected
  three-cell or larger land component remains stable. The cleanup may remove
  liquid-sensitive trees, plants, fruit, hives, wheat, poop, and minerals, but
  preserves geysers, cities, creatures, creep, anomalies, and other structures.
- Added live confluence count and awarded basin volume to the Terrain
  Laboratory water status.

## 1.4.1 - 2026-07-17

- Fixed native geyser sources being retired between small pulses before their
  water budget could accumulate enough to materialize a channel cell. Source
  lifetime now follows the patched `Building` instance and uses a short removal
  grace period before draining a destroyed geyser network.
- Replaced ineffective `TipButton` components on numeric rows with direct
  pointer, click, and focus handlers. Every Terrain Laboratory numeric field
  now opens its localized plain-language explanation on hover or selection.

## 1.4.0 - 2026-07-17

- Removed the former 50-percent hard live-water ceiling. The maximum-area
  control now accepts the complete `1..100%` range; finite source volume,
  terrain resistance, gameplay obstacles, and per-tick work still bound spread.
- Added connected terminal lakes for channel fronts that cannot reach another
  river, lake, ocean, or map boundary. The local seed prefers a deep
  Priority-Flood depression, low elevation, and low substrate resistance, then
  fills only connected cells at or below its spill level.
- Established rivers now moisten and degrade a configurable one- or two-cell
  bank strip into sand or fine clay/silt alluvium.
- Destroying a registered native geyser cancels its pending source and applies
  configurable drainage to the orphaned connected channel. Shared networks
  remain wet while another live geyser feeds them. Drained rivers retain their
  hydro identity and expose a sandy channel with sparse hill and rare mountain
  ravine shoulders.
- Extended every live-water numeric control with English and Russian
  whole-row hover help. Added bank-width and destroyed-source drain controls.
- Advanced `hydrology.water_dynamics` to schema `1.7.0`; no additional
  persistent WBXGEO raster is added.

## 1.3.0 - 2026-07-17

- Live channel growth now materializes up to three consecutive receiver cells
  per routing operation. Each fragment is a connected straight or turning
  triplet for D8, D-infinity, and MFD instead of immediate sibling pixels.
- Removed obstacle bypass from blocked channel fronts. A route now ends at its
  last reachable cell or takes another receiver adjacent to that cell; it can
  no longer jump across an unavailable gameplay tile and leave detached water.
  Every triplet member still pays its own depth/resistance cost and obeys the
  per-tick and flooded-area limits.
- Added native WorldBox hover tooltips to every numeric DEM, brush, hydrology,
  erosion, and live-water parameter. English and Russian text explains the
  control in plain language, its valid range, and the effect of increasing it.
- Advanced `hydrology.water_dynamics` to schema `1.6.0` to identify connected
  triplet routing. The stored raster layout remains compatible with `1.5.x`.

## 1.2.4 - 2026-07-17

- Water-state rasters are now initialized when a world is attached, so every
  water-layer button has a valid dataset before live routing is enabled.
- Valid all-zero water rasters remain selected and begin rendering
  incrementally when their first wet cells arrive instead of reporting an
  empty layer and silently detaching.
- Base erodibility and compact metric Horn slope/aspect are initialized for
  every valid DEM cell. Drying clears dynamic water state without deleting
  those intrinsic terrain attributes.
- Moved native geyser observation from a drop-manager inference to the actual
  `Building.spawnBurstSpecial(int)` call. A pristine water simulation starts on
  its first geyser pulse, searches outside the building footprint for a safe
  outlet up to four cells away, and reports pulse/injected/consumed counters.
  Routing continues while GIS/settings windows are open and pauses only with
  the game or a conflicting scientific analysis.
- Advanced `hydrology.water_dynamics` to schema `1.5.0`; older payloads migrate
  the full-grid terrain attributes while preserving dynamic river data.

## 1.2.3 - 2026-07-17

- Decoupled toolbar chapter navigation from active map-tool state. Inspector,
  its native activity lamp, and the bottom coordinate/elevation strip now stay
  active while Project, Analysis, Layers, or another toolbar chapter is shown.
  A map tool is disabled only by selecting another tool or clicking it again.

## 1.2.2 - 2026-07-17

- Added an explicit horizontal cell scale to TerrainLab projects. New worlds use
  `1000 m` per WorldBox cell by default, WBXGEO `1.1` persists the metric value,
  and legacy `worldbox_tile` packages migrate without losing compatibility.
- Corrected Horn slope, aspect, hillshade, and river-valley derivatives to
  divide vertical metre differences by horizontal ground distance. A `567 m`
  rise across the default cell is now about `29.5 degrees`, not `89.9 degrees`.
- Added spatial regularization to vanilla-derived initial DEMs: generated land
  and bathymetry are limited to Earth-like neighboring rises and gradual coastal
  transitions while imported and manually edited DEM values remain untouched.
- GeoTIFF, world files, WKT2 engineering CRS, GIS manifests, and protected file
  sync now carry the same metric cell size.
- The bottom coordinate/elevation strip is now visible only while the Inspector
  tool is active and hides immediately when that tool is deselected.
- Advanced `hydrology.water_dynamics` to schema `1.4.0`; older compact local
  slope/aspect fields are recalculated using the metric cell scale on load.

## 1.2.1 - 2026-07-16

- Kept every GIS raster overlay visible after WorldBox switches from the
  gameplay tilemap to its low-resolution overview renderer at distant camera
  zoom. Overlay chunks now use the topmost gameplay/overview sorting context
  and are excluded from dynamic occlusion culling.

## 1.2.0 - 2026-07-16

- Added persistent `River` and `Waterbody` cell semantics independent of sea
  level, so inland water remains freshwater at positive or negative DEM values
  and dry channels retain their identity for later recharge.
- Added compact UInt8 moisture, nonlinear erodibility, local Horn slope, and
  downslope-aspect fields. Slope and aspect decode to radians without adding
  floating-point arrays to the project state.
- Added material-aware routing resistance and water retention. Saturated soil
  and organic cover degrade to sand; low-energy saturated alluvium can form a
  gameplay-safe clay substrate with higher retention and lower erodibility.
- Added bounded stream-power incision. Active rivers may lower their local Int16
  DEM bed by one to three metres per climate step, no more than 24 metres below
  the current local neighbor floor.
- Rebuilds live routing after incision while preserving every active source's
  origin, head, and remaining finite volume, allowing geysers to continue down
  the newly cut valley.
- Added live overlays, localized inspection values, layer-catalog entries, and
  GeoTIFF exports for hydro feature, moisture, erodibility, local slope, and
  local aspect.
- Advanced `hydrology.water_dynamics` to schema `1.3.0`; older payloads migrate
  managed cells into safe river/waterbody defaults, while dry hydro features
  and their dynamic fields now survive WBXGEO round trips.

## 1.1.0 - 2026-07-16

- Added map overlays for core landforms, materials, 250-metre contours,
  managed live water, and persistent UInt8 water storage.
- Added the previously unexposed ruggedness, filled DEM, and D8 direction
  rasters to both the adaptive toolbar and internal analysis pages.
- Added a two-click DEM ramp that interpolates from the sampled start height to
  the configured endpoint height across the current brush width as one
  undoable edit.
- Added localized per-cell values, active-state lamps, incremental contour and
  live-water refresh, and palette/ramp regression probes.

- Replaced linear vanilla-height expansion with an Earth-like, morphotype-aware
  initial DEM. Ordinary terrain is concentrated below `2000 m`, combined
  mountain/summit cells have a `5000 m` median, and at most five percent reach
  `7000 m` or higher. Deep ocean uses the analogous `5000 m` median and
  five-percent extreme-depth tail, while shallow water and shelf remain inside
  `0..-5 m` and `-6..-149 m`.
- Fixed native geyser injection at the real rain-drop event and moved its
  source to a safe adjacent cell; exhausted routes now restart and refill dried
  channels.
- Fixed the authoritative DEM domain at `-20000..9000 m` with sea level `0`,
  retaining signed Int16 storage and reserved `NODATA=9999`.
- Bound water types to absolute bed elevation: shallow `0..-5 m`, shelf
  `-6..-149 m`, and deep ocean at `-150 m` and below. Ordinary positive-DEM
  water is restored to land; managed freshwater channels remain shallow so
  inland and geyser-fed rivers still work.
- Added persistent UInt8 water storage, 30-second evaporation/recharge steps,
  exact pre-water surface restoration, WBXGEO schema 1.2 migration, and a
  `water_storage.tif` GIS layer.
- Bound the translucent DEM chunks to the gameplay tilemap layer and sorting
  layer so the enabled elevation overlay remains visible above the world.
- Bound every relief, hydrology, and erosion chunk to the gameplay tilemap and
  made derivative buttons calculate stale prerequisites before opening the
  requested overlay.
- Added selectable D8, Tarboton D-infinity, and Freeman MFD live channel
  routing with conservative receiver weights, acyclic Priority-Flood ranks,
  bounded branching, localized segmented controls, and WBXGEO persistence.
- Added bounded live DEM water routing with finite painted sources,
  Priority-Flood/D8 channels, depth-weighted local depression filling, and a
  hard 50-percent valid-cell ceiling.
- Connected native geyser pulses as continuing water-volume sources while
  preserving WorldBox pause, disable, destruction, and spawn timing.
- Added live-water toolbar/settings controls, WBXGEO managed-mask persistence,
  GeoTIFF export, optional-module validation, and maximum-grid probes.
- Added a direct per-cell Int16 DEM overlay with translucent, sea-level-centered
  Turbo colors and incremental chunk refresh during edit, undo, and redo.
- Added command-specific Russian and English hover descriptions to every
  TerrainLab toolbar and internal-window button.
- Made tooltip metadata mandatory for internal action buttons and added
  one-time missing-sprite diagnostics with text fallback.
- Added icons to internal module, layer, DEM, analysis, and overlay controls;
  documented all remaining purpose-drawn art and unimplemented GIS modules.

## 1.0.0 - 2026-07-16

- Added the in-game Int16 DEM editor with inspect, brush tools, and undo/redo.
- Added Horn slope, aspect, hillshade, ruggedness, and hypsometric overlays.
- Added Priority-Flood/D8 hydrology, UInt32 accumulation, streams, stable
  watersheds, and Strahler order.
- Added deterministic integer erosion/accumulation preview with exact mass
  balance, WBXGEO persistence, overlays, apply, and undo.
- Added strict GeoTIFF export/import with north-first rows, local engineering
  CRS sidecars, NODATA validation, and external GDAL compatibility tests.
- Added local GIS file sync with baseline hashes, conflict rejection,
  branch-and-apply, incoming history, and JSONL changes.
- Added a complete runtime layer catalog and one-job background scheduler.
- Added the two-level adaptive GIS toolbar with native WorldBox toggle lamps,
  repeat-click deselection, and a three-state module button.
- Enforced the 1,884,160-cell limit while retaining unrestricted aspect ratio.
- Kept vanilla `map.wbox` as the fallback and isolated corrupt optional modules.

## 0.5.0

- Added cancellable Priority-Flood/D8 hydrology and chunked map previews.

## 0.4.0

- Added WBXGEO project UI and interactive DEM editing.
