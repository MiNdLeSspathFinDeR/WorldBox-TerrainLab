using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace TerrainLab
{
    public enum TerrainSyncConflictPolicy
    {
        Reject,
        PreferWorld,
        PreferIncoming,
        BranchAndApplyIncoming
    }

    public enum TerrainSyncOutcome
    {
        Prepared,
        NoIncoming,
        NoChanges,
        Conflict,
        WorldKept,
        Applied
    }

    public sealed class TerrainSyncBaseline
    {
        [JsonProperty("format")]
        public string Format { get; set; }

        [JsonProperty("schema_version")]
        public string SchemaVersion { get; set; }

        [JsonProperty("project_id")]
        public string ProjectId { get; set; }

        [JsonProperty("source_revision")]
        public long SourceRevision { get; set; }

        [JsonProperty("elevation_sha256")]
        public string ElevationSha256 { get; set; }

        [JsonProperty("exported_utc")]
        public DateTime ExportedUtc { get; set; }

        [JsonProperty("width")]
        public int Width { get; set; }

        [JsonProperty("height")]
        public int Height { get; set; }

        [JsonProperty("data_type")]
        public string DataType { get; set; }

        [JsonProperty("nodata")]
        public int NoData { get; set; }

        internal static TerrainSyncBaseline FromState(
            TerrainWorldState state,
            string elevationSha256)
        {
            return new TerrainSyncBaseline
            {
                Format = TerrainFileSync.FormatId,
                SchemaVersion = TerrainFileSync.SchemaVersion,
                ProjectId = state.ProjectId,
                SourceRevision = state.Revision,
                ElevationSha256 = elevationSha256,
                ExportedUtc = DateTime.UtcNow,
                Width = state.Width,
                Height = state.Height,
                DataType = "int16",
                NoData = TerrainElevationEncoding.NoData
            };
        }
    }

    public sealed class TerrainSyncChangeRecord
    {
        [JsonProperty("changed_utc")]
        public DateTime ChangedUtc { get; set; }

        [JsonProperty("project_id")]
        public string ProjectId { get; set; }

        [JsonProperty("origin")]
        public string Origin { get; set; }

        [JsonProperty("policy")]
        public string Policy { get; set; }

        [JsonProperty("outcome")]
        public string Outcome { get; set; }

        [JsonProperty("baseline_sha256")]
        public string BaselineSha256 { get; set; }

        [JsonProperty("before_sha256")]
        public string BeforeSha256 { get; set; }

        [JsonProperty("incoming_sha256")]
        public string IncomingSha256 { get; set; }

        [JsonProperty("after_sha256")]
        public string AfterSha256 { get; set; }

        [JsonProperty("changed_cells")]
        public int ChangedCells { get; set; }

        [JsonProperty("conflict_detected")]
        public bool ConflictDetected { get; set; }

        [JsonProperty("archived_incoming", NullValueHandling = NullValueHandling.Ignore)]
        public string ArchivedIncoming { get; set; }

        [JsonProperty("branch", NullValueHandling = NullValueHandling.Ignore)]
        public string Branch { get; set; }
    }

    public sealed class TerrainSyncResult
    {
        internal TerrainSyncResult(
            TerrainSyncOutcome outcome,
            string workspaceDirectory,
            bool conflictDetected,
            string baselineSha256,
            string beforeSha256,
            string incomingSha256,
            string afterSha256,
            int changedCells,
            TerrainElevationEdit edit,
            string branchPath)
        {
            Outcome = outcome;
            WorkspaceDirectory = workspaceDirectory;
            ConflictDetected = conflictDetected;
            BaselineSha256 = baselineSha256;
            BeforeSha256 = beforeSha256;
            IncomingSha256 = incomingSha256;
            AfterSha256 = afterSha256;
            ChangedCells = changedCells;
            Edit = edit;
            BranchPath = branchPath;
        }

        public TerrainSyncOutcome Outcome { get; }

        public string WorkspaceDirectory { get; }

        public bool ConflictDetected { get; }

        public bool Applied => Outcome == TerrainSyncOutcome.Applied;

        public string BaselineSha256 { get; }

        public string BeforeSha256 { get; }

        public string IncomingSha256 { get; }

        public string AfterSha256 { get; }

        public int ChangedCells { get; }

        public TerrainElevationEdit Edit { get; }

        public string BranchPath { get; }
    }

    public static class TerrainFileSync
    {
        public const string FormatId = "terrainlab-file-sync";
        public const string SchemaVersion = "1.0.0";

        private const string BaselineFileName = "baseline.json";
        private const string ChangeLogFileName = "changes.jsonl";
        private const string ElevationFileName = "elevation.tif";

        public static string GetWorkspaceDirectory(
            string exchangeDirectory,
            TerrainWorldState state)
        {
            if (string.IsNullOrWhiteSpace(exchangeDirectory))
            {
                throw new ArgumentException("Exchange directory is required.", nameof(exchangeDirectory));
            }

            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            string token = new string((state.ProjectId ?? "project")
                .Where(character => char.IsLetterOrDigit(character) ||
                                    character == '-' || character == '_')
                .ToArray());
            if (string.IsNullOrWhiteSpace(token))
            {
                token = "project";
            }

            return Path.Combine(
                Path.GetFullPath(exchangeDirectory),
                "Sync",
                token);
        }

        public static TerrainSyncResult PrepareWorkspace(
            string exchangeDirectory,
            TerrainWorldState state)
        {
            ValidateState(state);
            string workspace = GetWorkspaceDirectory(exchangeDirectory, state);
            EnsureWorkspaceDirectories(workspace);
            string incomingPath = GetIncomingPath(workspace);
            if (File.Exists(incomingPath))
            {
                throw new InvalidOperationException(
                    "Incoming elevation.tif must be pulled or removed before refreshing the baseline.");
            }

            WriteSnapshot(workspace, state);
            WriteProtocolReadme(workspace);
            string hash = ComputeElevationSha256(state.Elevation);
            return new TerrainSyncResult(
                TerrainSyncOutcome.Prepared,
                workspace,
                false,
                hash,
                hash,
                null,
                hash,
                0,
                null,
                null);
        }

        public static TerrainSyncResult Pull(
            string exchangeDirectory,
            TerrainWorldState state,
            TerrainSyncConflictPolicy policy)
        {
            ValidateState(state);
            string workspace = GetWorkspaceDirectory(exchangeDirectory, state);
            EnsureWorkspaceDirectories(workspace);
            TerrainSyncBaseline baseline = ReadAndValidateBaseline(workspace, state);
            string incomingPath = GetIncomingPath(workspace);
            string beforeHash = ComputeElevationSha256(state.Elevation);
            bool conflict = !HashesEqual(beforeHash, baseline.ElevationSha256);
            if (!File.Exists(incomingPath))
            {
                return new TerrainSyncResult(
                    TerrainSyncOutcome.NoIncoming,
                    workspace,
                    conflict,
                    baseline.ElevationSha256,
                    beforeHash,
                    null,
                    beforeHash,
                    0,
                    null,
                    null);
            }

            string stagingPath = Path.Combine(
                workspace,
                ".pull-" + Guid.NewGuid().ToString("N") + ".tif");
            try
            {
                FileInfo beforeCopy = new FileInfo(incomingPath);
                long sourceLength = beforeCopy.Length;
                DateTime sourceWriteTime = beforeCopy.LastWriteTimeUtc;
                File.Copy(incomingPath, stagingPath, false);
                FileInfo afterCopy = new FileInfo(incomingPath);
                if (afterCopy.Length != sourceLength ||
                    afterCopy.LastWriteTimeUtc != sourceWriteTime)
                {
                    throw new IOException(
                        "Incoming elevation.tif changed while it was being staged; retry after the writer finishes.");
                }

                short[] incoming = TerrainGeoTiff.ReadInt16(
                    stagingPath,
                    state.Width,
                    state.Height);
                string incomingHash = ComputeElevationSha256(incoming);
                if (conflict && (policy == TerrainSyncConflictPolicy.Reject ||
                                 policy == TerrainSyncConflictPolicy.PreferWorld))
                {
                    TerrainSyncOutcome outcome = policy == TerrainSyncConflictPolicy.Reject
                        ? TerrainSyncOutcome.Conflict
                        : TerrainSyncOutcome.WorldKept;
                    AppendChangeLog(
                        workspace,
                        CreateChangeRecord(
                            state,
                            policy,
                            outcome,
                            baseline.ElevationSha256,
                            beforeHash,
                            incomingHash,
                            beforeHash,
                            0,
                            true,
                            null,
                            null));
                    return new TerrainSyncResult(
                        outcome,
                        workspace,
                        true,
                        baseline.ElevationSha256,
                        beforeHash,
                        incomingHash,
                        beforeHash,
                        0,
                        null,
                        null);
                }

                if (HashesEqual(beforeHash, incomingHash))
                {
                    string archived = null;
                    try
                    {
                        archived = ArchiveIncoming(workspace, incomingPath);
                        WriteSnapshot(workspace, state);
                        AppendChangeLog(
                            workspace,
                            CreateChangeRecord(
                                state,
                                policy,
                                TerrainSyncOutcome.NoChanges,
                                baseline.ElevationSha256,
                                beforeHash,
                                incomingHash,
                                beforeHash,
                                0,
                                conflict,
                                archived,
                                null));
                        return new TerrainSyncResult(
                            TerrainSyncOutcome.NoChanges,
                            workspace,
                            conflict,
                            baseline.ElevationSha256,
                            beforeHash,
                            incomingHash,
                            beforeHash,
                            0,
                            null,
                            null);
                    }
                    catch
                    {
                        RestoreArchivedIncoming(workspace, incomingPath, archived);
                        throw;
                    }
                }

                string branchPath = null;
                if (conflict && policy == TerrainSyncConflictPolicy.BranchAndApplyIncoming)
                {
                    branchPath = WriteBranch(workspace, state, beforeHash);
                }

                TerrainElevationEdit edit = null;
                string archivedIncoming = null;
                try
                {
                    edit = state.ApplyElevationGrid(incoming);
                    string afterHash = ComputeElevationSha256(state.Elevation);
                    archivedIncoming = ArchiveIncoming(workspace, incomingPath);
                    WriteSnapshot(workspace, state);
                    AppendChangeLog(
                        workspace,
                        CreateChangeRecord(
                            state,
                            policy,
                            TerrainSyncOutcome.Applied,
                            baseline.ElevationSha256,
                            beforeHash,
                            incomingHash,
                            afterHash,
                            edit.ChangedCellCount,
                            conflict,
                            archivedIncoming,
                            branchPath));
                    return new TerrainSyncResult(
                        TerrainSyncOutcome.Applied,
                        workspace,
                        conflict,
                        baseline.ElevationSha256,
                        beforeHash,
                        incomingHash,
                        afterHash,
                        edit.ChangedCellCount,
                        edit,
                        branchPath);
                }
                catch
                {
                    if (edit != null && edit.ChangedCellCount > 0)
                    {
                        state.ApplyElevationEdit(edit, false);
                    }

                    RestoreArchivedIncoming(workspace, incomingPath, archivedIncoming);
                    try
                    {
                        WriteSnapshot(workspace, state);
                    }
                    catch
                    {
                        // Preserve the original sync failure; the next Prepare repairs output.
                    }

                    throw;
                }
            }
            finally
            {
                if (File.Exists(stagingPath))
                {
                    try
                    {
                        File.Delete(stagingPath);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static void ValidateState(TerrainWorldState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (!TerrainMapLimits.TryValidate(state.Width, state.Height, out string error) ||
                state.Elevation == null || state.Elevation.Length != state.Width * state.Height)
            {
                throw new InvalidOperationException(
                    error ?? "TerrainLab state has inconsistent elevation dimensions.");
            }
        }

        private static void EnsureWorkspaceDirectories(string workspace)
        {
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(Path.Combine(workspace, "outgoing"));
            Directory.CreateDirectory(Path.Combine(workspace, "incoming"));
            Directory.CreateDirectory(Path.Combine(workspace, "history"));
            Directory.CreateDirectory(Path.Combine(workspace, "branches"));
        }

        private static void WriteSnapshot(string workspace, TerrainWorldState state)
        {
            string hash = ComputeElevationSha256(state.Elevation);
            string outgoingPath = Path.Combine(workspace, "outgoing", ElevationFileName);
            TerrainGeoTiff.Write(
                outgoingPath,
                state.Width,
                state.Height,
                state.Elevation,
                TerrainTiffSampleKind.Int16,
                TerrainElevationEncoding.NoData.ToString(CultureInfo.InvariantCulture),
                state.ProjectId,
                "core.elevation");
            WriteJsonAtomic(
                Path.Combine(workspace, BaselineFileName),
                TerrainSyncBaseline.FromState(state, hash));
        }

        private static TerrainSyncBaseline ReadAndValidateBaseline(
            string workspace,
            TerrainWorldState state)
        {
            string path = Path.Combine(workspace, BaselineFileName);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "Sync baseline is missing; prepare the workspace first.",
                    path);
            }

            if (new FileInfo(path).Length > 1024 * 1024)
            {
                throw new InvalidDataException("Sync baseline is unexpectedly large.");
            }

            TerrainSyncBaseline baseline;
            try
            {
                baseline = JsonConvert.DeserializeObject<TerrainSyncBaseline>(
                    File.ReadAllText(path, Encoding.UTF8));
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Sync baseline JSON is invalid.", exception);
            }

            if (baseline == null || baseline.Format != FormatId ||
                baseline.SchemaVersion != SchemaVersion ||
                baseline.ProjectId != state.ProjectId ||
                baseline.Width != state.Width || baseline.Height != state.Height ||
                baseline.DataType != "int16" ||
                baseline.NoData != TerrainElevationEncoding.NoData ||
                !IsSha256(baseline.ElevationSha256))
            {
                throw new InvalidDataException(
                    "Sync baseline does not match the active TerrainLab project.");
            }

            return baseline;
        }

        private static string WriteBranch(
            string workspace,
            TerrainWorldState state,
            string elevationSha256)
        {
            string stem = CreateTimestampStem("world");
            string path = CreateUniquePath(
                Path.Combine(workspace, "branches"),
                stem,
                ".tif");
            TerrainGeoTiff.Write(
                path,
                state.Width,
                state.Height,
                state.Elevation,
                TerrainTiffSampleKind.Int16,
                TerrainElevationEncoding.NoData.ToString(CultureInfo.InvariantCulture),
                state.ProjectId,
                "core.elevation.branch");
            WriteJsonAtomic(
                Path.ChangeExtension(path, ".json"),
                TerrainSyncBaseline.FromState(state, elevationSha256));
            return MakeRelative(workspace, path);
        }

        private static string ArchiveIncoming(string workspace, string incomingPath)
        {
            string history = Path.Combine(workspace, "history");
            string archivePath = CreateUniquePath(
                history,
                CreateTimestampStem("incoming"),
                ".tif");
            MoveFileWithSidecars(incomingPath, archivePath);
            return MakeRelative(workspace, archivePath);
        }

        private static void MoveFileWithSidecars(string sourceTiff, string destinationTiff)
        {
            List<Tuple<string, string>> moved = new List<Tuple<string, string>>();
            try
            {
                File.Move(sourceTiff, destinationTiff);
                moved.Add(Tuple.Create(sourceTiff, destinationTiff));
                foreach (string extension in new[] { ".tfw", ".prj" })
                {
                    string source = Path.ChangeExtension(sourceTiff, extension);
                    if (!File.Exists(source))
                    {
                        continue;
                    }

                    string destination = Path.ChangeExtension(destinationTiff, extension);
                    File.Move(source, destination);
                    moved.Add(Tuple.Create(source, destination));
                }
            }
            catch
            {
                for (int index = moved.Count - 1; index >= 0; index--)
                {
                    Tuple<string, string> pair = moved[index];
                    if (File.Exists(pair.Item2) && !File.Exists(pair.Item1))
                    {
                        File.Move(pair.Item2, pair.Item1);
                    }
                }

                throw;
            }
        }

        private static void RestoreArchivedIncoming(
            string workspace,
            string incomingPath,
            string archivedRelativePath)
        {
            if (string.IsNullOrWhiteSpace(archivedRelativePath) || File.Exists(incomingPath))
            {
                return;
            }

            string archivePath = Path.Combine(workspace, archivedRelativePath);
            if (File.Exists(archivePath))
            {
                MoveFileWithSidecars(archivePath, incomingPath);
            }
        }

        private static TerrainSyncChangeRecord CreateChangeRecord(
            TerrainWorldState state,
            TerrainSyncConflictPolicy policy,
            TerrainSyncOutcome outcome,
            string baselineHash,
            string beforeHash,
            string incomingHash,
            string afterHash,
            int changedCells,
            bool conflict,
            string archivePath,
            string branchPath)
        {
            return new TerrainSyncChangeRecord
            {
                ChangedUtc = DateTime.UtcNow,
                ProjectId = state.ProjectId,
                Origin = "external-gis",
                Policy = ToToken(policy),
                Outcome = ToToken(outcome),
                BaselineSha256 = baselineHash,
                BeforeSha256 = beforeHash,
                IncomingSha256 = incomingHash,
                AfterSha256 = afterHash,
                ChangedCells = changedCells,
                ConflictDetected = conflict,
                ArchivedIncoming = archivePath,
                Branch = branchPath
            };
        }

        private static void AppendChangeLog(
            string workspace,
            TerrainSyncChangeRecord record)
        {
            string line = JsonConvert.SerializeObject(record, Formatting.None) +
                          Environment.NewLine;
            File.AppendAllText(
                Path.Combine(workspace, ChangeLogFileName),
                line,
                new UTF8Encoding(false));
        }

        private static void WriteJsonAtomic(string path, object value)
        {
            string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(
                    temporaryPath,
                    JsonConvert.SerializeObject(value, Formatting.Indented),
                    new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    File.Replace(temporaryPath, path, null);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        private static void WriteProtocolReadme(string workspace)
        {
            string path = Path.Combine(workspace, "README.txt");
            if (File.Exists(path))
            {
                return;
            }

            File.WriteAllText(
                path,
                "TerrainLab file sync 1.0\r\n" +
                "1. Open outgoing/elevation.tif in a GIS editor.\r\n" +
                "2. Export the edited DEM atomically as incoming/elevation.tif.\r\n" +
                "3. In TerrainLab, pull with conflict checking.\r\n" +
                "The raster must remain signed Int16, use NODATA 9999, and keep its dimensions.\r\n",
                new UTF8Encoding(false));
        }

        private static string GetIncomingPath(string workspace)
        {
            return Path.Combine(workspace, "incoming", ElevationFileName);
        }

        private static string ComputeElevationSha256(short[] elevation)
        {
            return TerrainReliefAnalyzer.ComputeSourceSha256(elevation);
        }

        private static bool HashesEqual(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSha256(string value)
        {
            return value != null && value.Length == 64 &&
                   value.All(character =>
                       character >= '0' && character <= '9' ||
                       character >= 'a' && character <= 'f' ||
                       character >= 'A' && character <= 'F');
        }

        private static string CreateTimestampStem(string prefix)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}-{1:yyyyMMdd-HHmmssfff}",
                prefix,
                DateTime.UtcNow);
        }

        private static string CreateUniquePath(
            string directory,
            string stem,
            string extension)
        {
            string path = Path.Combine(directory, stem + extension);
            int suffix = 2;
            while (File.Exists(path))
            {
                path = Path.Combine(directory, stem + "-" + suffix++ + extension);
            }

            return path;
        }

        private static string MakeRelative(string root, string path)
        {
            Uri rootUri = new Uri(
                Path.GetFullPath(root).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar);
            Uri pathUri = new Uri(Path.GetFullPath(path));
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString())
                .Replace('/', Path.DirectorySeparatorChar);
        }

        private static string ToToken(TerrainSyncConflictPolicy policy)
        {
            switch (policy)
            {
                case TerrainSyncConflictPolicy.PreferWorld:
                    return "prefer_world";
                case TerrainSyncConflictPolicy.PreferIncoming:
                    return "prefer_incoming";
                case TerrainSyncConflictPolicy.BranchAndApplyIncoming:
                    return "branch_and_apply_incoming";
                default:
                    return "reject";
            }
        }

        private static string ToToken(TerrainSyncOutcome outcome)
        {
            switch (outcome)
            {
                case TerrainSyncOutcome.Prepared:
                    return "prepared";
                case TerrainSyncOutcome.NoIncoming:
                    return "no_incoming";
                case TerrainSyncOutcome.NoChanges:
                    return "no_changes";
                case TerrainSyncOutcome.Conflict:
                    return "conflict";
                case TerrainSyncOutcome.WorldKept:
                    return "world_kept";
                default:
                    return "applied";
            }
        }
    }
}
