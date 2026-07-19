using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace TerrainLab
{
    public enum TerrainTiffSampleKind
    {
        UInt8,
        Int16,
        UInt16,
        Int32,
        UInt32
    }

    public static class TerrainGeoTiff
    {
        private const ushort TiffVersion = 42;
        private const int RowsPerStrip = 256;

        private const ushort TypeAscii = 2;
        private const ushort TypeShort = 3;
        private const ushort TypeLong = 4;
        private const ushort TypeRational = 5;
        private const ushort TypeDouble = 12;

        public static void Write(
            string path,
            int width,
            int height,
            Array values,
            TerrainTiffSampleKind sampleKind,
            string noData,
            string projectId,
            string layerId,
            double horizontalMetresPerCell =
                TerrainSpatialScale.DefaultHorizontalMetresPerCell,
            TerrainRasterGeoreference georeference = null)
        {
            int count = checked(width * height);
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("GeoTIFF path is required.", nameof(path));
            }

            if (!TerrainMapLimits.TryValidate(width, height, out string limitError))
            {
                throw new ArgumentOutOfRangeException(nameof(width), limitError);
            }

            if (values == null || values.Length != count)
            {
                throw new ArgumentException("GeoTIFF layer does not match the canvas.", nameof(values));
            }

            if (!TerrainSpatialScale.IsValid(horizontalMetresPerCell))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(horizontalMetresPerCell),
                    "GeoTIFF cell size must be between 1 and 1000000 metres.");
            }

            georeference?.Validate(width, height);
            ValidateArrayType(values, sampleKind);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            int bytesPerSample = GetBytesPerSample(sampleKind);
            int stripCount = (height + RowsPerStrip - 1) / RowsPerStrip;
            uint[] stripByteCounts = new uint[stripCount];
            uint[] stripOffsets = new uint[stripCount];
            for (int strip = 0; strip < stripCount; strip++)
            {
                int rowStart = strip * RowsPerStrip;
                int rows = Math.Min(RowsPerStrip, height - rowStart);
                stripByteCounts[strip] = checked((uint)(rows * width * bytesPerSample));
            }

            string description = JsonConvert.SerializeObject(new
            {
                format = "terrainlab-geotiff",
                schema_version = "1.2.0",
                project_id = projectId,
                layer_id = layerId,
                origin = "south-west",
                file_row_order = "north-to-south",
                horizontal_metres_per_cell = horizontalMetresPerCell,
                source_file_name = georeference?.SourceFileName,
                source_epsg = georeference?.Epsg,
                pixel_interpretation = georeference?.PixelInterpretation ??
                    "area"
            }, Formatting.None);

            List<TiffEntry> entries = new List<TiffEntry>
            {
                TiffEntry.InlineLong(256, checked((uint)width)),
                TiffEntry.InlineLong(257, checked((uint)height)),
                TiffEntry.InlineShort(258, checked((ushort)(bytesPerSample * 8))),
                TiffEntry.InlineShort(259, 1),
                TiffEntry.InlineShort(262, 1),
                new TiffEntry(270, TypeAscii, Ascii(description)),
                new TiffEntry(273, TypeLong, UInt32Bytes(stripOffsets)),
                TiffEntry.InlineShort(277, 1),
                TiffEntry.InlineLong(278, RowsPerStrip),
                new TiffEntry(279, TypeLong, UInt32Bytes(stripByteCounts)),
                new TiffEntry(282, TypeRational, RationalBytes(1, 1)),
                new TiffEntry(283, TypeRational, RationalBytes(1, 1)),
                TiffEntry.InlineShort(284, 1),
                TiffEntry.InlineShort(296, 1),
                TiffEntry.InlineShort(339, IsSigned(sampleKind) ? (ushort)2 : (ushort)1),
                new TiffEntry(42113, TypeAscii, Ascii(noData ?? string.Empty))
            };
            if (georeference == null)
            {
                byte[] geoAscii = Ascii("WorldBox Local ENGCRS|");
                ushort[] geoKeys =
                {
                    1, 1, 0, 3,
                    1024, 0, 1, 32767,
                    1025, 0, 1, 1,
                    1026, 34737, (ushort)(geoAscii.Length - 1), 0
                };
                entries.Add(new TiffEntry(33550, TypeDouble, DoubleBytes(
                    horizontalMetresPerCell,
                    horizontalMetresPerCell,
                    0.0)));
                entries.Add(new TiffEntry(33922, TypeDouble, DoubleBytes(
                    0.0,
                    0.0,
                    0.0,
                    0.0,
                    height * horizontalMetresPerCell,
                    0.0)));
                entries.Add(new TiffEntry(
                    34735,
                    TypeShort,
                    UInt16Bytes(geoKeys)));
                entries.Add(new TiffEntry(34737, TypeAscii, geoAscii));
            }
            else
            {
                entries.Add(new TiffEntry(
                    34264,
                    TypeDouble,
                    DoubleBytes(georeference.GetModelTransformation())));
                entries.Add(new TiffEntry(
                    34735,
                    TypeShort,
                    UInt16Bytes(georeference.GetGeoKeyDirectory())));
                if (georeference.GeoDoubleParams?.Length > 0)
                {
                    entries.Add(new TiffEntry(
                        34736,
                        TypeDouble,
                        DoubleBytes(georeference.GeoDoubleParams)));
                }

                if (!string.IsNullOrEmpty(georeference.GeoAsciiParams))
                {
                    entries.Add(new TiffEntry(
                        34737,
                        TypeAscii,
                        Ascii(georeference.GeoAsciiParams)));
                }
            }

            entries.Sort((left, right) => left.Tag.CompareTo(right.Tag));

            uint extraOffset = checked((uint)(8 + 2 + entries.Count * 12 + 4));
            foreach (TiffEntry entry in entries)
            {
                if (!entry.IsInline)
                {
                    extraOffset = Align4(extraOffset);
                    entry.Offset = extraOffset;
                    extraOffset = checked(extraOffset + (uint)entry.Data.Length);
                }
            }

            uint pixelOffset = Align4(extraOffset);
            uint runningOffset = pixelOffset;
            for (int strip = 0; strip < stripCount; strip++)
            {
                stripOffsets[strip] = runningOffset;
                runningOffset = checked(runningOffset + stripByteCounts[strip]);
            }

            TiffEntry stripOffsetsEntry = entries.Single(entry => entry.Tag == 273);
            stripOffsetsEntry.SetData(UInt32Bytes(stripOffsets));

            string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                using (BinaryWriter writer = new BinaryWriter(stream, Encoding.ASCII, true))
                {
                    writer.Write((byte)'I');
                    writer.Write((byte)'I');
                    writer.Write(TiffVersion);
                    writer.Write((uint)8);
                    writer.Write(checked((ushort)entries.Count));
                    foreach (TiffEntry entry in entries)
                    {
                        writer.Write(entry.Tag);
                        writer.Write(entry.Type);
                        writer.Write(entry.Count);
                        if (entry.IsInline)
                        {
                            writer.Write(entry.GetInlineBytes());
                        }
                        else
                        {
                            writer.Write(entry.Offset);
                        }
                    }

                    writer.Write((uint)0);
                    foreach (TiffEntry entry in entries.Where(entry => !entry.IsInline))
                    {
                        stream.Position = entry.Offset;
                        writer.Write(entry.Data);
                    }

                    stream.Position = pixelOffset;
                    WritePixels(writer, width, height, values, sampleKind);
                }

                ReplaceFile(temporaryPath, path);
                WriteSidecars(
                    path,
                    height,
                    projectId,
                    horizontalMetresPerCell,
                    georeference);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    try
                    {
                        File.Delete(temporaryPath);
                    }
                    catch
                    {
                    }
                }
            }
        }

        public static short[] ReadInt16(
            string path,
            int expectedWidth,
            int expectedHeight,
            double? expectedHorizontalMetresPerCell = null,
            TerrainRasterGeoreference expectedGeoreference = null)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new FileNotFoundException("GeoTIFF was not found.", path);
            }

            if (!TerrainMapLimits.TryValidate(expectedWidth, expectedHeight, out string limitError))
            {
                throw new InvalidDataException(limitError);
            }

            if (expectedHorizontalMetresPerCell.HasValue &&
                !TerrainSpatialScale.IsValid(
                    expectedHorizontalMetresPerCell.Value))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(expectedHorizontalMetresPerCell));
            }

            expectedGeoreference?.Validate(expectedWidth, expectedHeight);
            using (FileStream stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            using (BinaryReader reader = new BinaryReader(stream, Encoding.ASCII, true))
            {
                if (stream.Length < 8 ||
                    reader.ReadByte() != (byte)'I' || reader.ReadByte() != (byte)'I')
                {
                    throw new InvalidDataException("Only little-endian TIFF is supported.");
                }

                if (reader.ReadUInt16() != TiffVersion)
                {
                    throw new InvalidDataException("Invalid TIFF version.");
                }

                uint ifdOffset = reader.ReadUInt32();
                if (ifdOffset > stream.Length - 2)
                {
                    throw new InvalidDataException("TIFF IFD lies outside the file.");
                }

                stream.Position = ifdOffset;
                ushort entryCount = reader.ReadUInt16();
                if (entryCount == 0 || entryCount > 256 ||
                    stream.Position + entryCount * 12L + 4L > stream.Length)
                {
                    throw new InvalidDataException("TIFF IFD is invalid.");
                }

                Dictionary<ushort, TiffReadEntry> entries = new Dictionary<ushort, TiffReadEntry>();
                for (int index = 0; index < entryCount; index++)
                {
                    ushort tag = reader.ReadUInt16();
                    ushort type = reader.ReadUInt16();
                    uint count = reader.ReadUInt32();
                    byte[] value = reader.ReadBytes(4);
                    if (!entries.ContainsKey(tag))
                    {
                        entries.Add(tag, new TiffReadEntry(tag, type, count, value));
                    }
                }

                int width = checked((int)ReadSingleUInt(entries, stream, 256));
                int height = checked((int)ReadSingleUInt(entries, stream, 257));
                if (width != expectedWidth || height != expectedHeight)
                {
                    throw new InvalidDataException(string.Format(
                        CultureInfo.InvariantCulture,
                        "GeoTIFF dimensions {0} x {1} do not match {2} x {3}.",
                        width,
                        height,
                        expectedWidth,
                        expectedHeight));
                }

                if (expectedHorizontalMetresPerCell.HasValue)
                {
                    double expectedScale = expectedHorizontalMetresPerCell.Value;
                    double[] pixelScale = ReadDoubleArray(
                        entries,
                        stream,
                        33550,
                        3);
                    double tolerance = Math.Max(1e-9, expectedScale * 1e-9);
                    if (Math.Abs(pixelScale[0] - expectedScale) > tolerance ||
                        Math.Abs(pixelScale[1] - expectedScale) > tolerance ||
                        Math.Abs(pixelScale[2]) > tolerance)
                    {
                        throw new InvalidDataException(
                            "GeoTIFF metric cell size does not match the active project.");
                    }
                }

                if (expectedGeoreference != null)
                {
                    ValidateGeoreference(
                        entries,
                        stream,
                        expectedGeoreference);
                }

                if (ReadSingleUInt(entries, stream, 258) != 16 ||
                    ReadSingleUInt(entries, stream, 259) != 1 ||
                    ReadSingleUInt(entries, stream, 277) != 1 ||
                    ReadSingleUInt(entries, stream, 339) != 2)
                {
                    throw new InvalidDataException(
                        "GeoTIFF must be uncompressed single-band signed Int16.");
                }

                uint rowsPerStrip = ReadSingleUInt(entries, stream, 278);
                if (rowsPerStrip == 0 || rowsPerStrip > int.MaxValue)
                {
                    throw new InvalidDataException("GeoTIFF rows-per-strip is invalid.");
                }

                int expectedStrips = checked((int)(
                    (height + (long)rowsPerStrip - 1L) / rowsPerStrip));
                uint[] offsets = ReadUIntArray(entries, stream, 273, expectedStrips);
                uint[] byteCounts = ReadUIntArray(entries, stream, 279, expectedStrips);

                if (!entries.TryGetValue(42113, out TiffReadEntry noDataEntry))
                {
                    throw new InvalidDataException("GeoTIFF NODATA tag is required.");
                }

                if (noDataEntry.Count == 0 || noDataEntry.Count > 32)
                {
                    throw new InvalidDataException("GeoTIFF NODATA tag is invalid.");
                }

                string noData = ReadAscii(noDataEntry, stream).TrimEnd('\0', '|');
                if (!string.Equals(
                    noData,
                    TerrainElevationEncoding.NoData.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal))
                {
                    throw new InvalidDataException("GeoTIFF NODATA must be 9999.");
                }

                short[] result = new short[checked(width * height)];
                long previousStripEnd = -1;
                for (int strip = 0; strip < expectedStrips; strip++)
                {
                    int rasterRowStart = strip * (int)rowsPerStrip;
                    int rows = Math.Min((int)rowsPerStrip, height - rasterRowStart);
                    uint expectedBytes = checked((uint)(rows * width * sizeof(short)));
                    if (byteCounts[strip] != expectedBytes ||
                        offsets[strip] > stream.Length ||
                        byteCounts[strip] > stream.Length - offsets[strip])
                    {
                        throw new InvalidDataException("GeoTIFF strip lies outside the file.");
                    }

                    if (offsets[strip] < previousStripEnd)
                    {
                        throw new InvalidDataException("GeoTIFF strips overlap or are out of order.");
                    }

                    previousStripEnd = offsets[strip] + (long)byteCounts[strip];

                    stream.Position = offsets[strip];
                    for (int localRow = 0; localRow < rows; localRow++)
                    {
                        int rasterRow = rasterRowStart + localRow;
                        int sourceY = height - 1 - rasterRow;
                        int destinationOffset = sourceY * width;
                        for (int x = 0; x < width; x++)
                        {
                            result[destinationOffset + x] = reader.ReadInt16();
                        }
                    }
                }

                return result;
            }
        }

        private static void WritePixels(
            BinaryWriter writer,
            int width,
            int height,
            Array values,
            TerrainTiffSampleKind sampleKind)
        {
            for (int rasterY = 0; rasterY < height; rasterY++)
            {
                int sourceOffset = (height - 1 - rasterY) * width;
                for (int x = 0; x < width; x++)
                {
                    int index = sourceOffset + x;
                    switch (sampleKind)
                    {
                        case TerrainTiffSampleKind.UInt8:
                            writer.Write(((byte[])values)[index]);
                            break;
                        case TerrainTiffSampleKind.Int16:
                            writer.Write(((short[])values)[index]);
                            break;
                        case TerrainTiffSampleKind.UInt16:
                            writer.Write(((ushort[])values)[index]);
                            break;
                        case TerrainTiffSampleKind.Int32:
                            writer.Write(((int[])values)[index]);
                            break;
                        case TerrainTiffSampleKind.UInt32:
                            writer.Write(((uint[])values)[index]);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(sampleKind));
                    }
                }
            }
        }

        private static void WriteSidecars(
            string tiffPath,
            int height,
            string projectId,
            double horizontalMetresPerCell,
            TerrainRasterGeoreference georeference)
        {
            string stem = Path.Combine(
                Path.GetDirectoryName(tiffPath),
                Path.GetFileNameWithoutExtension(tiffPath));
            double[] worldFile = georeference?.GetWorldFile() ?? new[]
            {
                horizontalMetresPerCell,
                0d,
                0d,
                -horizontalMetresPerCell,
                horizontalMetresPerCell * 0.5d,
                (height - 0.5d) * horizontalMetresPerCell
            };
            File.WriteAllText(
                stem + ".tfw",
                string.Join(
                    Environment.NewLine,
                    worldFile.Select(TerrainRasterGeoreference.FormatNumber)) +
                Environment.NewLine,
                new UTF8Encoding(false));

            string projection = georeference == null
                ? GetCrsWkt(projectId)
                : georeference.CrsWkt;
            string projectionPath = stem + ".prj";
            if (!string.IsNullOrWhiteSpace(projection))
            {
                File.WriteAllText(
                    projectionPath,
                    projection.TrimEnd() + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            else if (File.Exists(projectionPath))
            {
                File.Delete(projectionPath);
            }

            string georeferencePath =
                TerrainRasterGeoreference.GetRasterSidecarPath(tiffPath);
            if (georeference != null)
            {
                georeference.WriteRasterSidecar(tiffPath);
            }
            else if (File.Exists(georeferencePath))
            {
                File.Delete(georeferencePath);
            }
        }

        internal static string GetCrsWkt(string projectId)
        {
            string name = string.IsNullOrWhiteSpace(projectId)
                ? "WorldBox local"
                : "WorldBox / " + projectId.Replace("\"", string.Empty);
            return
                "ENGCRS[\"" + name +
                "\",EDATUM[\"WorldBox local datum\"],CS[Cartesian,2]," +
                "AXIS[\"easting (X)\",east,ORDER[1],LENGTHUNIT[\"metre\",1]]," +
                "AXIS[\"northing (Y)\",north,ORDER[2],LENGTHUNIT[\"metre\",1]]]";
        }

        private static uint ReadSingleUInt(
            IDictionary<ushort, TiffReadEntry> entries,
            Stream stream,
            ushort tag)
        {
            if (!entries.TryGetValue(tag, out TiffReadEntry entry) || entry.Count != 1)
            {
                throw new InvalidDataException("Required TIFF tag is missing: " + tag);
            }

            byte[] bytes = ReadEntryBytes(entry, stream);
            if (entry.Type == TypeShort && bytes.Length >= 2)
            {
                return BitConverter.ToUInt16(bytes, 0);
            }

            if (entry.Type == TypeLong && bytes.Length >= 4)
            {
                return BitConverter.ToUInt32(bytes, 0);
            }

            throw new InvalidDataException("TIFF tag has an invalid numeric type: " + tag);
        }

        private static uint[] ReadUIntArray(
            IDictionary<ushort, TiffReadEntry> entries,
            Stream stream,
            ushort tag,
            int expectedCount)
        {
            if (!entries.TryGetValue(tag, out TiffReadEntry entry) ||
                entry.Type != TypeLong || entry.Count != expectedCount)
            {
                throw new InvalidDataException("TIFF strip catalog is inconsistent: " + tag);
            }

            byte[] bytes = ReadEntryBytes(entry, stream);
            uint[] result = new uint[entry.Count];
            for (int index = 0; index < result.Length; index++)
            {
                result[index] = BitConverter.ToUInt32(bytes, index * sizeof(uint));
            }

            return result;
        }

        private static double[] ReadDoubleArray(
            IDictionary<ushort, TiffReadEntry> entries,
            Stream stream,
            ushort tag,
            int expectedCount)
        {
            if (!entries.TryGetValue(tag, out TiffReadEntry entry) ||
                entry.Type != TypeDouble || entry.Count != expectedCount)
            {
                throw new InvalidDataException(
                    "TIFF double array is missing or inconsistent: " + tag);
            }

            byte[] bytes = ReadEntryBytes(entry, stream);
            double[] result = new double[expectedCount];
            for (int index = 0; index < result.Length; index++)
            {
                result[index] = BitConverter.ToDouble(
                    bytes,
                    index * sizeof(double));
            }

            return result;
        }

        private static ushort[] TryReadUShortArray(
            IDictionary<ushort, TiffReadEntry> entries,
            Stream stream,
            ushort tag)
        {
            if (!entries.TryGetValue(tag, out TiffReadEntry entry))
            {
                return Array.Empty<ushort>();
            }

            if (entry.Type != TypeShort || entry.Count > 4096)
            {
                throw new InvalidDataException(
                    "TIFF unsigned-short array is inconsistent: " + tag);
            }

            byte[] bytes = ReadEntryBytes(entry, stream);
            ushort[] result = new ushort[entry.Count];
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            return result;
        }

        private static void ValidateGeoreference(
            IDictionary<ushort, TiffReadEntry> entries,
            Stream stream,
            TerrainRasterGeoreference expected)
        {
            ushort[] geoKeys = TryReadUShortArray(entries, stream, 34735);
            string pixelInterpretation =
                GetGeoKeyValue(geoKeys, 1025) == 2 ? "point" : "area";
            if (!string.Equals(
                pixelInterpretation,
                expected.PixelInterpretation,
                StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "GeoTIFF pixel-is-area/point semantics do not match the active project.");
            }

            if (expected.Epsg.HasValue &&
                expected.Epsg.Value <= ushort.MaxValue)
            {
                int? actualEpsg =
                    GetGeoKeyValue(geoKeys, 3072) ??
                    GetGeoKeyValue(geoKeys, 2048);
                if (!actualEpsg.HasValue ||
                    actualEpsg.Value != expected.Epsg.Value)
                {
                    throw new InvalidDataException(
                        "GeoTIFF horizontal CRS does not match the active project.");
                }
            }
            else if (expected.GeoKeyDirectory?.Length > 0 &&
                !geoKeys.SequenceEqual(expected.GeoKeyDirectory))
            {
                throw new InvalidDataException(
                    "GeoTIFF user-defined CRS does not match the active project.");
            }
            else if (!expected.Epsg.HasValue ||
                expected.Epsg.Value > ushort.MaxValue)
            {
                double[] actualDoubleParams =
                    TryReadDoubleArray(entries, stream, 34736);
                double[] expectedDoubleParams =
                    expected.GeoDoubleParams ?? Array.Empty<double>();
                if (actualDoubleParams.Length !=
                    expectedDoubleParams.Length)
                {
                    throw new InvalidDataException(
                        "GeoTIFF user-defined CRS parameters do not match the active project.");
                }

                for (int index = 0; index < actualDoubleParams.Length; index++)
                {
                    double tolerance = Math.Max(
                        1e-12,
                        Math.Abs(expectedDoubleParams[index]) * 1e-12);
                    if (Math.Abs(
                        actualDoubleParams[index] -
                        expectedDoubleParams[index]) > tolerance)
                    {
                        throw new InvalidDataException(
                            "GeoTIFF user-defined CRS parameters do not match the active project.");
                    }
                }

                string actualAsciiParams =
                    TryReadAscii(entries, stream, 34737);
                if (!string.Equals(
                    actualAsciiParams,
                    (expected.GeoAsciiParams ?? string.Empty)
                        .TrimEnd('\0'),
                    StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "GeoTIFF user-defined CRS citation does not match the active project.");
                }
            }

            bool expectedProjected =
                string.Equals(
                    expected.CrsKind,
                    "projected",
                    StringComparison.Ordinal) ||
                string.Equals(
                    expected.CrsKind,
                    "compound_projected",
                    StringComparison.Ordinal);
            bool expectedGeographic =
                string.Equals(
                    expected.CrsKind,
                    "geographic",
                    StringComparison.Ordinal) ||
                string.Equals(
                    expected.CrsKind,
                    "compound_geographic",
                    StringComparison.Ordinal);
            int? modelType = GetGeoKeyValue(geoKeys, 1024);
            int? expectedModelType = expectedProjected
                ? 1
                : expectedGeographic
                    ? 2
                    : (int?)null;
            if (modelType.HasValue &&
                expectedModelType.HasValue &&
                modelType.Value != expectedModelType.Value)
            {
                throw new InvalidDataException(
                    "GeoTIFF CRS model type does not match the active project.");
            }

            if (expected.VerticalEpsg.HasValue &&
                expected.VerticalEpsg.Value <= ushort.MaxValue &&
                GetGeoKeyValue(geoKeys, 4096) != expected.VerticalEpsg.Value)
            {
                throw new InvalidDataException(
                    "GeoTIFF vertical CRS does not match the active project.");
            }

            double[] actual = ReadRasterTransform(
                entries,
                stream,
                string.Equals(
                    pixelInterpretation,
                    "point",
                    StringComparison.Ordinal));
            for (int index = 0; index < 6; index++)
            {
                double expectedValue = expected.RasterToCrs[index];
                double tolerance = Math.Max(
                    1e-10,
                    Math.Abs(expectedValue) * 1e-10);
                if (Math.Abs(actual[index] - expectedValue) > tolerance)
                {
                    throw new InvalidDataException(
                        "GeoTIFF affine transform does not match the active project.");
                }
            }
        }

        private static double[] ReadRasterTransform(
            IDictionary<ushort, TiffReadEntry> entries,
            Stream stream,
            bool pixelIsPoint)
        {
            double[] transform;
            if (entries.ContainsKey(34264))
            {
                double[] matrix = ReadDoubleArray(
                    entries,
                    stream,
                    34264,
                    16);
                transform = new[]
                {
                    matrix[3],
                    matrix[0],
                    matrix[1],
                    matrix[7],
                    matrix[4],
                    matrix[5]
                };
            }
            else
            {
                double[] scale = ReadDoubleArray(
                    entries,
                    stream,
                    33550,
                    3);
                if (!entries.TryGetValue(
                    33922,
                    out TiffReadEntry tiePointEntry) ||
                    tiePointEntry.Type != TypeDouble ||
                    tiePointEntry.Count < 6 ||
                    tiePointEntry.Count > 4096)
                {
                    throw new InvalidDataException(
                        "GeoTIFF model tie point is missing or inconsistent.");
                }

                byte[] tiePointBytes = ReadEntryBytes(tiePointEntry, stream);
                double[] tiePoint = new double[6];
                Buffer.BlockCopy(
                    tiePointBytes,
                    0,
                    tiePoint,
                    0,
                    6 * sizeof(double));
                transform = new[]
                {
                    tiePoint[3] - tiePoint[0] * scale[0],
                    scale[0],
                    0d,
                    tiePoint[4] + tiePoint[1] * scale[1],
                    0d,
                    -scale[1]
                };
            }

            if (pixelIsPoint)
            {
                transform[0] -= 0.5d * transform[1] +
                    0.5d * transform[2];
                transform[3] -= 0.5d * transform[4] +
                    0.5d * transform[5];
            }

            if (transform.Any(value =>
                    double.IsNaN(value) || double.IsInfinity(value)) ||
                Math.Abs(
                    transform[1] * transform[5] -
                    transform[2] * transform[4]) <= 1e-18)
            {
                throw new InvalidDataException(
                    "GeoTIFF affine transform is invalid.");
            }

            return transform;
        }

        private static double[] TryReadDoubleArray(
            IDictionary<ushort, TiffReadEntry> entries,
            Stream stream,
            ushort tag)
        {
            if (!entries.TryGetValue(tag, out TiffReadEntry entry))
            {
                return Array.Empty<double>();
            }

            if (entry.Type != TypeDouble || entry.Count > 4096)
            {
                throw new InvalidDataException(
                    "TIFF double array is inconsistent: " + tag);
            }

            byte[] bytes = ReadEntryBytes(entry, stream);
            double[] result = new double[entry.Count];
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            if (result.Any(value =>
                double.IsNaN(value) || double.IsInfinity(value)))
            {
                throw new InvalidDataException(
                    "TIFF double array contains a non-finite value: " + tag);
            }

            return result;
        }

        private static string TryReadAscii(
            IDictionary<ushort, TiffReadEntry> entries,
            Stream stream,
            ushort tag)
        {
            if (!entries.TryGetValue(tag, out TiffReadEntry entry))
            {
                return string.Empty;
            }

            if (entry.Count > 65536)
            {
                throw new InvalidDataException(
                    "TIFF ASCII array is too large: " + tag);
            }

            return ReadAscii(entry, stream).TrimEnd('\0');
        }

        private static int? GetGeoKeyValue(
            ushort[] directory,
            ushort keyId)
        {
            if (directory == null || directory.Length < 4)
            {
                return null;
            }

            int count = directory[3];
            if (directory.Length < 4 + count * 4)
            {
                return null;
            }

            for (int index = 0; index < count; index++)
            {
                int offset = 4 + index * 4;
                if (directory[offset] == keyId &&
                    directory[offset + 1] == 0 &&
                    directory[offset + 2] == 1)
                {
                    int value = directory[offset + 3];
                    return value == 0 || value == 32767
                        ? (int?)null
                        : value;
                }
            }

            return null;
        }

        private static string ReadAscii(TiffReadEntry entry, Stream stream)
        {
            if (entry.Type != TypeAscii)
            {
                throw new InvalidDataException("TIFF ASCII tag has the wrong type: " + entry.Tag);
            }

            return Encoding.ASCII.GetString(ReadEntryBytes(entry, stream));
        }

        private static byte[] ReadEntryBytes(TiffReadEntry entry, Stream stream)
        {
            int typeSize = GetTypeSize(entry.Type);
            if (entry.Count > int.MaxValue / typeSize)
            {
                throw new InvalidDataException("TIFF tag is too large: " + entry.Tag);
            }

            int length = checked((int)entry.Count * typeSize);
            if (length <= 4)
            {
                return entry.Value.Take(length).ToArray();
            }

            uint offset = BitConverter.ToUInt32(entry.Value, 0);
            if (offset > stream.Length || length > stream.Length - offset)
            {
                throw new InvalidDataException("TIFF tag lies outside the file: " + entry.Tag);
            }

            long previous = stream.Position;
            try
            {
                stream.Position = offset;
                byte[] bytes = new byte[length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int current = stream.Read(bytes, read, bytes.Length - read);
                    if (current == 0)
                    {
                        throw new EndOfStreamException("TIFF tag is truncated: " + entry.Tag);
                    }

                    read += current;
                }

                return bytes;
            }
            finally
            {
                stream.Position = previous;
            }
        }

        private static int GetTypeSize(ushort type)
        {
            switch (type)
            {
                case 1:
                case TypeAscii:
                    return 1;
                case TypeShort:
                    return 2;
                case TypeLong:
                    return 4;
                case TypeRational:
                case TypeDouble:
                    return 8;
                default:
                    throw new InvalidDataException("Unsupported TIFF field type: " + type);
            }
        }

        private static int GetBytesPerSample(TerrainTiffSampleKind sampleKind)
        {
            switch (sampleKind)
            {
                case TerrainTiffSampleKind.UInt8:
                    return 1;
                case TerrainTiffSampleKind.Int16:
                case TerrainTiffSampleKind.UInt16:
                    return 2;
                case TerrainTiffSampleKind.Int32:
                case TerrainTiffSampleKind.UInt32:
                    return 4;
                default:
                    throw new ArgumentOutOfRangeException(nameof(sampleKind));
            }
        }

        private static bool IsSigned(TerrainTiffSampleKind sampleKind)
        {
            return sampleKind == TerrainTiffSampleKind.Int16 ||
                   sampleKind == TerrainTiffSampleKind.Int32;
        }

        private static void ValidateArrayType(Array values, TerrainTiffSampleKind sampleKind)
        {
            bool valid = sampleKind == TerrainTiffSampleKind.UInt8 && values is byte[] ||
                         sampleKind == TerrainTiffSampleKind.Int16 && values is short[] ||
                         sampleKind == TerrainTiffSampleKind.UInt16 && values is ushort[] ||
                         sampleKind == TerrainTiffSampleKind.Int32 && values is int[] ||
                         sampleKind == TerrainTiffSampleKind.UInt32 && values is uint[];
            if (!valid)
            {
                throw new ArgumentException("GeoTIFF sample kind does not match the array type.");
            }
        }

        private static byte[] Ascii(string value)
        {
            return Encoding.ASCII.GetBytes((value ?? string.Empty) + "\0");
        }

        private static byte[] UInt16Bytes(ushort[] values)
        {
            byte[] bytes = new byte[values.Length * sizeof(ushort)];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        private static byte[] UInt32Bytes(uint[] values)
        {
            byte[] bytes = new byte[values.Length * sizeof(uint)];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        private static byte[] DoubleBytes(params double[] values)
        {
            byte[] bytes = new byte[values.Length * sizeof(double)];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        private static byte[] RationalBytes(uint numerator, uint denominator)
        {
            return UInt32Bytes(new[] { numerator, denominator });
        }

        private static uint Align4(uint value)
        {
            return checked((value + 3u) & ~3u);
        }

        private static void ReplaceFile(string temporaryPath, string destinationPath)
        {
            if (File.Exists(destinationPath))
            {
                File.Replace(temporaryPath, destinationPath, null);
            }
            else
            {
                File.Move(temporaryPath, destinationPath);
            }
        }

        private sealed class TiffEntry
        {
            public TiffEntry(ushort tag, ushort type, byte[] data)
            {
                Tag = tag;
                Type = type;
                Data = data ?? throw new ArgumentNullException(nameof(data));
                Count = checked((uint)(data.Length / GetTypeSize(type)));
                if (Count * GetTypeSize(type) != data.Length)
                {
                    throw new ArgumentException("TIFF entry data is misaligned.", nameof(data));
                }
            }

            public ushort Tag { get; }

            public ushort Type { get; }

            public uint Count { get; }

            public byte[] Data { get; private set; }

            public uint Offset { get; set; }

            public bool IsInline => Data.Length <= 4;

            public static TiffEntry InlineShort(ushort tag, ushort value)
            {
                return new TiffEntry(tag, TypeShort, BitConverter.GetBytes(value));
            }

            public static TiffEntry InlineLong(ushort tag, uint value)
            {
                return new TiffEntry(tag, TypeLong, BitConverter.GetBytes(value));
            }

            public void SetData(byte[] data)
            {
                if (data == null || data.Length != Data.Length)
                {
                    throw new InvalidOperationException("TIFF entry size may not change after layout.");
                }

                Data = data;
            }

            public byte[] GetInlineBytes()
            {
                byte[] result = new byte[4];
                Buffer.BlockCopy(Data, 0, result, 0, Data.Length);
                return result;
            }
        }

        private sealed class TiffReadEntry
        {
            public TiffReadEntry(ushort tag, ushort type, uint count, byte[] value)
            {
                Tag = tag;
                Type = type;
                Count = count;
                Value = value;
            }

            public ushort Tag { get; }

            public ushort Type { get; }

            public uint Count { get; }

            public byte[] Value { get; }
        }
    }

    public sealed class TerrainGisLayerManifest
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("file")]
        public string File { get; set; }

        [JsonProperty("data_type")]
        public string DataType { get; set; }

        [JsonProperty("nodata")]
        public string NoData { get; set; }

        [JsonProperty("sha256")]
        public string Sha256 { get; set; }
    }

    public sealed class TerrainGisManifest
    {
        [JsonProperty("format")]
        public string Format { get; set; }

        [JsonProperty("schema_version")]
        public string SchemaVersion { get; set; }

        [JsonProperty("project_id")]
        public string ProjectId { get; set; }

        [JsonProperty("source_revision")]
        public long SourceRevision { get; set; }

        [JsonProperty("source_elevation_sha256")]
        public string SourceElevationSha256 { get; set; }

        [JsonProperty("created_utc")]
        public DateTime CreatedUtc { get; set; }

        [JsonProperty("width")]
        public int Width { get; set; }

        [JsonProperty("height")]
        public int Height { get; set; }

        [JsonProperty("horizontal_metres_per_cell")]
        public double HorizontalMetresPerCell { get; set; }

        [JsonProperty("crs_wkt")]
        public string CrsWkt { get; set; }

        [JsonProperty("local_crs_wkt")]
        public string LocalCrsWkt { get; set; }

        [JsonProperty("georeference", NullValueHandling = NullValueHandling.Ignore)]
        public TerrainRasterGeoreference Georeference { get; set; }

        [JsonProperty("vertical_unit")]
        public string VerticalUnit { get; set; }

        [JsonProperty("sea_level")]
        public short SeaLevel { get; set; }

        [JsonProperty("layers")]
        public List<TerrainGisLayerManifest> Layers { get; set; }
    }

    public static class TerrainGisExporter
    {
        public static string Export(string parentDirectory, TerrainWorldState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            Directory.CreateDirectory(parentDirectory);
            string token = state.ProjectId.Length > 8
                ? state.ProjectId.Substring(0, 8)
                : state.ProjectId;
            string directory = Path.Combine(
                parentDirectory,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "terrainlab-gis-{0}-{1:yyyyMMdd-HHmmss}",
                    token,
                    DateTime.UtcNow));
            int suffix = 2;
            string candidate = directory;
            while (Directory.Exists(candidate))
            {
                candidate = directory + "-" + suffix++;
            }

            directory = candidate;
            string stagingDirectory = directory + ".tmp-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(stagingDirectory);
            try
            {
                string elevationHash = TerrainReliefAnalyzer.ComputeSourceSha256(state.Elevation);
                List<TerrainGisLayerManifest> layers = new List<TerrainGisLayerManifest>();
                WriteLayer(stagingDirectory, state, layers, "core.elevation", "elevation.tif",
                    state.Elevation, TerrainTiffSampleKind.Int16, "9999", "int16");
                WriteLayer(stagingDirectory, state, layers, "core.landform", "landform.tif",
                    state.Landform, TerrainTiffSampleKind.UInt8, "255", "uint8");
                WriteLayer(stagingDirectory, state, layers, "core.material", "material.tif",
                    state.Material, TerrainTiffSampleKind.UInt8, "255", "uint8");

                if (state.Relief != null && state.Relief.IsCurrent(state))
                {
                    RequireSourceHash("Relief", elevationHash, state.Relief.SourceSha256);
                    WriteLayer(stagingDirectory, state, layers, "relief.slope", "slope.tif",
                        state.Relief.SlopeTenths, TerrainTiffSampleKind.UInt16, "65535", "uint16");
                    WriteLayer(stagingDirectory, state, layers, "relief.aspect", "aspect.tif",
                        state.Relief.AspectTenths, TerrainTiffSampleKind.UInt16, "65535", "uint16");
                    WriteLayer(stagingDirectory, state, layers, "relief.hillshade", "hillshade.tif",
                        state.Relief.Hillshade, TerrainTiffSampleKind.UInt8, "255", "uint8");
                    WriteLayer(stagingDirectory, state, layers, "relief.ruggedness", "ruggedness.tif",
                        state.Relief.Ruggedness, TerrainTiffSampleKind.UInt16, "65535", "uint16");
                }

                if (state.Hydrology != null && state.Hydrology.IsCurrent(state))
                {
                    RequireSourceHash(
                        "Hydrology",
                        TerrainHydrologyAnalyzer.ComputeSourceSha256(
                            state.Elevation,
                            state.Landform),
                        state.Hydrology.SourceSha256);
                    WriteLayer(stagingDirectory, state, layers, "hydrology.filled_elevation", "filled_elevation.tif",
                        state.Hydrology.FilledElevation, TerrainTiffSampleKind.Int16, "9999", "int16");
                    WriteLayer(stagingDirectory, state, layers, "hydrology.flow_direction", "flow_direction.tif",
                        state.Hydrology.FlowDirection, TerrainTiffSampleKind.UInt8, "255", "uint8");
                    WriteLayer(stagingDirectory, state, layers, "hydrology.flow_accumulation", "flow_accumulation.tif",
                        state.Hydrology.FlowAccumulation, TerrainTiffSampleKind.UInt32, "0", "uint32");
                    WriteLayer(stagingDirectory, state, layers, "hydrology.streams", "streams.tif",
                        state.Hydrology.StreamMask, TerrainTiffSampleKind.UInt8, "255", "uint8");
                    WriteLayer(stagingDirectory, state, layers, "hydrology.watersheds", "watersheds.tif",
                        state.Hydrology.Watershed, TerrainTiffSampleKind.UInt32, "0", "uint32");
                    WriteLayer(stagingDirectory, state, layers, "hydrology.stream_order", "stream_order.tif",
                        state.Hydrology.StreamOrder, TerrainTiffSampleKind.UInt8, "255", "uint8");
                }

                if (state.WaterDynamics?.ManagedMask?.Length == state.CellCount)
                {
                    WriteLayer(
                        stagingDirectory,
                        state,
                        layers,
                        "hydrology.water_dynamics.managed_mask",
                        "managed_water.tif",
                        state.WaterDynamics.ManagedMask,
                        TerrainTiffSampleKind.UInt8,
                        "255",
                        "uint8");
                    if (state.WaterDynamics.WaterStorage?.Length == state.CellCount)
                    {
                        WriteLayer(
                            stagingDirectory,
                            state,
                            layers,
                            "hydrology.water_dynamics.water_storage",
                            "water_storage.tif",
                            state.WaterDynamics.WaterStorage,
                            TerrainTiffSampleKind.UInt8,
                            "0",
                            "uint8");
                    }

                    WriteOptionalWaterLayer(
                        stagingDirectory,
                        state,
                        layers,
                        "hydrology.water_dynamics.hydro_feature",
                        "hydro_feature.tif",
                        state.WaterDynamics.HydroFeature,
                        "255");
                    WriteOptionalWaterLayer(
                        stagingDirectory,
                        state,
                        layers,
                        "hydrology.water_dynamics.moisture",
                        "moisture.tif",
                        state.WaterDynamics.Moisture,
                        string.Empty);
                    WriteOptionalWaterLayer(
                        stagingDirectory,
                        state,
                        layers,
                        "hydrology.water_dynamics.erodibility",
                        "erodibility.tif",
                        state.WaterDynamics.Erodibility,
                        "255");
                    WriteOptionalWaterLayer(
                        stagingDirectory,
                        state,
                        layers,
                        "hydrology.water_dynamics.local_slope",
                        "local_slope.tif",
                        state.WaterDynamics.LocalSlope,
                        "255");
                    WriteOptionalWaterLayer(
                        stagingDirectory,
                        state,
                        layers,
                        "hydrology.water_dynamics.local_aspect",
                        "local_aspect.tif",
                        state.WaterDynamics.LocalAspect,
                        "255");
                }

                if (state.Erosion != null && state.Erosion.IsCurrent(state))
                {
                    RequireSourceHash(
                        "Erosion",
                        TerrainErosionAnalyzer.ComputeSourceSha256(
                            state.Elevation,
                            state.Landform),
                        state.Erosion.SourceSha256);
                    WriteLayer(stagingDirectory, state, layers, "erosion.result_elevation", "erosion_result.tif",
                        state.Erosion.ResultElevation, TerrainTiffSampleKind.Int16, "9999", "int16");
                    WriteLayer(stagingDirectory, state, layers, "erosion.net_change", "erosion_net_change.tif",
                        state.Erosion.NetChange, TerrainTiffSampleKind.Int32,
                        int.MinValue.ToString(CultureInfo.InvariantCulture), "int32");
                }

                TerrainGisManifest manifest = new TerrainGisManifest
                {
                    Format = "terrainlab-gis",
                    SchemaVersion = "1.2.0",
                    ProjectId = state.ProjectId,
                    SourceRevision = state.Revision,
                    SourceElevationSha256 = elevationHash,
                    CreatedUtc = DateTime.UtcNow,
                    Width = state.Width,
                    Height = state.Height,
                    HorizontalMetresPerCell = state.HorizontalMetresPerCell,
                    CrsWkt = state.Georeference != null
                        ? state.Georeference.CrsWkt
                        : TerrainGeoTiff.GetCrsWkt(state.ProjectId),
                    LocalCrsWkt = TerrainGeoTiff.GetCrsWkt(state.ProjectId),
                    Georeference = state.Georeference,
                    VerticalUnit = "metre",
                    SeaLevel = state.SeaLevel,
                    Layers = layers
                };
                File.WriteAllText(
                    Path.Combine(stagingDirectory, "terrainlab-gis.json"),
                    JsonConvert.SerializeObject(manifest, Formatting.Indented),
                    new UTF8Encoding(false));
                Directory.Move(stagingDirectory, directory);
                return directory;
            }
            catch
            {
                if (Directory.Exists(stagingDirectory))
                {
                    try
                    {
                        Directory.Delete(stagingDirectory, true);
                    }
                    catch
                    {
                    }
                }

                throw;
            }
        }

        private static void WriteLayer(
            string directory,
            TerrainWorldState state,
            ICollection<TerrainGisLayerManifest> layers,
            string id,
            string fileName,
            Array values,
            TerrainTiffSampleKind kind,
            string noData,
            string dataType)
        {
            string path = Path.Combine(directory, fileName);
            TerrainGeoTiff.Write(
                path,
                state.Width,
                state.Height,
                values,
                kind,
                noData,
                state.ProjectId,
                id,
                state.HorizontalMetresPerCell,
                state.Georeference);
            layers.Add(new TerrainGisLayerManifest
            {
                Id = id,
                File = fileName,
                DataType = dataType,
                NoData = noData,
                Sha256 = ComputeFileSha256(path)
            });
        }

        private static void WriteOptionalWaterLayer(
            string directory,
            TerrainWorldState state,
            ICollection<TerrainGisLayerManifest> layers,
            string id,
            string fileName,
            byte[] values,
            string noData)
        {
            if (values?.Length != state.CellCount)
            {
                return;
            }

            WriteLayer(
                directory,
                state,
                layers,
                id,
                fileName,
                values,
                TerrainTiffSampleKind.UInt8,
                noData,
                "uint8");
        }

        internal static string ComputeFileSha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(stream);
                StringBuilder builder = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash)
                {
                    builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }

        private static void RequireSourceHash(
            string layerGroup,
            string currentHash,
            string resultHash)
        {
            if (!string.Equals(currentHash, resultHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    layerGroup + " source checksum is stale.");
            }
        }
    }
}
