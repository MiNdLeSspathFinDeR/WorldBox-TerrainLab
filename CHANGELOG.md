# Changelog

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
