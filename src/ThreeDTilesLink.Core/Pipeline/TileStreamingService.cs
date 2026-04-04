using System.Net;
using System.Numerics;
using Microsoft.Extensions.Logging;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Tiles;
using SMath = System.Math;

namespace ThreeDTilesLink.Core.Pipeline
{
    public sealed class TileStreamingService(
        ITileContentFetcher fetcher,
        ITileStreamingScheduler scheduler,
        IGlbMeshExtractor glbMeshExtractor,
        ICoordinateTransformer coordinateTransformer,
        IResoniteLinkClient resoniteLinkClient,
        IGoogleAccessTokenProvider googleAccessTokenProvider,
        ILogger<TileStreamingService> logger)
    {
        // 3D Tiles glTF content is Y-up; convert to tiles/world Z-up before tile transform application.
        private static readonly Matrix4x4d GltfYUpToZUp = new(
            1d, 0d, 0d, 0d,
            0d, 0d, 1d, 0d,
            0d, -1d, 0d, 0d,
            0d, 0d, 0d, 1d);

        private readonly ITileContentFetcher _fetcher = fetcher;
        private readonly ITileStreamingScheduler _scheduler = scheduler;
        private readonly IGlbMeshExtractor _glbMeshExtractor = glbMeshExtractor;
        private readonly ICoordinateTransformer _coordinateTransformer = coordinateTransformer;
        private readonly IResoniteLinkClient _resoniteLinkClient = resoniteLinkClient;
        private readonly IGoogleAccessTokenProvider _googleAccessTokenProvider = googleAccessTokenProvider;
        private readonly ILogger<TileStreamingService> _logger = logger;

        public async Task<RunSummary> RunAsync(StreamerOptions options, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);
            GoogleTilesAuth auth = await BuildAuthAsync(options, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Fetching root tileset from Google Map Tiles API.");
            Tileset rootTileset = await _fetcher.FetchRootTilesetAsync(auth, cancellationToken).ConfigureAwait(false);
            _scheduler.Initialize(rootTileset, options);

            if (!options.DryRun && options.ManageResoniteConnection)
            {
                _logger.LogInformation("Connecting to Resonite Link at {Host}:{Port}", options.LinkHost, options.LinkPort);
                await _resoniteLinkClient.ConnectAsync(options.LinkHost, options.LinkPort, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                while (_scheduler.TryDequeueWorkItem(out SchedulerWorkItem? workItem) && workItem is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SchedulerWorkResult result = await ExecuteWorkItemAsync(workItem, options, auth, cancellationToken).ConfigureAwait(false);
                    _scheduler.HandleResult(result);
                }
            }
            finally
            {
                if (!options.DryRun && options.ManageResoniteConnection)
                {
                    await _resoniteLinkClient.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            return _scheduler.GetSummary();
        }

        private async Task<SchedulerWorkResult> ExecuteWorkItemAsync(
            SchedulerWorkItem workItem,
            StreamerOptions options,
            GoogleTilesAuth auth,
            CancellationToken cancellationToken)
        {
            return workItem switch
            {
                FetchNestedTilesetWorkItem fetchWork => await ExecuteFetchNestedTilesetAsync(fetchWork, auth, cancellationToken).ConfigureAwait(false),
                StreamGlbTileWorkItem streamWork => await ExecuteStreamGlbTileAsync(streamWork, options, auth, cancellationToken).ConfigureAwait(false),
                RemoveParentTileSlotsWorkItem removeWork => await ExecuteRemoveParentTileSlotsAsync(removeWork, cancellationToken).ConfigureAwait(false),
                UpdateLicenseCreditWorkItem updateWork => await ExecuteUpdateLicenseCreditAsync(updateWork, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unsupported scheduler work item type: {workItem.GetType().Name}")
            };
        }

        private async Task<FetchNestedTilesetWorkResult> ExecuteFetchNestedTilesetAsync(
            FetchNestedTilesetWorkItem workItem,
            GoogleTilesAuth auth,
            CancellationToken cancellationToken)
        {
            try
            {
                Tileset nestedTileset = await _fetcher
                    .FetchTilesetAsync(workItem.Tile.ContentUri, auth, cancellationToken)
                    .ConfigureAwait(false);
                return new FetchNestedTilesetWorkResult(workItem.Tile, true, nestedTileset, false, null);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
            {
                return new FetchNestedTilesetWorkResult(workItem.Tile, false, null, true, ex);
            }
            catch (Exception ex)
            {
                return new FetchNestedTilesetWorkResult(workItem.Tile, false, null, false, ex);
            }
        }

        private async Task<StreamGlbTileWorkResult> ExecuteStreamGlbTileAsync(
            StreamGlbTileWorkItem workItem,
            StreamerOptions options,
            GoogleTilesAuth auth,
            CancellationToken cancellationToken)
        {
            var streamedSlotIds = new List<string>();
            int streamedMeshCount = 0;
            string? assetCopyright = null;

            try
            {
                byte[] glb = await _fetcher.FetchTileContentAsync(workItem.Tile.ContentUri, auth, cancellationToken).ConfigureAwait(false);
                GlbExtractResult extracted = _glbMeshExtractor.Extract(glb);
                assetCopyright = extracted.AssetCopyright;
                IReadOnlyList<MeshData> meshes = extracted.Meshes;

                foreach (MeshData mesh in meshes)
                {
                    TileMeshPayload payload = ToEunPayload(
                        mesh,
                        workItem.Tile.WorldTransform,
                        options.Reference,
                        workItem.Tile.TileId,
                        options.MeshParentSlotId);
                    if (!options.DryRun)
                    {
                        string? slotId = await _resoniteLinkClient.SendTileMeshAsync(payload, cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(slotId))
                        {
                            streamedSlotIds.Add(slotId);
                        }
                    }

                    streamedMeshCount++;
                }

                return new StreamGlbTileWorkResult(
                    workItem.Tile,
                    StreamGlbOutcome.Success,
                    streamedMeshCount,
                    streamedSlotIds,
                    assetCopyright,
                    null);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
            {
                return new StreamGlbTileWorkResult(
                    workItem.Tile,
                    StreamGlbOutcome.BadRequest,
                    streamedMeshCount,
                    streamedSlotIds,
                    assetCopyright,
                    ex);
            }
            catch (Exception ex)
            {
                return new StreamGlbTileWorkResult(
                    workItem.Tile,
                    StreamGlbOutcome.Failed,
                    streamedMeshCount,
                    streamedSlotIds,
                    assetCopyright,
                    ex);
            }
        }

        private async Task<RemoveParentTileSlotsWorkResult> ExecuteRemoveParentTileSlotsAsync(
            RemoveParentTileSlotsWorkItem workItem,
            CancellationToken cancellationToken)
        {
            int failedSlotCount = 0;
            Exception? firstError = null;

            foreach (string slotId in workItem.SlotIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await _resoniteLinkClient.RemoveSlotAsync(slotId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failedSlotCount++;
                    firstError ??= ex;
                    _logger.LogWarning(ex, "Failed to remove parent tile slot {SlotId} for tile {TileId}.", slotId, workItem.TileId);
                }
            }

            return new RemoveParentTileSlotsWorkResult(workItem.StateId, workItem.TileId, failedSlotCount == 0, failedSlotCount, firstError);
        }

        private async Task<UpdateLicenseCreditWorkResult> ExecuteUpdateLicenseCreditAsync(
            UpdateLicenseCreditWorkItem workItem,
            CancellationToken cancellationToken)
        {
            try
            {
                await _resoniteLinkClient
                    .SetSessionLicenseCreditAsync(workItem.CreditString, cancellationToken)
                    .ConfigureAwait(false);
                return new UpdateLicenseCreditWorkResult(workItem.CreditString, true, null);
            }
            catch (Exception ex)
            {
                return new UpdateLicenseCreditWorkResult(workItem.CreditString, false, ex);
            }
        }

        private TileMeshPayload ToEunPayload(
            MeshData mesh,
            Matrix4x4d tileWorld,
            GeoReference reference,
            string tileId,
            string? parentSlotId)
        {
            // 3D Tiles/glTF transform chain:
            // glTF node local (Y-up) -> tiles/world frame (Z-up) -> tile world transform.
            Matrix4x4d meshWorld = mesh.LocalTransform * GltfYUpToZUp * tileWorld;
            Vector3d meshOriginEcef = meshWorld.TransformPoint(new Vector3d(0d, 0d, 0d));
            Vector3d meshOriginEun = ToEun(meshOriginEcef, reference);

            Vector3d basisXEun = ToEun(meshWorld.TransformPoint(new Vector3d(1d, 0d, 0d)), reference) - meshOriginEun;
            Vector3d basisYEun = ToEun(meshWorld.TransformPoint(new Vector3d(0d, 1d, 0d)), reference) - meshOriginEun;
            Vector3d basisZEun = ToEun(meshWorld.TransformPoint(new Vector3d(0d, 0d, 1d)), reference) - meshOriginEun;
            (Quaternion slotRotation, Vector3 slotScale) = BuildSlotFrame(basisXEun, basisYEun, basisZEun);
            Quaternion invRotation = Quaternion.Inverse(slotRotation);

            var worldVertices = new List<Vector3d>(mesh.Vertices.Count);
            var min = new Vector3d(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
            var max = new Vector3d(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
            foreach (Vector3d vertex in mesh.Vertices)
            {
                Vector3d worldEcef = meshWorld.TransformPoint(vertex);
                Vector3d worldEun = ToEun(worldEcef, reference);
                worldVertices.Add(worldEun);

                min = new Vector3d(
                    SMath.Min(min.X, worldEun.X),
                    SMath.Min(min.Y, worldEun.Y),
                    SMath.Min(min.Z, worldEun.Z));
                max = new Vector3d(
                    SMath.Max(max.X, worldEun.X),
                    SMath.Max(max.Y, worldEun.Y),
                    SMath.Max(max.Z, worldEun.Z));
            }

            // Use mesh center as slot origin to keep vertex values compact and improve float precision.
            var slotOriginEun = new Vector3d(
                (min.X + max.X) * 0.5d,
                (min.Y + max.Y) * 0.5d,
                (min.Z + max.Z) * 0.5d);

            var vertices = new List<Vector3>(worldVertices.Count);
            var uvs = new List<Vector2>(mesh.Uvs.Count);
            float maxAbsVertex = 0f;

            foreach (Vector3d worldEun in worldVertices)
            {
                Vector3d delta = worldEun - slotOriginEun;

                Vector3 localRotated = Vector3.Transform(
                    new Vector3((float)delta.X, (float)delta.Y, (float)delta.Z),
                    invRotation);

                var local = new Vector3(
                    slotScale.X > 1e-6f ? localRotated.X / slotScale.X : localRotated.X,
                    slotScale.Y > 1e-6f ? localRotated.Y / slotScale.Y : localRotated.Y,
                    slotScale.Z > 1e-6f ? localRotated.Z / slotScale.Z : localRotated.Z);
                vertices.Add(local);
                maxAbsVertex = SMath.Max(maxAbsVertex, SMath.Max(SMath.Abs(local.X), SMath.Max(SMath.Abs(local.Y), SMath.Abs(local.Z))));
            }

            foreach (Vector2d uv in mesh.Uvs)
            {
                uvs.Add(new Vector2((float)uv.X, (float)uv.Y));
            }

            _logger.LogDebug(
                "Tile {TileId} mesh {MeshName}: slotPos=({PosX:F2},{PosY:F2},{PosZ:F2}) scale=({ScaleX:F4},{ScaleY:F4},{ScaleZ:F4}) localMaxAbs={LocalMaxAbs:F2}m",
                tileId,
                mesh.Name,
                slotOriginEun.X,
                slotOriginEun.Y,
                slotOriginEun.Z,
                slotScale.X,
                slotScale.Y,
                slotScale.Z,
                maxAbsVertex);

            // ENU -> EUN axis swap flips handedness. Reverse winding to keep front faces.
            var eunIndices = new List<int>(mesh.Indices.Count);
            for (int i = 0; i + 2 < mesh.Indices.Count; i += 3)
            {
                int a = mesh.Indices[i];
                int b = mesh.Indices[i + 1];
                int c = mesh.Indices[i + 2];
                eunIndices.Add(a);
                eunIndices.Add(c);
                eunIndices.Add(b);
            }

            return new TileMeshPayload(
                BuildMeshSlotName(tileId, mesh.Name),
                vertices,
                eunIndices,
                uvs,
                mesh.HasUv0,
                new Vector3((float)slotOriginEun.X, (float)slotOriginEun.Y, (float)slotOriginEun.Z),
                slotRotation,
                slotScale,
                mesh.BaseColorTextureBytes,
                mesh.BaseColorTextureExtension,
                parentSlotId);
        }

        private static string BuildMeshSlotName(string tileId, string meshName)
        {
            string compactTileId = tileId.Replace("/", string.Empty, StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(compactTileId))
            {
                compactTileId = "tile";
            }

            return $"tile_{compactTileId}_{meshName}";
        }

        private Vector3d ToEun(Vector3d ecef, GeoReference reference)
        {
            Vector3d enu = _coordinateTransformer.EcefToEnu(ecef, reference);
            return _coordinateTransformer.EnuToEun(enu);
        }

        private static (Quaternion Rotation, Vector3 Scale) BuildSlotFrame(Vector3d basisX, Vector3d basisY, Vector3d basisZ)
        {
            const double epsilon = 1e-9d;
            float sx = (float)SMath.Max(basisX.Length(), epsilon);
            float sy = (float)SMath.Max(basisY.Length(), epsilon);
            float sz = (float)SMath.Max(basisZ.Length(), epsilon);

            Vector3d x = NormalizeOrFallback(basisX, new Vector3d(1d, 0d, 0d));
            Vector3d yProjected = basisY - (Vector3d.Dot(basisY, x) * x);
            Vector3d y = NormalizeOrFallback(yProjected, new Vector3d(0d, 1d, 0d));
            Vector3d z = Vector3d.Normalize(Vector3d.Cross(x, y));

            if (z.Length() <= epsilon)
            {
                z = NormalizeOrFallback(basisZ, new Vector3d(0d, 0d, 1d));
                y = NormalizeOrFallback(Vector3d.Cross(z, x), new Vector3d(0d, 1d, 0d));
                z = NormalizeOrFallback(Vector3d.Cross(x, y), new Vector3d(0d, 0d, 1d));
            }

            if (Vector3d.Dot(z, basisZ) < 0d)
            {
                y = -1d * y;
                z = -1d * z;
            }

            var rotationMatrix = new Matrix4x4(
                (float)x.X, (float)x.Y, (float)x.Z, 0f,
                (float)y.X, (float)y.Y, (float)y.Z, 0f,
                (float)z.X, (float)z.Y, (float)z.Z, 0f,
                0f, 0f, 0f, 1f);

            Quaternion rotation = Quaternion.CreateFromRotationMatrix(rotationMatrix);
            rotation = !float.IsFinite(rotation.X) ||
                !float.IsFinite(rotation.Y) ||
                !float.IsFinite(rotation.Z) ||
                !float.IsFinite(rotation.W)
                ? Quaternion.Identity
                : Quaternion.Normalize(rotation);

            return (rotation, new Vector3(sx, sy, sz));
        }

        private static Vector3d NormalizeOrFallback(Vector3d value, Vector3d fallback)
        {
            Vector3d normalized = Vector3d.Normalize(value);
            return normalized.Length() <= 1e-9d ? fallback : normalized;
        }

        private async Task<GoogleTilesAuth> BuildAuthAsync(StreamerOptions options, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
            {
                return new GoogleTilesAuth(options.ApiKey, null);
            }

            string token = await _googleAccessTokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            return new GoogleTilesAuth(null, token);
        }
    }
}
