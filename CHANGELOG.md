# Changelog

## Unreleased

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
