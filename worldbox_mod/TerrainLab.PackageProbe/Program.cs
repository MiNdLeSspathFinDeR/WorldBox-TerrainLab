using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TerrainLab;
using UnityEngine;

internal static class Program
{
    private static int Main(string[] args)
    {
        bool preserveArtifacts = false;
        bool runStress = false;
        string outputParent = null;
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] == "--stress")
            {
                runStress = true;
            }
            else if (args[index] == "--output" && index + 1 < args.Length)
            {
                preserveArtifacts = true;
                outputParent = args[++index];
            }
            else
            {
                Console.Error.WriteLine(
                    "Usage: TerrainLab.PackageProbe [--stress] [--output <directory>]");
                return 2;
            }
        }

        string parent = preserveArtifacts
            ? Path.GetFullPath(outputParent)
            : Path.GetTempPath();
        string testRoot = Path.Combine(
            parent,
            "TerrainLabPackageProbe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);

        try
        {
            ValidateMapBudget();
            ValidateDigitizingRaster();
            ValidateElevationPalette();
            ValidateReliefAlgorithm();
            ValidateHydrologyAlgorithm();
            ValidateWaterDynamicsAlgorithm();
            ValidateGeoTiff(testRoot);
            ValidateFileSync(testRoot);
            ValidateFileSyncRollback(testRoot);
            RunRoundTrip(testRoot);
            if (runStress)
            {
                ValidateMaximumDigitizingStress();
                ValidateMaximumGridStress();
            }

            Console.WriteLine("WBXGEO package probe passed.");
            if (preserveArtifacts)
            {
                Console.WriteLine("Artifacts: " + testRoot);
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            if (!preserveArtifacts)
            {
                Directory.Delete(testRoot, true);
            }
        }
    }

    private static void ValidateReliefAlgorithm()
    {
        const int width = 3;
        const int height = 3;
        short[] eastRisingPlane =
        {
            0, 1, 2,
            0, 1, 2,
            0, 1, 2
        };
        TerrainReliefResult result = TerrainReliefAnalyzer.Analyze(
            "relief-probe",
            0,
            width,
            height,
            eastRisingPlane);
        int center = width + 1;
        Assert(result.SlopeTenths[center] == 450, "Horn slope is incorrect.");
        Assert(result.AspectTenths[center] == 2700, "Downslope aspect is incorrect.");
        Assert(result.Ruggedness[center] == 1, "Ruggedness is incorrect.");
        Assert(result.Hillshade[center] <= 254, "Hillshade uses the NODATA token.");

        eastRisingPlane[0] = TerrainElevationEncoding.NoData;
        TerrainReliefResult masked = TerrainReliefAnalyzer.Analyze(
            "relief-probe",
            1,
            width,
            height,
            eastRisingPlane);
        Assert(
            masked.SlopeTenths[0] == ushort.MaxValue &&
            masked.AspectTenths[0] == ushort.MaxValue &&
            masked.Ruggedness[0] == ushort.MaxValue &&
            masked.Hillshade[0] == byte.MaxValue,
            "Relief NODATA encoding is incorrect.");
    }

    private static void ValidateElevationPalette()
    {
        Color32 noData = TerrainElevationOverlay.GetColor(
            TerrainElevationEncoding.NoData,
            0,
            -100,
            100);
        Color32 belowSea = TerrainElevationOverlay.GetColor(-100, 0, -100, 100);
        Color32 shallow = TerrainElevationOverlay.GetColor(-1, 0, -100, 100);
        Color32 seaLevel = TerrainElevationOverlay.GetColor(0, 0, -100, 100);
        Color32 high = TerrainElevationOverlay.GetColor(100, 0, -100, 100);

        Assert(noData.a == 0, "Elevation palette renders NODATA.");
        Assert(
            belowSea.b > belowSea.r && belowSea.b > belowSea.g,
            "Negative elevation does not start in the blue Turbo range.");
        Assert(
            shallow.b > shallow.r,
            "Shallow negative elevation left the blue/cyan range.");
        Assert(
            seaLevel.r > seaLevel.b && seaLevel.g > seaLevel.b,
            "Sea level does not start the yellow positive range.");
        Assert(
            high.r > high.g && high.r > high.b,
            "Positive maximum does not end in the red Turbo range.");
        Assert(
            belowSea.a > 0 && belowSea.a < byte.MaxValue &&
            belowSea.a == shallow.a && shallow.a == seaLevel.a &&
            seaLevel.a == high.a,
            "Elevation overlay alpha is not consistently translucent.");
    }

    private static void ValidateDigitizingRaster()
    {
        const int width = 5;
        const int height = 5;
        bool[] region = new bool[width * height];
        region[2 * width + 2] = true;
        region[2 * width + 1] = true;
        region[2 * width + 3] = true;
        region[1 * width + 2] = true;
        region[3 * width + 2] = true;
        region[0] = true;
        int[] connected = TerrainDigitizingRaster.CollectConnectedRegion(
            2 * width + 2,
            width,
            height,
            index => region[index]);
        Assert(connected.Length == 5,
            "Connected fill crossed a diagonal or missed a cardinal neighbour.");

        TerrainGridPoint[] lineVertices =
        {
            new TerrainGridPoint(0, 0),
            new TerrainGridPoint(4, 4)
        };
        int[] line = TerrainDigitizingRaster.RasterizePolyline(
            lineVertices,
            width,
            height,
            0);
        Assert(line.Length == 5 && line.Contains(0) && line.Contains(24),
            "Bresenham line rasterization is incorrect.");

        int[] rectangle = TerrainDigitizingRaster.RasterizeRectangle(
            new TerrainGridPoint(1, 1),
            new TerrainGridPoint(3, 2),
            width,
            height);
        Assert(rectangle.Length == 6,
            "Rectangle digitizing did not include every covered cell.");

        TerrainGridPoint[] polygonVertices =
        {
            new TerrainGridPoint(1, 1),
            new TerrainGridPoint(4, 1),
            new TerrainGridPoint(4, 4),
            new TerrainGridPoint(1, 4)
        };
        int[] polygon = TerrainDigitizingRaster.RasterizePolygon(
            polygonVertices,
            width,
            height);
        Assert(polygon.Contains(2 * width + 2) && !polygon.Contains(0),
            "Polygon rasterization lost its interior or leaked outside its extent.");

        TerrainBoundarySegment[] boundary = TerrainDigitizingRaster.BuildBoundary(
            new[] { 1 * width + 1, 1 * width + 2, 2 * width + 1, 2 * width + 2 },
            width,
            height);
        Assert(boundary.Length == 8,
            "Polygonized 2 x 2 region has an invalid boundary.");
    }

    private static void ValidateMapBudget()
    {
        int block = TerrainMapLimits.WorldBoxBlockSize;
        Assert(
            TerrainMapLimits.MaximumCellCount == 1884160,
            "TerrainLab maximum cell budget changed unexpectedly.");
        Assert(
            TerrainMapLimits.TryValidate(23 * block, 20 * block, out _),
            "The exact 115-percent boundary was rejected.");
        Assert(
            TerrainMapLimits.TryValidate(40 * block, 10 * block, out _),
            "A valid elongated map was rejected.");
        Assert(
            !TerrainMapLimits.TryValidate(22 * block, 22 * block, out _),
            "An over-budget square map was accepted.");
    }

    private static void ValidateMaximumGridStress()
    {
        int width = 23 * TerrainMapLimits.WorldBoxBlockSize;
        int height = 20 * TerrainMapLimits.WorldBoxBlockSize;
        int count = checked(width * height);
        Assert(count == TerrainMapLimits.MaximumCellCount,
            "Stress canvas is not the exact maximum size.");

        short[] elevation = new short[count];
        for (int y = 0; y < height; y++)
        {
            int offset = y * width;
            for (int x = 0; x < width; x++)
            {
                elevation[offset + x] = (short)(y + x / 64);
            }
        }

        TerrainWorldState state = TerrainWorldState.CreateFromLayers(
            "maximum-grid-stress",
            DateTime.UtcNow,
            width,
            height,
            0,
            elevation,
            Enumerable.Repeat((byte)TerrainLandform.Plain, count).ToArray(),
            Enumerable.Repeat((byte)TerrainMaterial.Soil, count).ToArray());
        GC.Collect();
        long beforeBytes = GC.GetTotalMemory(true);
        Stopwatch stopwatch = Stopwatch.StartNew();
        RunRelief(state);
        TerrainHydrologyModule hydrology = new TerrainHydrologyModule();
        RunHydrology(
            state,
            hydrology,
            TerrainHydrologyAnalyzer.GetDefaultStreamThreshold(count));
        TerrainErosionModule erosion = new TerrainErosionModule();
        RunErosion(state, erosion);
        TerrainWaterRouting waterRouting = TerrainWaterRouting.Build(
            width,
            height,
            state.Elevation);
        TerrainWaterSimulation water = new TerrainWaterSimulation(
            waterRouting,
            new byte[count],
            new byte[count],
            new TerrainWaterParameters
            {
                MaximumFloodPercent = 50,
                InitialSourceVolume = 128,
                GeyserPulseVolume = 2,
                CellsPerTick = 128,
                RoutingAlgorithm =
                    TerrainWaterRoutingAlgorithm.MultipleFlowDirection
            });
        int waterSource = height / 2 * width + width / 2;
        water.AddSource(waterSource, elevation[waterSource], 128);
        IReadOnlyList<TerrainWaterCellChange> waterChanges =
            water.Step(128, _ => true);
        stopwatch.Stop();
        GC.Collect();
        long retainedBytes = GC.GetTotalMemory(true) - beforeBytes;

        Assert(state.Relief != null && state.Relief.IsCurrent(state),
            "Maximum-grid relief result is missing.");
        Assert(state.Hydrology != null && state.Hydrology.IsCurrent(state),
            "Maximum-grid hydrology result is missing.");
        Assert(state.Erosion != null && state.Erosion.IsCurrent(state) &&
               state.Erosion.Statistics.MassBalance == 0,
            "Maximum-grid erosion result is invalid.");
        Assert(waterChanges.Count > 0 &&
               water.ManagedCellCount <= water.FloodCellLimit,
            "Maximum-grid live-water routing is invalid.");
        Process process = Process.GetCurrentProcess();
        process.Refresh();
        Console.WriteLine(
            "Maximum-grid stress: {0:N0} cells, {1:0.00}s, retained managed delta " +
            "{2:N1} MiB, process peak {3:N1} MiB.",
            count,
            stopwatch.Elapsed.TotalSeconds,
            retainedBytes / 1048576.0,
            process.PeakWorkingSet64 / 1048576.0);
    }

    private static void ValidateMaximumDigitizingStress()
    {
        int width = 23 * TerrainMapLimits.WorldBoxBlockSize;
        int height = 20 * TerrainMapLimits.WorldBoxBlockSize;
        int count = checked(width * height);
        Stopwatch stopwatch = Stopwatch.StartNew();
        int[] connected = TerrainDigitizingRaster.CollectConnectedRegion(
            0,
            width,
            height,
            _ => true);
        Assert(connected.Length == count,
            "Maximum-grid connected selection is incomplete.");

        TerrainGridPoint[] extent =
        {
            new TerrainGridPoint(0, 0),
            new TerrainGridPoint(width - 1, 0),
            new TerrainGridPoint(width - 1, height - 1),
            new TerrainGridPoint(0, height - 1)
        };
        int[] polygon = TerrainDigitizingRaster.RasterizePolygon(
            extent,
            width,
            height);
        Assert(polygon.Length == count,
            "Maximum-grid polygon did not cover the full extent.");
        TerrainBoundarySegment[] boundary = TerrainDigitizingRaster.BuildBoundary(
            polygon,
            width,
            height);
        Assert(boundary.Length == 2 * width + 2 * height,
            "Maximum-grid extent boundary is invalid.");
        stopwatch.Stop();
        Console.WriteLine(
            "Maximum-grid digitizing: {0:N0} cells, {1:0.00}s.",
            count,
            stopwatch.Elapsed.TotalSeconds);
    }

    private static void RunRoundTrip(string testRoot)
    {
        const int width = 8;
        const int height = 4;
        int count = width * height;

        short[] elevation = Enumerable.Range(0, count)
            .Select(value => (short)(value - 10))
            .ToArray();
        elevation[5] = TerrainElevationEncoding.NoData;

        byte[] landform = Enumerable.Repeat((byte)TerrainLandform.Plain, count).ToArray();
        byte[] material = Enumerable.Repeat((byte)TerrainMaterial.Rock, count).ToArray();
        TerrainWorldState state = TerrainWorldState.CreateFromLayers(
            Guid.NewGuid().ToString("D"),
            DateTime.UtcNow,
            width,
            height,
            0,
            elevation,
            landform,
            material);

        SavedMap savedMap = (SavedMap)FormatterServices.GetUninitializedObject(typeof(SavedMap));
        savedMap.width = 1;
        savedMap.height = 1;
        savedMap.saveVersion = 17;
        TerrainModuleRegistry registry = new TerrainModuleRegistry();
        TerrainHydrologyModule hydrologyModule = new TerrainHydrologyModule();
        TerrainWaterDynamicsModule waterModule = new TerrainWaterDynamicsModule();
        TerrainErosionModule erosionModule = new TerrainErosionModule();
        registry.Register(hydrologyModule);
        registry.Register(waterModule);
        registry.Register(erosionModule);
        RunRelief(state);
        RunHydrology(state, hydrologyModule, 2);
        Assert(state.Hydrology != null, "Hydrology analysis did not attach a result.");
        RunErosion(state, erosionModule);
        Assert(state.Erosion != null, "Erosion analysis did not attach a result.");
        TerrainWaterState water = state.EnsureWaterDynamics();
        water.Parameters.MaximumFloodPercent = 37;
        water.Parameters.InitialSourceVolume = 19;
        water.Parameters.GeyserPulseVolume = 3;
        water.Parameters.CellsPerTick = 11;
        water.Parameters.RoutingAlgorithm =
            TerrainWaterRoutingAlgorithm.DInfinity;
        water.ManagedMask[2] = 1;
        water.ManagedMask[7] = 1;
        state.EnsureWaterDynamics();

        string gisDirectory = TerrainGisExporter.Export(
            Path.Combine(testRoot, "gis"),
            state);
        Assert(File.Exists(Path.Combine(gisDirectory, "terrainlab-gis.json")),
            "GIS manifest was not created.");
        JObject gisManifest = JObject.Parse(
            File.ReadAllText(Path.Combine(gisDirectory, "terrainlab-gis.json")));
        Assert(gisManifest["layers"].Count() == 16,
            "GIS export did not include every ready layer.");
        Assert(File.Exists(Path.Combine(gisDirectory, "managed_water.tif")),
            "GIS export omitted the managed-water mask.");
        Assert(((string)gisManifest["crs_wkt"]).StartsWith("ENGCRS["),
            "GIS manifest does not contain WKT2 ENGCRS.");
        Assert(
            TerrainGeoTiff.ReadInt16(
                Path.Combine(gisDirectory, "elevation.tif"),
                width,
                height).SequenceEqual(state.Elevation),
            "Exported GIS elevation changed during round-trip.");

        string baseMap = Path.Combine(testRoot, "map.wbox");
        File.WriteAllBytes(baseMap, new byte[] { 1, 2, 3, 4, 5 });
        WbxGeoPackage.Save(testRoot, state, savedMap, registry);

        string packagePath = WbxGeoPackage.GetSidecarPath(testRoot);
        Assert(File.Exists(packagePath), "Package was not created.");
        ValidateArchive(packagePath, count);

        Assert(
            WbxGeoPackage.TryLoad(
                packagePath,
                baseMap,
                registry,
                out TerrainWorldState loaded,
                out string loadError),
            "Package reload failed: " + loadError);
        Assert(loaded.Elevation[5] == TerrainElevationEncoding.NoData, "NODATA changed.");
        Assert(loaded.Width == width && loaded.Height == height, "Dimensions changed.");
        Assert(loaded.Hydrology != null, "Hydrology module did not survive package reload.");
        Assert(
            loaded.WaterDynamics != null &&
            loaded.WaterDynamics.ManagedCellCount == 2 &&
            loaded.WaterDynamics.ManagedMask.SequenceEqual(water.ManagedMask) &&
            loaded.WaterDynamics.Parameters.MaximumFloodPercent == 37 &&
            loaded.WaterDynamics.Parameters.GeyserPulseVolume == 3 &&
            loaded.WaterDynamics.Parameters.RoutingAlgorithm ==
            TerrainWaterRoutingAlgorithm.DInfinity,
            "Water dynamics state changed during package round-trip.");
        Assert(
            loaded.Hydrology.FlowDirection[5] == byte.MaxValue &&
            loaded.Hydrology.FlowAccumulation[5] == 0 &&
            loaded.Hydrology.StreamMask[5] == byte.MaxValue &&
            loaded.Hydrology.Watershed[5] == 0 &&
            loaded.Hydrology.StreamOrder[5] == byte.MaxValue,
            "Hydrology NODATA encoding changed during round-trip.");
        Assert(
            loaded.Hydrology.FlowAccumulation.SequenceEqual(
                state.Hydrology.FlowAccumulation),
            "Flow accumulation changed during round-trip.");
        Assert(
            loaded.Hydrology.Watershed.SequenceEqual(state.Hydrology.Watershed) &&
            loaded.Hydrology.StreamOrder.SequenceEqual(state.Hydrology.StreamOrder),
            "Advanced hydrology layers changed during round-trip.");
        Assert(loaded.Erosion != null, "Erosion module did not survive package reload.");
        Assert(
            loaded.Erosion.ResultElevation.SequenceEqual(state.Erosion.ResultElevation) &&
            loaded.Erosion.NetChange.SequenceEqual(state.Erosion.NetChange),
            "Erosion layers changed during round-trip.");
        Assert(loaded.Erosion.Statistics.MassBalance == 0, "Loaded erosion lost mass.");
        ValidateElevationEditing(loaded);

        string importDirectory = Path.Combine(testRoot, "imported");
        WbxGeoPackage.ImportVanillaPayload(packagePath, importDirectory);
        Assert(
            File.ReadAllBytes(Path.Combine(importDirectory, "map.wbox"))
                .SequenceEqual(File.ReadAllBytes(baseMap)),
            "Imported vanilla map differs from the embedded map.");
        Assert(
            File.Exists(WbxGeoPackage.GetSidecarPath(importDirectory)),
            "Imported WBXGEO sidecar is missing.");

        string corruptHydrologyPackage = Path.Combine(testRoot, "corrupt-hydrology.wbxgeo");
        File.Copy(packagePath, corruptHydrologyPackage);
        using (ZipArchive archive = ZipFile.Open(corruptHydrologyPackage, ZipArchiveMode.Update))
        {
            ZipArchiveEntry streams = archive.GetEntry("modules/hydrology/streams.u8");
            streams.Delete();
            using (Stream output = archive.CreateEntry("modules/hydrology/streams.u8").Open())
            {
                output.WriteByte(1);
            }
        }

        Assert(
            WbxGeoPackage.TryLoad(
                corruptHydrologyPackage,
                baseMap,
                registry,
                out TerrainWorldState hydrologyFallback,
                out string hydrologyFallbackError),
            "Optional hydrology corruption rejected the core project: " +
            hydrologyFallbackError);
        Assert(
            hydrologyFallback.Hydrology == null,
            "Corrupt optional hydrology data was attached to the project.");

        string semanticHydrologyPackage = Path.Combine(
            testRoot,
            "semantic-hydrology.wbxgeo");
        File.Copy(packagePath, semanticHydrologyPackage);
        using (ZipArchive archive = ZipFile.Open(
            semanticHydrologyPackage,
            ZipArchiveMode.Update))
        {
            const string entryPath = "modules/hydrology/flow_direction.u8";
            byte[] direction = ReadArchiveEntry(archive, entryPath);
            direction[0] = 8;
            ReplaceArchiveEntry(archive, entryPath, direction);
            UpdateLayerChecksum(archive, "hydrology.flow_direction", direction);
        }

        Assert(
            WbxGeoPackage.TryLoad(
                semanticHydrologyPackage,
                baseMap,
                registry,
                out TerrainWorldState semanticHydrologyFallback,
                out string semanticHydrologyError),
            "Semantic hydrology corruption rejected the core project: " +
            semanticHydrologyError);
        Assert(semanticHydrologyFallback.Hydrology == null,
            "Semantically invalid D8 data was attached to the project.");

        string semanticErosionPackage = Path.Combine(
            testRoot,
            "semantic-erosion.wbxgeo");
        File.Copy(packagePath, semanticErosionPackage);
        using (ZipArchive archive = ZipFile.Open(
            semanticErosionPackage,
            ZipArchiveMode.Update))
        {
            const string entryPath = "modules/erosion.hydraulic/net_change.i32";
            byte[] netChange = ReadArchiveEntry(archive, entryPath);
            Buffer.BlockCopy(BitConverter.GetBytes(123456), 0, netChange, 0, sizeof(int));
            ReplaceArchiveEntry(archive, entryPath, netChange);
            UpdateLayerChecksum(archive, "erosion.net_change", netChange);
        }

        Assert(
            WbxGeoPackage.TryLoad(
                semanticErosionPackage,
                baseMap,
                registry,
                out TerrainWorldState semanticErosionFallback,
                out string semanticErosionError),
            "Semantic erosion corruption rejected the core project: " +
            semanticErosionError);
        Assert(semanticErosionFallback.Hydrology != null &&
               semanticErosionFallback.Erosion == null,
            "Semantically invalid erosion data was attached to the project.");

        string semanticWaterPackage = Path.Combine(
            testRoot,
            "semantic-water-dynamics.wbxgeo");
        File.Copy(packagePath, semanticWaterPackage);
        using (ZipArchive archive = ZipFile.Open(
            semanticWaterPackage,
            ZipArchiveMode.Update))
        {
            const string entryPath =
                "modules/hydrology.water_dynamics/managed_water.u8";
            byte[] managedWater = ReadArchiveEntry(archive, entryPath);
            managedWater[0] = 2;
            ReplaceArchiveEntry(archive, entryPath, managedWater);
            UpdateLayerChecksum(
                archive,
                "hydrology.water_dynamics.managed_mask",
                managedWater);
        }

        Assert(
            WbxGeoPackage.TryLoad(
                semanticWaterPackage,
                baseMap,
                registry,
                out TerrainWorldState semanticWaterFallback,
                out string semanticWaterError),
            "Semantic water corruption rejected the core project: " +
            semanticWaterError);
        Assert(
            semanticWaterFallback.WaterDynamics == null &&
            semanticWaterFallback.Hydrology != null &&
            semanticWaterFallback.Erosion != null,
            "Semantically invalid live-water data was attached to the project.");

        string corruptPackage = Path.Combine(testRoot, "corrupt.wbxgeo");
        File.Copy(packagePath, corruptPackage);
        using (ZipArchive archive = ZipFile.Open(corruptPackage, ZipArchiveMode.Update))
        {
            ZipArchiveEntry embeddedMap = archive.GetEntry("base/map.wbox");
            embeddedMap.Delete();
            using (Stream output = archive.CreateEntry("base/map.wbox").Open())
            {
                output.WriteByte(99);
            }
        }

        Assert(
            !WbxGeoPackage.TryLoad(
                corruptPackage,
                null,
                registry,
                out _,
                out _),
            "Package with a corrupt embedded vanilla map was accepted.");

        File.WriteAllBytes(baseMap, new byte[] { 9, 9, 9 });
        Assert(
            !WbxGeoPackage.TryLoad(
                packagePath,
                baseMap,
                registry,
                out _,
                out _),
            "Stale overlay was accepted after base-map checksum changed.");
    }

    private static void ValidateHydrologyAlgorithm()
    {
        const int width = 5;
        const int height = 5;
        const int count = width * height;
        short[] elevation = Enumerable.Repeat((short)10, count).ToArray();
        elevation[2 * width + 2] = 0;
        byte[] landform = Enumerable.Repeat((byte)TerrainLandform.Plain, count).ToArray();
        byte[] material = Enumerable.Repeat((byte)TerrainMaterial.Soil, count).ToArray();
        TerrainWorldState state = TerrainWorldState.CreateFromLayers(
            Guid.NewGuid().ToString("D"),
            DateTime.UtcNow,
            width,
            height,
            0,
            elevation,
            landform,
            material);
        TerrainHydrologyModule module = new TerrainHydrologyModule();
        RunHydrology(state, module, 2);

        TerrainHydrologyResult result = state.Hydrology;
        int center = 2 * width + 2;
        Assert(result != null && result.IsCurrent(state), "Hydrology result is stale.");
        Assert(result.FilledElevation[center] == 10, "Priority-Flood did not fill a pit.");
        Assert(result.Statistics.FilledCellCount == 1, "Unexpected filled-cell count.");
        Assert(result.Statistics.MaximumFillDepth == 10, "Unexpected fill depth.");
        Assert(result.Statistics.OutletCellCount == 16, "Boundary outlets are incorrect.");
        Assert(
            result.FlowDirection[center] != byte.MaxValue,
            "D8 did not route the filled pit.");
        Assert(
            result.Statistics.MaximumAccumulation > 1,
            "Flow accumulation did not collect upstream cells.");
        Assert(result.Statistics.WatershedCount == 16, "Watershed outlet labels are incorrect.");
        Assert(
            result.Watershed.Where((value, index) => elevation[index] != TerrainElevationEncoding.NoData)
                .All(value => value > 0),
            "A valid hydrology cell has no watershed id.");
        Assert(
            result.Statistics.MaximumStreamOrder >= 1 &&
            result.StreamOrder.Where(value => value != byte.MaxValue).All(value => value <= result.Statistics.MaximumStreamOrder),
            "Strahler stream ordering is invalid.");

        TerrainErosionParameters erosionParameters = new TerrainErosionParameters
        {
            Iterations = 3,
            FlowStrengthPercent = 50,
            ThermalStrengthPercent = 50,
            TalusThreshold = 2
        };
        TerrainErosionResult erosion = TerrainErosionAnalyzer.Analyze(
            state.ProjectId,
            state.Revision,
            width,
            height,
            state.Elevation,
            state.Landform,
            result.FlowDirection,
            result.FlowAccumulation,
            erosionParameters);
        TerrainErosionResult repeated = TerrainErosionAnalyzer.Analyze(
            state.ProjectId,
            state.Revision,
            width,
            height,
            state.Elevation,
            state.Landform,
            result.FlowDirection,
            result.FlowAccumulation,
            erosionParameters);
        Assert(erosion.Statistics.MassBalance == 0, "Erosion mass balance is not zero.");
        Assert(erosion.Statistics.ChangedCellCount > 0, "Erosion did not change the test DEM.");
        Assert(
            erosion.ResultElevation.SequenceEqual(repeated.ResultElevation),
            "Erosion is not deterministic.");

        long revision = state.Revision;
        state.ApplyElevationBrush(
            2,
            2,
            0,
            TerrainElevationOperation.Raise,
            0,
            1);
        Assert(state.Revision == revision + 1, "DEM revision did not advance.");
        Assert(!result.IsCurrent(state), "DEM edit did not invalidate hydrology.");
    }

    private static void ValidateWaterDynamicsAlgorithm()
    {
        const int width = 10;
        const int height = 10;
        const int count = width * height;
        short[] elevation = new short[count];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                elevation[y * width + x] =
                    (short)(200 - x * 10 + Math.Abs(y - 5) * 3);
            }
        }

        elevation[0] = TerrainElevationEncoding.NoData;
        TerrainWaterRouting routing = TerrainWaterRouting.Build(
            width,
            height,
            elevation);
        ValidateWaterRoutingAlgorithms();
        TerrainWaterParameters legacyParameters =
            JsonConvert.DeserializeObject<TerrainWaterParameters>(
                "{\"maximum_flood_percent\":25}");
        Assert(
            legacyParameters.RoutingAlgorithm == TerrainWaterRoutingAlgorithm.D8,
            "A legacy live-water payload did not default to D8.");
        TerrainWaterParameters cappedParameters = new TerrainWaterParameters
        {
            MaximumFloodPercent = 99,
            InitialSourceVolume = 100,
            GeyserPulseVolume = 2,
            CellsPerTick = 100
        };
        TerrainWaterSimulation capped = new TerrainWaterSimulation(
            routing,
            new byte[count],
            new byte[count],
            cappedParameters);
        Assert(capped.FloodCellLimit == routing.ValidCellCount / 2,
            "Water dynamics exceeded its hard 50-percent cell cap.");

        TerrainWaterParameters finiteParameters = new TerrainWaterParameters
        {
            MaximumFloodPercent = 50,
            InitialSourceVolume = 3,
            GeyserPulseVolume = 1,
            CellsPerTick = 100
        };
        int source = 5 * width + 1;
        TerrainWaterSimulation first = new TerrainWaterSimulation(
            routing,
            new byte[count],
            new byte[count],
            finiteParameters);
        Assert(first.AddSource(source, elevation[source], 3),
            "Finite water source was rejected.");
        int[] firstPath = first.Step(100, _ => true)
            .Select(change => change.Index)
            .ToArray();
        Assert(firstPath.Length == 3 && first.ManagedCellCount == 3,
            "Finite water source did not consume exactly its volume.");
        Assert(first.Step(100, _ => true).Count == 0,
            "Exhausted finite water source continued spreading.");
        Assert(firstPath.All(index => elevation[index] != TerrainElevationEncoding.NoData),
            "Water entered a NODATA cell.");
        Assert(first.AddSource(source, elevation[source], 2) &&
               first.Step(100, _ => true).Count == 2,
            "A repeated finite contact did not replenish its unfinished route.");
        Assert(first.MarkExternalDry(firstPath[0]) &&
               first.ManagedCellCount == 4 &&
               !first.IsWater(firstPath[0]),
            "Externally repainted water remained in the managed budget.");

        TerrainWaterSimulation repeated = new TerrainWaterSimulation(
            routing,
            new byte[count],
            new byte[count],
            finiteParameters);
        repeated.AddSource(source, elevation[source], 3);
        int[] repeatedPath = repeated.Step(100, _ => true)
            .Select(change => change.Index)
            .ToArray();
        Assert(firstPath.SequenceEqual(repeatedPath),
            "D8 water routing is not deterministic.");

        const int basinWidth = 7;
        const int basinHeight = 7;
        short[] basinElevation = new short[basinWidth * basinHeight];
        for (int y = 0; y < basinHeight; y++)
        {
            for (int x = 0; x < basinWidth; x++)
            {
                basinElevation[y * basinWidth + x] =
                    x == 0 || y == 0 || x == basinWidth - 1 || y == basinHeight - 1
                        ? (short)50
                        : (short)0;
            }
        }

        TerrainWaterRouting basinRouting = TerrainWaterRouting.Build(
            basinWidth,
            basinHeight,
            basinElevation);
        TerrainWaterSimulation basin = new TerrainWaterSimulation(
            basinRouting,
            new byte[basinElevation.Length],
            new byte[basinElevation.Length],
            finiteParameters);
        int basinSource = 3 * basinWidth + 3;
        basin.AddSource(basinSource, basinElevation[basinSource], 204);
        TerrainWaterCellChange[] basinChanges = basin.Step(10, _ => true).ToArray();
        Assert(
            basinChanges.Length == 4 &&
            basinChanges.All(change =>
                change.Cost == 51 &&
                basinElevation[change.Index] == 0) &&
            basinChanges.Any(change =>
                change.DepthClass == TerrainWaterDepthClass.Shallow) &&
            basinChanges.Any(change =>
                change.DepthClass == TerrainWaterDepthClass.Deep),
            "Depression fill did not consume depth-weighted volume locally: " +
            string.Join(", ", basinChanges.Select(change => string.Format(
                "{0}/{1}/{2}",
                change.Index,
                change.Cost,
                change.DepthClass))));

        TerrainWaterSimulation limited = new TerrainWaterSimulation(
            routing,
            new byte[count],
            new byte[count],
            new TerrainWaterParameters
            {
                MaximumFloodPercent = 10,
                InitialSourceVolume = 1000,
                GeyserPulseVolume = 1,
                CellsPerTick = 100
            });
        limited.AddSource(source, elevation[source], 1000);
        for (int iteration = 0; iteration < 10; iteration++)
        {
            limited.Step(100, _ => true);
        }

        Assert(limited.ManagedCellCount <= limited.FloodCellLimit,
            "Water spread crossed its configured area limit.");

        const int registryWidth = 20;
        const int registryHeight = 20;
        short[] registryElevation = Enumerable.Range(
                0,
                registryWidth * registryHeight)
            .Select(index => (short)(index / registryWidth + index % registryWidth))
            .ToArray();
        byte[] allExternalWater = Enumerable.Repeat(
                (byte)1,
                registryElevation.Length)
            .ToArray();
        TerrainWaterSimulation registrySimulation = new TerrainWaterSimulation(
            TerrainWaterRouting.Build(
                registryWidth,
                registryHeight,
                registryElevation),
            allExternalWater,
            new byte[registryElevation.Length],
            finiteParameters);
        for (int index = 0; index < 300; index++)
        {
            Assert(
                registrySimulation.AddSource(
                    index,
                    registryElevation[index],
                    1),
                "Inactive water sources permanently exhausted the registry.");
            registrySimulation.Step(1, _ => true);
        }

        Assert(registrySimulation.ActiveSourceCount == 0,
            "Completed external-water sources remained active.");
    }

    private static void ValidateWaterRoutingAlgorithms()
    {
        const int width = 7;
        const int height = 7;
        short[] elevation = new short[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                elevation[y * width + x] = (short)(500 - x * 20 - y * 7);
            }
        }

        TerrainWaterRouting routing = TerrainWaterRouting.Build(
            width,
            height,
            elevation);
        int center = 3 * width + 3;
        TerrainWaterReceiver[] receivers = new TerrainWaterReceiver[8];

        int d8Count = routing.GetReceivers(
            center,
            TerrainWaterRoutingAlgorithm.D8,
            receivers);
        Assert(d8Count == 1 && Math.Abs(receivers[0].Weight - 1.0) < 1e-12,
            "D8 live-water routing did not select exactly one receiver.");

        int dinfCount = routing.GetReceivers(
            center,
            TerrainWaterRoutingAlgorithm.DInfinity,
            receivers);
        Assert(dinfCount == 2,
            "D-infinity did not split an oblique planar slope between two cells.");
        Assert(
            Math.Abs(receivers.Take(dinfCount).Sum(item => item.Weight) - 1.0) < 1e-12,
            "D-infinity receiver weights are not conservative.");
        Assert(
            receivers.Take(dinfCount).All(item =>
                item.Weight > 0.0 &&
                routing.DrainageRank[item.Index] < routing.DrainageRank[center]),
            "D-infinity produced an invalid or cyclic receiver.");
        HashSet<int> dinfDirectReceivers = new HashSet<int>(
            receivers.Take(dinfCount).Select(item => item.Index));

        TerrainWaterSimulation dinfSimulation = new TerrainWaterSimulation(
            routing,
            new byte[elevation.Length],
            new byte[elevation.Length],
            new TerrainWaterParameters
            {
                RoutingAlgorithm = TerrainWaterRoutingAlgorithm.DInfinity
            });
        dinfSimulation.AddSource(center, elevation[center], 3);
        int[] dinfPath = dinfSimulation.Step(3, _ => true)
            .Select(change => change.Index)
            .ToArray();
        Assert(
            dinfPath.Length == 3 &&
            dinfPath.Skip(1).All(dinfDirectReceivers.Contains),
            "D-infinity channel fronts did not materialize both weighted branches.");

        int mfdCount = routing.GetReceivers(
            center,
            TerrainWaterRoutingAlgorithm.MultipleFlowDirection,
            receivers);
        Assert(mfdCount > 2,
            "MFD did not distribute an oblique planar slope across downslope cells.");
        Assert(
            Math.Abs(receivers.Take(mfdCount).Sum(item => item.Weight) - 1.0) < 1e-12,
            "MFD receiver weights are not conservative.");
        Assert(
            receivers.Take(mfdCount).All(item =>
                item.Weight > 0.0 &&
                routing.DrainageRank[item.Index] < routing.DrainageRank[center]),
            "MFD produced an invalid or cyclic receiver.");
        HashSet<int> mfdDirectReceivers = new HashSet<int>(
            receivers.Take(mfdCount).Select(item => item.Index));

        TerrainWaterSimulation mfdSimulation = new TerrainWaterSimulation(
            routing,
            new byte[elevation.Length],
            new byte[elevation.Length],
            new TerrainWaterParameters
            {
                RoutingAlgorithm =
                    TerrainWaterRoutingAlgorithm.MultipleFlowDirection
            });
        mfdSimulation.AddSource(center, elevation[center], 3);
        int[] mfdPath = mfdSimulation.Step(3, _ => true)
            .Select(change => change.Index)
            .ToArray();
        Assert(
            mfdPath.Length == 3 &&
            mfdPath.Skip(1).Distinct().Count() == 2 &&
            mfdPath.Skip(1).All(mfdDirectReceivers.Contains),
            "MFD channel fronts did not materialize distinct weighted branches.");

        TerrainWaterParameters mfdParameters = new TerrainWaterParameters
        {
            RoutingAlgorithm = TerrainWaterRoutingAlgorithm.MultipleFlowDirection
        };
        string serialized = JsonConvert.SerializeObject(mfdParameters);
        Assert(serialized.Contains("\"routing_algorithm\":\"mfd\""),
            "The live-water routing algorithm is not stored by stable string ID.");
    }

    private static void ValidateGeoTiff(string testRoot)
    {
        string directory = Path.Combine(testRoot, "geotiff");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "elevation.tif");
        short[] values =
        {
            10, 11, TerrainElevationEncoding.NoData,
            20, 21, 22
        };
        TerrainGeoTiff.Write(
            path,
            3,
            2,
            values,
            TerrainTiffSampleKind.Int16,
            "9999",
            "geotiff-probe",
            "core.elevation");

        Assert(TerrainGeoTiff.ReadInt16(path, 3, 2).SequenceEqual(values),
            "GeoTIFF Int16 round-trip changed values or row order.");
        Assert(ReadFirstTiffInt16(path) == 20,
            "GeoTIFF does not store the north row first.");
        Assert(File.Exists(Path.ChangeExtension(path, ".tfw")) &&
               File.Exists(Path.ChangeExtension(path, ".prj")),
            "GeoTIFF sidecars are missing.");

        short[] tall = Enumerable.Range(0, 600)
            .Select(value => (short)(value - 300))
            .ToArray();
        string tallPath = Path.Combine(directory, "tall.tif");
        TerrainGeoTiff.Write(
            tallPath,
            2,
            300,
            tall,
            TerrainTiffSampleKind.Int16,
            "9999",
            "geotiff-probe",
            "core.elevation");
        Assert(TerrainGeoTiff.ReadInt16(tallPath, 2, 300).SequenceEqual(tall),
            "Multi-strip GeoTIFF round-trip failed.");

        AssertThrows<InvalidDataException>(
            () => TerrainGeoTiff.ReadInt16(path, 2, 3),
            "GeoTIFF with unexpected dimensions was accepted.");

        string invalidNoData = Path.Combine(directory, "invalid-nodata.tif");
        byte[] corrupt = File.ReadAllBytes(path);
        int noDataOffset = FindAscii(corrupt, "9999\0");
        Assert(noDataOffset >= 0, "GeoTIFF NODATA tag was not found.");
        corrupt[noDataOffset + 3] = (byte)'8';
        File.WriteAllBytes(invalidNoData, corrupt);
        AssertThrows<InvalidDataException>(
            () => TerrainGeoTiff.ReadInt16(invalidNoData, 3, 2),
            "GeoTIFF with invalid NODATA was accepted.");
    }

    private static void ValidateFileSync(string testRoot)
    {
        const int width = 4;
        const int height = 3;
        int count = width * height;
        short[] elevation = Enumerable.Range(0, count)
            .Select(value => (short)(100 + value))
            .ToArray();
        TerrainWorldState state = TerrainWorldState.CreateFromLayers(
            "sync-probe",
            DateTime.UtcNow,
            width,
            height,
            0,
            elevation,
            Enumerable.Repeat((byte)TerrainLandform.Plain, count).ToArray(),
            Enumerable.Repeat((byte)TerrainMaterial.Soil, count).ToArray());
        string exchange = Path.Combine(testRoot, "sync-exchange");
        TerrainSyncResult prepared = TerrainFileSync.PrepareWorkspace(exchange, state);
        string workspace = prepared.WorkspaceDirectory;
        Assert(prepared.Outcome == TerrainSyncOutcome.Prepared,
            "Sync workspace was not prepared.");
        Assert(File.Exists(Path.Combine(workspace, "baseline.json")) &&
               File.Exists(Path.Combine(workspace, "outgoing", "elevation.tif")),
            "Sync workspace is incomplete.");

        short[] incoming = (short[])state.Elevation.Clone();
        incoming[1] += 7;
        WriteIncomingTiff(workspace, state, incoming);
        TerrainSyncResult applied = TerrainFileSync.Pull(
            exchange,
            state,
            TerrainSyncConflictPolicy.Reject);
        Assert(applied.Outcome == TerrainSyncOutcome.Applied &&
               applied.ChangedCells == 1 && state.Elevation[1] == incoming[1],
            "Non-conflicting sync edit was not applied.");
        Assert(!File.Exists(Path.Combine(workspace, "incoming", "elevation.tif")) &&
               Directory.EnumerateFiles(Path.Combine(workspace, "history"), "*.tif").Any(),
            "Applied sync input was not archived.");

        short[] baselineGrid = (short[])state.Elevation.Clone();
        state.ApplyElevationBrush(
            0,
            0,
            0,
            TerrainElevationOperation.Raise,
            0,
            3);
        short[] conflictingIncoming = (short[])baselineGrid.Clone();
        conflictingIncoming[width + 2] -= 5;
        WriteIncomingTiff(workspace, state, conflictingIncoming);
        short localValue = state.Elevation[0];
        TerrainSyncResult rejected = TerrainFileSync.Pull(
            exchange,
            state,
            TerrainSyncConflictPolicy.Reject);
        Assert(rejected.Outcome == TerrainSyncOutcome.Conflict &&
               rejected.ConflictDetected && state.Elevation[0] == localValue,
            "Sync conflict was not rejected.");
        Assert(File.Exists(Path.Combine(workspace, "incoming", "elevation.tif")),
            "Rejected sync input was unexpectedly consumed.");

        TerrainSyncResult branched = TerrainFileSync.Pull(
            exchange,
            state,
            TerrainSyncConflictPolicy.BranchAndApplyIncoming);
        Assert(branched.Outcome == TerrainSyncOutcome.Applied &&
               branched.ConflictDetected && !string.IsNullOrWhiteSpace(branched.BranchPath),
            "Branch-and-apply did not resolve the sync conflict.");
        Assert(File.Exists(Path.Combine(workspace, branched.BranchPath)) &&
               state.Elevation.SequenceEqual(conflictingIncoming),
            "Sync branch or incoming DEM is incorrect.");

        string[] logLines = File.ReadAllLines(Path.Combine(workspace, "changes.jsonl"));
        Assert(logLines.Length == 3,
            "Sync change log does not contain every processed input.");
        Assert(logLines.Select(JObject.Parse).Any(line => (bool)line["conflict_detected"]),
            "Sync change log lost conflict metadata.");
        TerrainSyncBaseline finalBaseline = JsonConvert.DeserializeObject<TerrainSyncBaseline>(
            File.ReadAllText(Path.Combine(workspace, "baseline.json")));
        Assert(finalBaseline.SourceRevision == state.Revision,
            "Sync baseline revision was not advanced after apply.");
    }

    private static void WriteIncomingTiff(
        string workspace,
        TerrainWorldState state,
        short[] elevation)
    {
        TerrainGeoTiff.Write(
            Path.Combine(workspace, "incoming", "elevation.tif"),
            state.Width,
            state.Height,
            elevation,
            TerrainTiffSampleKind.Int16,
            "9999",
            state.ProjectId,
            "core.elevation");
    }

    private static void ValidateFileSyncRollback(string testRoot)
    {
        const int width = 3;
        const int height = 2;
        short[] original = { 1, 2, 3, 4, 5, 6 };
        TerrainWorldState state = TerrainWorldState.CreateFromLayers(
            "sync-rollback-probe",
            DateTime.UtcNow,
            width,
            height,
            0,
            (short[])original.Clone(),
            Enumerable.Repeat((byte)TerrainLandform.Plain, width * height).ToArray(),
            Enumerable.Repeat((byte)TerrainMaterial.Soil, width * height).ToArray());
        string exchange = Path.Combine(testRoot, "sync-rollback-exchange");
        string workspace = TerrainFileSync.PrepareWorkspace(exchange, state).WorkspaceDirectory;
        short[] incoming = (short[])original.Clone();
        incoming[4] = 50;
        WriteIncomingTiff(workspace, state, incoming);

        string blockedLogPath = Path.Combine(workspace, "changes.jsonl");
        Directory.CreateDirectory(blockedLogPath);
        AssertThrows<UnauthorizedAccessException>(
            () => TerrainFileSync.Pull(
                exchange,
                state,
                TerrainSyncConflictPolicy.Reject),
            "A blocked sync change log did not fail the transaction.");
        Assert(state.Elevation.SequenceEqual(original),
            "Failed sync transaction did not roll the DEM back.");
        Assert(File.Exists(Path.Combine(workspace, "incoming", "elevation.tif")),
            "Failed sync transaction did not restore the incoming TIFF.");
        TerrainSyncBaseline baseline = JsonConvert.DeserializeObject<TerrainSyncBaseline>(
            File.ReadAllText(Path.Combine(workspace, "baseline.json")));
        Assert(
            baseline.SourceRevision == state.Revision &&
            TerrainGeoTiff.ReadInt16(
                Path.Combine(workspace, "outgoing", "elevation.tif"),
                width,
                height).SequenceEqual(original),
            "Failed sync transaction did not repair its outgoing baseline.");
        Directory.Delete(blockedLogPath);
    }

    private static short ReadFirstTiffInt16(string path)
    {
        using (BinaryReader reader = new BinaryReader(File.OpenRead(path)))
        {
            Assert(reader.ReadByte() == (byte)'I' && reader.ReadByte() == (byte)'I',
                "Probe expected a little-endian TIFF.");
            Assert(reader.ReadUInt16() == 42, "Probe expected TIFF version 42.");
            reader.BaseStream.Position = reader.ReadUInt32();
            ushort count = reader.ReadUInt16();
            uint firstStripOffset = 0;
            for (int index = 0; index < count; index++)
            {
                ushort tag = reader.ReadUInt16();
                reader.ReadUInt16();
                reader.ReadUInt32();
                uint value = reader.ReadUInt32();
                if (tag == 273)
                {
                    firstStripOffset = value;
                }
            }

            Assert(firstStripOffset > 0, "TIFF strip offset is missing.");
            reader.BaseStream.Position = firstStripOffset;
            return reader.ReadInt16();
        }
    }

    private static int FindAscii(byte[] bytes, string value)
    {
        byte[] token = System.Text.Encoding.ASCII.GetBytes(value);
        for (int start = 0; start <= bytes.Length - token.Length; start++)
        {
            bool matches = true;
            for (int index = 0; index < token.Length; index++)
            {
                matches &= bytes[start + index] == token[index];
            }

            if (matches)
            {
                return start;
            }
        }

        return -1;
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void RunRelief(TerrainWorldState state)
    {
        TerrainReliefService service = new TerrainReliefService();
        Assert(service.TryStartAnalysis(state, out string startError),
            "Relief did not start: " + startError);
        DateTime deadline = DateTime.UtcNow.AddSeconds(
            state.CellCount > 1000000 ? 120 : 10);
        while (service.IsRunning && DateTime.UtcNow < deadline)
        {
            service.Poll(state);
            Thread.Sleep(5);
        }

        service.Poll(state);
        Assert(!service.IsRunning, "Relief analysis timed out.");
        Assert(string.IsNullOrWhiteSpace(service.LastError),
            "Relief failed: " + service.LastError);
        Assert(state.Relief != null && state.Relief.IsCurrent(state),
            "Relief result was not attached.");
    }

    private static void RunHydrology(
        TerrainWorldState state,
        TerrainHydrologyModule module,
        int threshold)
    {
        Assert(
            module.TryStartAnalysis(state, threshold, out string startError),
            "Hydrology did not start: " + startError);
        DateTime deadline = DateTime.UtcNow.AddSeconds(
            state.CellCount > 1000000 ? 120 : 10);
        while (module.IsRunning && DateTime.UtcNow < deadline)
        {
            module.Poll(state);
            Thread.Sleep(5);
        }

        module.Poll(state);
        Assert(!module.IsRunning, "Hydrology analysis timed out.");
        Assert(string.IsNullOrWhiteSpace(module.LastError), "Hydrology failed: " + module.LastError);
    }

    private static void RunErosion(
        TerrainWorldState state,
        TerrainErosionModule module)
    {
        Assert(
            module.TryStartAnalysis(
                state,
                new TerrainErosionParameters
                {
                    Iterations = 2,
                    FlowStrengthPercent = 35,
                    ThermalStrengthPercent = 20,
                    TalusThreshold = 2
                },
                out string startError),
            "Erosion did not start: " + startError);
        DateTime deadline = DateTime.UtcNow.AddSeconds(
            state.CellCount > 1000000 ? 120 : 10);
        while (module.IsRunning && DateTime.UtcNow < deadline)
        {
            module.Poll(state);
            Thread.Sleep(5);
        }

        module.Poll(state);
        Assert(!module.IsRunning, "Erosion analysis timed out.");
        Assert(string.IsNullOrWhiteSpace(module.LastError), "Erosion failed: " + module.LastError);
    }

    private static void ValidateElevationEditing(TerrainWorldState state)
    {
        const int centerX = 2;
        const int centerY = 2;
        int centerIndex = centerY * state.Width + centerX;
        short originalCenter = state.Elevation[centerIndex];

        TerrainElevationEdit edit = state.ApplyElevationBrush(
            centerX,
            centerY,
            1,
            TerrainElevationOperation.Set,
            120,
            1);
        Assert(edit.ChangedCellCount == 5, "Radius-one brush did not use a circle mask.");
        Assert(state.Elevation[centerIndex] == 120, "Brush did not update its center cell.");
        Assert(state.IsDirty, "Elevation edit did not mark the project dirty.");

        state.ApplyElevationEdit(edit, false);
        Assert(state.Elevation[centerIndex] == originalCenter, "Undo did not restore elevation.");

        bool noDataRejected = false;
        try
        {
            state.ApplyElevationBrush(
                centerX,
                centerY,
                0,
                TerrainElevationOperation.Set,
                TerrainElevationEncoding.NoData,
                1);
        }
        catch (ArgumentException)
        {
            noDataRejected = true;
        }

        Assert(noDataRejected, "Reserved NODATA was accepted as an elevation value.");

        state.ApplyElevationBrush(0, 0, 0, TerrainElevationOperation.Set, 9998, 1);
        state.ApplyElevationBrush(0, 0, 0, TerrainElevationOperation.Raise, 0, 1);
        Assert(state.Elevation[0] == 10000, "Raise operation produced reserved NODATA.");
    }

    private static void ValidateArchive(string packagePath, int expectedCells)
    {
        using (ZipArchive archive = ZipFile.OpenRead(packagePath))
        {
            string[] requiredEntries =
            {
                "mimetype",
                "manifest.json",
                "base/map.wbox",
                "layers/elevation.i16",
                "layers/landform.u8",
                "layers/material.u8",
                "modules/hydrology/analysis.json",
                "modules/hydrology/filled_elevation.i16",
                "modules/hydrology/flow_direction.u8",
                "modules/hydrology/flow_accumulation.u32",
                "modules/hydrology/streams.u8",
                "modules/hydrology/watersheds.u32",
                "modules/hydrology/stream_order.u8",
                "modules/hydrology.water_dynamics/state.json",
                "modules/hydrology.water_dynamics/managed_water.u8",
                "modules/erosion.hydraulic/analysis.json",
                "modules/erosion.hydraulic/result_elevation.i16",
                "modules/erosion.hydraulic/net_change.i32"
            };
            foreach (string entry in requiredEntries)
            {
                Assert(archive.GetEntry(entry) != null, "Missing package entry: " + entry);
            }

            using (StreamReader reader = new StreamReader(archive.GetEntry("manifest.json").Open()))
            {
                JObject manifest = JObject.Parse(reader.ReadToEnd());
                Assert((string)manifest["vertical_reference"]["storage_type"] == "int16", "Not Int16.");
                Assert((int)manifest["vertical_reference"]["nodata"] == 9999, "Wrong NODATA.");
                Assert((int)manifest["canvas"]["cell_count"] == expectedCells, "Wrong cell count.");
                Assert(
                    manifest["modules"].Any(module => (string)module["id"] == "hydrology"),
                    "Hydrology module descriptor is missing.");
                Assert(
                    manifest["modules"].Any(module =>
                        (string)module["id"] == "hydrology.water_dynamics"),
                    "Water dynamics module descriptor is missing.");
                Assert(
                    manifest["modules"].Any(module =>
                        (string)module["id"] == "erosion.hydraulic"),
                    "Erosion module descriptor is missing.");
                Assert(
                    manifest["layers"].Count(layer =>
                        (string)layer["module"] == "hydrology") == 6,
                    "Hydrology layer catalog is incomplete.");
                Assert(
                    manifest["layers"].Count(layer =>
                        (string)layer["module"] == "hydrology.water_dynamics") == 1,
                    "Water dynamics layer catalog is incomplete.");
                Assert(
                    manifest["layers"].Count(layer =>
                        (string)layer["module"] == "erosion.hydraulic") == 2,
                    "Erosion layer catalog is incomplete.");
            }
        }
    }

    private static byte[] ReadArchiveEntry(ZipArchive archive, string path)
    {
        ZipArchiveEntry entry = archive.GetEntry(path);
        Assert(entry != null, "Missing package entry: " + path);
        using (MemoryStream output = new MemoryStream())
        using (Stream input = entry.Open())
        {
            input.CopyTo(output);
            return output.ToArray();
        }
    }

    private static void ReplaceArchiveEntry(
        ZipArchive archive,
        string path,
        byte[] bytes)
    {
        archive.GetEntry(path)?.Delete();
        using (Stream output = archive.CreateEntry(path).Open())
        {
            output.Write(bytes, 0, bytes.Length);
        }
    }

    private static void UpdateLayerChecksum(
        ZipArchive archive,
        string layerId,
        byte[] layerBytes)
    {
        JObject manifest = JObject.Parse(
            Encoding.UTF8.GetString(ReadArchiveEntry(archive, "manifest.json")));
        JToken layer = manifest["layers"].FirstOrDefault(
            candidate => (string)candidate["id"] == layerId);
        Assert(layer != null, "Layer descriptor is missing: " + layerId);
        layer["sha256"] = ComputeSha256(layerBytes);
        ReplaceArchiveEntry(
            archive,
            "manifest.json",
            new UTF8Encoding(false).GetBytes(manifest.ToString()));
    }

    private static string ComputeSha256(byte[] bytes)
    {
        using (SHA256 sha = SHA256.Create())
        {
            return string.Concat(sha.ComputeHash(bytes)
                .Select(value => value.ToString("x2")));
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
