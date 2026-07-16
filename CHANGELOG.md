# Changelog

## Unreleased

- Fixed native geyser injection at the real rain-drop event and moved its
  source to a safe adjacent cell; exhausted routes now restart and refill dried
  channels.
- Added metre-based `5/150` water-depth classes, marine connectivity versus
  local inland spill levels, and explicit protection for dry negative DEM
  depressions.
- Added persistent UInt8 water storage, 30-second evaporation/recharge steps,
  exact pre-water surface restoration, WBXGEO schema 1.2 migration, and a
  `water_storage.tif` GIS layer.
- Bound the translucent DEM chunks to the gameplay tilemap layer and sorting
  layer so the enabled elevation overlay remains visible above the world.
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
