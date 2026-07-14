using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using Newtonsoft.Json.Linq;
using TerrainLab;

internal static class Program
{
    private static int Main()
    {
        string testRoot = Path.Combine(
            Path.GetTempPath(),
            "TerrainLabPackageProbe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);

        try
        {
            RunRoundTrip(testRoot);
            Console.WriteLine("WBXGEO package probe passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            Directory.Delete(testRoot, true);
        }
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
                "layers/material.u8"
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
            }
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
