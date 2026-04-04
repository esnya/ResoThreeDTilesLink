using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ResoniteLink;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Math;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Tiles;
using SMath = System.Math;

namespace ThreeDTilesLink.Core.Pipeline
{
    public sealed class TileStreamingService
    {
        private const string SlotWorkerType = "[FrooxEngine]FrooxEngine.Slot";
        private const string StaticMeshComponentType = "[FrooxEngine]FrooxEngine.StaticMesh";
        private const string StaticTextureComponentType = "[FrooxEngine]FrooxEngine.StaticTexture2D";
        private const string MeshColliderComponentType = "[FrooxEngine]FrooxEngine.MeshCollider";
        private const string MaterialComponentType = "[FrooxEngine]FrooxEngine.PBS_Metallic";
        private const string MeshRendererComponentType = "[FrooxEngine]FrooxEngine.MeshRenderer";
        private const string LicenseComponentType = "[FrooxEngine]FrooxEngine.License";
        private const string SimpleAvatarProtectionUserMemberName = "User";
        private const string SimpleAvatarProtectionReassignMemberName = "ReassignUserOnPackageImport";
        private const string UserLoginStatusLoggedInMemberName = "IsLoggedIn";
        private const string UserLoginStatusUserIdMemberName = "LoggedUserId";
        private const string DynamicVariableSpaceComponentType = "[FrooxEngine]FrooxEngine.DynamicVariableSpace";
        private const string DynamicFieldStringComponentType = "[FrooxEngine]FrooxEngine.DynamicField<string>";
        private const string DynamicValueVariableFloatComponentType = "[FrooxEngine]FrooxEngine.DynamicValueVariable<float>";
        private const string DynamicValueVariableStringComponentType = "[FrooxEngine]FrooxEngine.DynamicValueVariable<string>";
        private const string DynamicValueVariableValueMemberName = "Value";
        private const string StringFieldType = "[FrooxEngine]FrooxEngine.IField<string>";
        private const string GoogleTilesDynamicSpaceName = "Google3DTiles";
        private const string LicenseDynamicVariablePath = "World/ThreeDTilesLink.License";
        private const string DefaultGoogleMapsCreditText = "Google Maps";
        private const string MeshAssetProviderType = "[FrooxEngine]FrooxEngine.IAssetProvider<[FrooxEngine]FrooxEngine.Mesh>";
        private const string MaterialAssetProviderType = "[FrooxEngine]FrooxEngine.IAssetProvider<[FrooxEngine]FrooxEngine.Material>";
        private const string TextureAssetProviderType = "[FrooxEngine]FrooxEngine.IAssetProvider<[FrooxEngine]FrooxEngine.ITexture2D>";
        private const string ColliderTypeEnumType = "[FrooxEngine]FrooxEngine.ColliderType";

        // 3D Tiles glTF content is Y-up; convert to tiles/world Z-up before tile transform application.
        private static readonly Matrix4x4d GltfYUpToZUp = new(
            1d, 0d, 0d, 0d,
            0d, 0d, 1d, 0d,
            0d, -1d, 0d, 0d,
            0d, 0d, 0d, 1d);

        private static readonly string[] PreferredTextureFieldNames = ["AlbedoTexture", "BaseColorTexture", "MainTexture", "Texture"];
        private static readonly string[] SimpleAvatarProtectionComponentTypeCandidates =
        [
            "[FrooxEngine]FrooxEngine.SimpleAvatarProtection",
            "[FrooxEngine.Users]FrooxEngine.SimpleAvatarProtection",
            "FrooxEngine.SimpleAvatarProtection"
        ];
        private static readonly string[] UserLoginStatusComponentTypeCandidates =
        [
            "[FrooxEngine]FrooxEngine.UserLoginStatus",
            "[FrooxEngine.Users]FrooxEngine.UserLoginStatus",
            "FrooxEngine.UserLoginStatus"
        ];

        private readonly ITileContentFetcher _fetcher;
        private readonly ITileStreamingScheduler _scheduler;
        private readonly IGlbMeshExtractor _glbMeshExtractor;
        private readonly ICoordinateTransformer _coordinateTransformer;
        private readonly IGoogleAccessTokenProvider _googleAccessTokenProvider;
        private readonly ILogger<TileStreamingService> _logger;
        private readonly LinkInterface? _linkInterface;
        private readonly IResoniteLinkClient? _testClient;
        private readonly bool _dumpMeshJson;
        private readonly string _meshDumpDir = Path.Combine(Path.GetTempPath(), "3DTilesLink", "mesh-json");
        private readonly HashSet<string> _tempTextureFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _textureTempDir = Path.Combine(Path.GetTempPath(), "3DTilesLink", "textures");

        private bool _directClientInitialized;
        private string? _sessionRootSlotId;
        private string? _sessionLicenseComponentId;
        private string? _sessionLicenseCreditText;
        private string? _materialTextureFieldName;
        private bool _materialTextureFieldResolved;
        private Dictionary<string, MemberDefinition>? _materialMemberDefinitions;
        private string? _avatarProtectionComponentType;
        private string? _avatarProtectionUserTargetType;
        private bool _avatarProtectionHasReassignUserOnPackageImport;
        private bool _avatarProtectionUnavailable;
        private string? _localUserId;

        public TileStreamingService(
            ITileContentFetcher fetcher,
            ITileStreamingScheduler scheduler,
            IGlbMeshExtractor glbMeshExtractor,
            ICoordinateTransformer coordinateTransformer,
            LinkInterface resoniteLink,
            IGoogleAccessTokenProvider googleAccessTokenProvider,
            ILogger<TileStreamingService> logger)
            : this(fetcher, scheduler, glbMeshExtractor, coordinateTransformer, googleAccessTokenProvider, logger)
        {
            _linkInterface = resoniteLink ?? throw new ArgumentNullException(nameof(resoniteLink));
        }

        public TileStreamingService(
            ITileContentFetcher fetcher,
            ITileStreamingScheduler scheduler,
            IGlbMeshExtractor glbMeshExtractor,
            ICoordinateTransformer coordinateTransformer,
            IResoniteLinkClient resoniteLinkClient,
            IGoogleAccessTokenProvider googleAccessTokenProvider,
            ILogger<TileStreamingService> logger)
            : this(fetcher, scheduler, glbMeshExtractor, coordinateTransformer, googleAccessTokenProvider, logger)
        {
            _testClient = resoniteLinkClient ?? throw new ArgumentNullException(nameof(resoniteLinkClient));
        }

        private TileStreamingService(
            ITileContentFetcher fetcher,
            ITileStreamingScheduler scheduler,
            IGlbMeshExtractor glbMeshExtractor,
            ICoordinateTransformer coordinateTransformer,
            IGoogleAccessTokenProvider googleAccessTokenProvider,
            ILogger<TileStreamingService> logger)
        {
            _fetcher = fetcher;
            _scheduler = scheduler;
            _glbMeshExtractor = glbMeshExtractor;
            _coordinateTransformer = coordinateTransformer;
            _googleAccessTokenProvider = googleAccessTokenProvider;
            _logger = logger;

            string? dumpMeshJson = Environment.GetEnvironmentVariable("THREEDTILESLINK_DUMP_MESH_JSON")?.Trim();
            _dumpMeshJson = string.Equals(dumpMeshJson, "1", StringComparison.Ordinal) ||
                            string.Equals(dumpMeshJson, "true", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<RunSummary> RunAsync(StreamerOptions options, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);
            GoogleTilesAuth auth = await BuildAuthAsync(options, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Fetching root tileset from Google Map Tiles API.");
            Tileset rootTileset = await _fetcher.FetchRootTilesetAsync(auth, cancellationToken).ConfigureAwait(false);
            _scheduler.Initialize(rootTileset, options);

            if (!options.DryRun && options.ManageResoniteConnection)
            {
                _logger.LogInformation("Connecting to Resonite Link at {Host}:{Port}", options.ResoniteHost, options.ResonitePort);
                await ConnectAsync(options.ResoniteHost, options.ResonitePort, cancellationToken).ConfigureAwait(false);
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
                    await DisconnectAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            return _scheduler.GetSummary();
        }

        internal async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            if (_testClient is not null)
            {
                await _testClient.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
                return;
            }

            EnsureDirectMode();
            if (_linkInterface!.IsConnected)
            {
                return;
            }

            _directClientInitialized = true;
            await _linkInterface.Connect(new Uri($"ws://{host}:{port}/"), cancellationToken).ConfigureAwait(false);

            try
            {
                _sessionRootSlotId = await CreateSlotAsync(
                    $"3DTilesLink Session {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}",
                    Slot.ROOT_SLOT_ID).ConfigureAwait(false);

                await AttachSessionMetadataAsync(_sessionRootSlotId, cancellationToken).ConfigureAwait(false);
                await ResolveAvatarProtectionContextAsync().ConfigureAwait(false);
            }
            catch
            {
                DisposeDirectLink();
                throw;
            }
        }

        internal Task DisconnectAsync(CancellationToken cancellationToken)
        {
            if (_testClient is not null)
            {
                return _testClient.DisconnectAsync(cancellationToken);
            }

            DisposeDirectLink();
            return Task.CompletedTask;
        }

        internal async Task<string> CreateSessionChildSlotAsync(string name, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureDirectConnection();
            if (string.IsNullOrWhiteSpace(_sessionRootSlotId))
            {
                throw new InvalidOperationException("Session root slot is not initialized.");
            }

            return await CreateSlotAsync(name, _sessionRootSlotId).ConfigureAwait(false);
        }

        internal async Task<ProbeBinding> CreateProbeAsync(ProbeConfiguration configuration, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureDirectConnection();

            if (string.IsNullOrWhiteSpace(_sessionRootSlotId))
            {
                throw new InvalidOperationException("Session root slot is not initialized.");
            }

            string latComponentId = await AddDynamicValueVariableAsync(
                _sessionRootSlotId,
                configuration.LatitudeVariablePath).ConfigureAwait(false);

            string lonComponentId = await AddDynamicValueVariableAsync(
                _sessionRootSlotId,
                configuration.LongitudeVariablePath).ConfigureAwait(false);

            string rangeComponentId = await AddDynamicValueVariableAsync(
                _sessionRootSlotId,
                configuration.RangeVariablePath).ConfigureAwait(false);

            string searchComponentId = await AddDynamicStringValueVariableAsync(
                _sessionRootSlotId,
                configuration.SearchVariablePath).ConfigureAwait(false);

            return new ProbeBinding(
                _sessionRootSlotId,
                false,
                latComponentId,
                DynamicValueVariableValueMemberName,
                lonComponentId,
                DynamicValueVariableValueMemberName,
                rangeComponentId,
                DynamicValueVariableValueMemberName,
                searchComponentId,
                DynamicValueVariableValueMemberName);
        }

        internal async Task<ProbeValues?> ReadProbeValuesAsync(ProbeBinding binding, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(binding);
            EnsureDirectConnection();

            float lat = await ReadNumericMemberAsFloatAsync(binding.LatitudeComponentId, binding.LatitudeValueMemberName, cancellationToken).ConfigureAwait(false);
            float lon = await ReadNumericMemberAsFloatAsync(binding.LongitudeComponentId, binding.LongitudeValueMemberName, cancellationToken).ConfigureAwait(false);
            float range = await ReadNumericMemberAsFloatAsync(binding.RangeComponentId, binding.RangeValueMemberName, cancellationToken).ConfigureAwait(false);

            if (!float.IsFinite(lat) || !float.IsFinite(lon) || !float.IsFinite(range))
            {
                return null;
            }

            return new ProbeValues(lat, lon, range);
        }

        internal async Task<string?> ReadProbeSearchAsync(ProbeBinding binding, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(binding);
            EnsureDirectConnection();

            return await ReadStringMemberAsync(binding.SearchComponentId, binding.SearchValueMemberName, cancellationToken).ConfigureAwait(false);
        }

        internal async Task UpdateProbeCoordinatesAsync(ProbeBinding binding, double latitude, double longitude, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(binding);
            EnsureDirectConnection();

            float lat = checked((float)latitude);
            float lon = checked((float)longitude);
            if (!float.IsFinite(lat) || !float.IsFinite(lon))
            {
                throw new InvalidOperationException("Probe coordinates must be finite values.");
            }

            await UpdateNumericMemberAsync(binding.LatitudeComponentId, binding.LatitudeValueMemberName, lat).ConfigureAwait(false);
            await UpdateNumericMemberAsync(binding.LongitudeComponentId, binding.LongitudeValueMemberName, lon).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        internal async Task RemoveSlotAsync(string slotId, CancellationToken cancellationToken)
        {
            if (_testClient is not null)
            {
                await _testClient.RemoveSlotAsync(slotId, cancellationToken).ConfigureAwait(false);
                return;
            }

            EnsureDirectConnection();
            if (string.IsNullOrWhiteSpace(slotId))
            {
                return;
            }

            Response response = await _linkInterface!.RemoveSlot(new RemoveSlot { SlotID = slotId }).ConfigureAwait(false);
            _ = EnsureSuccess(response);
        }

        public async ValueTask DisposeAsync()
        {
            if (_testClient is null)
            {
                DisposeDirectLink();
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        private async Task<SchedulerWorkResult> ExecuteWorkItemAsync(
            SchedulerWorkItem workItem,
            StreamerOptions options,
            GoogleTilesAuth auth,
            CancellationToken cancellationToken)
        {
            return workItem switch
            {
                ProcessNodeContentWorkItem processWork => await ExecuteProcessNodeContentAsync(processWork, options, auth, cancellationToken).ConfigureAwait(false),
                RemoveParentTileSlotsWorkItem removeWork => await ExecuteRemoveParentTileSlotsAsync(removeWork, cancellationToken).ConfigureAwait(false),
                UpdateLicenseCreditWorkItem updateWork => await ExecuteUpdateLicenseCreditAsync(updateWork, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unsupported scheduler work item type: {workItem.GetType().Name}")
            };
        }

        private async Task<ProcessNodeContentWorkResult> ExecuteProcessNodeContentAsync(
            ProcessNodeContentWorkItem workItem,
            StreamerOptions options,
            GoogleTilesAuth auth,
            CancellationToken cancellationToken)
        {
            var streamedSlotIds = new List<string>();
            int streamedMeshCount = 0;
            string? assetCopyright = null;

            try
            {
                FetchedNodeContent content = await _fetcher
                    .FetchNodeContentAsync(workItem.Tile.ContentUri, auth, cancellationToken)
                    .ConfigureAwait(false);

                switch (content)
                {
                    case NestedTilesetFetchedContent nested:
                        return new ProcessNodeContentWorkResult(
                            workItem.Tile,
                            new NestedTilesetContentOutcome(nested.Tileset));

                    case GlbFetchedContent glb:
                        {
                            GlbExtractResult extracted = _glbMeshExtractor.Extract(glb.GlbBytes);
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
                                    string? slotId = await SendTileMeshAsync(payload, cancellationToken).ConfigureAwait(false);
                                    if (!string.IsNullOrWhiteSpace(slotId))
                                    {
                                        streamedSlotIds.Add(slotId);
                                    }
                                }

                                streamedMeshCount++;
                            }

                            return new ProcessNodeContentWorkResult(
                                workItem.Tile,
                                new StreamedRenderableContentOutcome(
                                    streamedMeshCount,
                                    streamedSlotIds,
                                    assetCopyright));
                        }

                    case UnsupportedFetchedContent unsupported:
                        return new ProcessNodeContentWorkResult(
                            workItem.Tile,
                            new UnsupportedContentOutcome(unsupported.Reason));

                    default:
                        throw new InvalidOperationException($"Unsupported fetched content type: {content.GetType().Name}");
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                return new ProcessNodeContentWorkResult(
                    workItem.Tile,
                    new UnavailableContentOutcome(ex));
            }
            catch (Exception ex)
            {
                return new ProcessNodeContentWorkResult(
                    workItem.Tile,
                    new FailedContentOutcome(
                        ex,
                        streamedMeshCount,
                        streamedSlotIds,
                        assetCopyright));
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
                    await RemoveSlotAsync(slotId, cancellationToken).ConfigureAwait(false);
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
                await SetSessionLicenseCreditAsync(workItem.CreditString, cancellationToken).ConfigureAwait(false);
                return new UpdateLicenseCreditWorkResult(workItem.CreditString, true, null);
            }
            catch (Exception ex)
            {
                return new UpdateLicenseCreditWorkResult(workItem.CreditString, false, ex);
            }
        }

        private async Task SetSessionLicenseCreditAsync(string creditString, CancellationToken cancellationToken)
        {
            if (_testClient is not null)
            {
                await _testClient.SetSessionLicenseCreditAsync(creditString, cancellationToken).ConfigureAwait(false);
                return;
            }

            EnsureDirectConnection();
            if (string.IsNullOrWhiteSpace(_sessionLicenseComponentId) || string.IsNullOrWhiteSpace(creditString))
            {
                return;
            }

            string normalized = creditString.Trim();
            if (string.Equals(normalized, _sessionLicenseCreditText, StringComparison.Ordinal))
            {
                return;
            }

            Response response = await _linkInterface!.UpdateComponent(
                new UpdateComponent
                {
                    Data = new Component
                    {
                        ID = _sessionLicenseComponentId,
                        Members = new Dictionary<string, Member>
                        {
                            ["RequireCredit"] = new Field_bool { Value = true },
                            ["CreditString"] = new Field_string { Value = normalized },
                            ["CanExport"] = new Field_bool { Value = false }
                        }
                    }
                }).ConfigureAwait(false);
            _ = EnsureSuccess(response);
            _sessionLicenseCreditText = normalized;
        }

        private async Task<string?> SendTileMeshAsync(TileMeshPayload payload, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(payload);

            if (_testClient is not null)
            {
                return await _testClient.SendTileMeshAsync(payload, cancellationToken).ConfigureAwait(false);
            }

            EnsureDirectConnection();
            if (payload.Vertices.Count == 0 || payload.Indices.Count == 0)
            {
                return null;
            }

            var triangleSubmesh = new TriangleSubmeshRawData
            {
                TriangleCount = payload.Indices.Count / 3
            };

            var importMesh = new ImportMeshRawData
            {
                VertexCount = payload.Vertices.Count,
                HasNormals = false,
                HasTangents = false,
                HasColors = false,
                BoneWeightCount = 0,
                UV_Channel_Dimensions = payload.HasUv0 ? [2] : [],
                Submeshes = [triangleSubmesh]
            };

            importMesh.AllocateBuffer();

            Span<float3> positionSpan = importMesh.Positions;
            for (int i = 0; i < payload.Vertices.Count; i++)
            {
                Vector3 p = payload.Vertices[i];
                positionSpan[i] = new float3 { x = p.X, y = p.Y, z = p.Z };
            }

            if (payload.HasUv0)
            {
                Span<float2> uvSpan = importMesh.AccessUV_2D(0);
                for (int i = 0; i < payload.Vertices.Count; i++)
                {
                    Vector2 uv = i < payload.Uvs.Count ? payload.Uvs[i] : default;
                    uvSpan[i] = new float2 { x = uv.X, y = uv.Y };
                }
            }

            Span<int> indicesSpan = triangleSubmesh.Indices;
            for (int i = 0; i < payload.Indices.Count; i++)
            {
                indicesSpan[i] = payload.Indices[i];
            }

            if (_dumpMeshJson)
            {
                _ = Directory.CreateDirectory(_meshDumpDir);
                byte[] dumpBytes = JsonSerializer.SerializeToUtf8Bytes(importMesh, LinkInterface.SerializationOptions);
                string path = Path.Combine(_meshDumpDir, $"mesh_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.json");
                await File.WriteAllBytesAsync(path, dumpBytes, cancellationToken).ConfigureAwait(false);
            }

            AssetData meshAsset = EnsureSuccess(await _linkInterface!.ImportMesh(importMesh).ConfigureAwait(false));

            string? parentSlotId = string.IsNullOrWhiteSpace(payload.ParentSlotId)
                ? _sessionRootSlotId
                : payload.ParentSlotId;
            if (string.IsNullOrWhiteSpace(parentSlotId))
            {
                throw new InvalidOperationException("Session root slot is not initialized.");
            }

            string tileSlotId = await CreateSlotAsync(
                payload.Name,
                parentSlotId,
                payload.SlotPosition,
                payload.SlotRotation,
                payload.SlotScale).ConfigureAwait(false);

            string staticMeshId = await AddComponentAsync(
                tileSlotId,
                StaticMeshComponentType,
                new Dictionary<string, Member>
                {
                    ["URL"] = new Field_Uri { Value = meshAsset.AssetURL }
                }).ConfigureAwait(false);

            _ = await AddComponentAsync(
                tileSlotId,
                MeshColliderComponentType,
                new Dictionary<string, Member>
                {
                    ["Mesh"] = new Reference { TargetType = MeshAssetProviderType, TargetID = staticMeshId },
                    ["CharacterCollider"] = new Field_bool { Value = true },
                    ["Type"] = new Field_Enum
                    {
                        EnumType = ColliderTypeEnumType,
                        Value = "Static"
                    }
                }).ConfigureAwait(false);

            var materialMembers = new Dictionary<string, Member>();
            Dictionary<string, MemberDefinition> materialMembersDefinition = await ResolveMaterialMemberDefinitionsAsync().ConfigureAwait(false);
            if (materialMembersDefinition.ContainsKey("Smoothness"))
            {
                materialMembers["Smoothness"] = new Field_float { Value = 0f };
            }

            AssetData? textureAsset = await ImportTextureAssetAsync(
                payload.BaseColorTextureBytes,
                payload.BaseColorTextureExtension,
                cancellationToken).ConfigureAwait(false);

            string? textureMemberName = await ResolveMaterialTextureMemberNameAsync().ConfigureAwait(false);
            if (textureAsset is not null && !string.IsNullOrWhiteSpace(textureMemberName))
            {
                string staticTextureId = await AddComponentAsync(
                    tileSlotId,
                    StaticTextureComponentType,
                    new Dictionary<string, Member>
                    {
                        ["URL"] = new Field_Uri { Value = textureAsset.AssetURL }
                    }).ConfigureAwait(false);

                materialMembers[textureMemberName] = new Reference
                {
                    TargetType = TextureAssetProviderType,
                    TargetID = staticTextureId
                };
            }

            string materialId = await AddComponentAsync(tileSlotId, MaterialComponentType, materialMembers).ConfigureAwait(false);

            _ = await AddComponentAsync(
                tileSlotId,
                MeshRendererComponentType,
                new Dictionary<string, Member>
                {
                    ["Mesh"] = new Reference { TargetType = MeshAssetProviderType, TargetID = staticMeshId },
                    ["Materials"] = new SyncList
                    {
                        Elements = [new Reference { TargetType = MaterialAssetProviderType, TargetID = materialId }]
                    }
                }).ConfigureAwait(false);

            await AttachAvatarProtectionAsync(tileSlotId).ConfigureAwait(false);
            return tileSlotId;
        }

        private TileMeshPayload ToEunPayload(
            MeshData mesh,
            Matrix4x4d tileWorld,
            GeoReference reference,
            string tileId,
            string? parentSlotId)
        {
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

        private async Task<string> CreateSlotAsync(
            string name,
            string? parentSlotId,
            Vector3? position = null,
            Quaternion? rotation = null,
            Vector3? scale = null)
        {
            EnsureDirectConnection();

            Vector3 p = position ?? Vector3.Zero;
            Quaternion r = rotation ?? Quaternion.Identity;
            Vector3 s = scale ?? Vector3.One;

            var slot = new Slot
            {
                ID = $"t3dtile_slot_{Guid.NewGuid():N}",
                Name = new Field_string { Value = name },
                Position = new Field_float3 { Value = new float3 { x = p.X, y = p.Y, z = p.Z } },
                Rotation = new Field_floatQ { Value = new floatQ { x = r.X, y = r.Y, z = r.Z, w = r.W } },
                Scale = new Field_float3 { Value = new float3 { x = s.X, y = s.Y, z = s.Z } },
                IsPersistent = new Field_bool { Value = false }
            };

            if (!string.IsNullOrWhiteSpace(parentSlotId))
            {
                slot.Parent = new Reference
                {
                    TargetID = parentSlotId,
                    TargetType = SlotWorkerType
                };
            }

            NewEntityId response = EnsureSuccess(await _linkInterface!.AddSlot(new AddSlot { Data = slot }).ConfigureAwait(false));
            return response.EntityId;
        }

        private async Task<string> AddComponentAsync(
            string slotId,
            string componentType,
            Dictionary<string, Member> members)
        {
            EnsureDirectConnection();

            NewEntityId response = EnsureSuccess(await _linkInterface!.AddComponent(
                new AddComponent
                {
                    ContainerSlotId = slotId,
                    Data = new Component
                    {
                        ID = $"t3dtile_comp_{Guid.NewGuid():N}",
                        ComponentType = componentType,
                        Members = members
                    }
                }).ConfigureAwait(false));
            return response.EntityId;
        }

        private async Task<string> AddDynamicValueVariableAsync(string slotId, string variablePath)
        {
            return await AddComponentAsync(
                slotId,
                DynamicValueVariableFloatComponentType,
                new Dictionary<string, Member>(StringComparer.Ordinal)
                {
                    ["VariableName"] = new Field_string { Value = variablePath }
                }).ConfigureAwait(false);
        }

        private async Task<string> AddDynamicStringValueVariableAsync(string slotId, string variablePath)
        {
            return await AddComponentAsync(
                slotId,
                DynamicValueVariableStringComponentType,
                new Dictionary<string, Member>(StringComparer.Ordinal)
                {
                    ["VariableName"] = new Field_string { Value = variablePath }
                }).ConfigureAwait(false);
        }

        private async Task<float> ReadNumericMemberAsFloatAsync(string componentId, string memberName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Member member = await ReadComponentMemberAsync(componentId, memberName).ConfigureAwait(false);
            return member switch
            {
                Field_double doubleMember => (float)doubleMember.Value,
                Field_float floatMember => floatMember.Value,
                Field_decimal decimalMember => (float)decimalMember.Value,
                _ => throw new InvalidOperationException(
                    $"Unsupported numeric member type: componentId={componentId} member={memberName} type={member.GetType().Name}")
            };
        }

        private async Task<bool> ReadBooleanMemberAsync(string componentId, string memberName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Member member = await ReadComponentMemberAsync(componentId, memberName).ConfigureAwait(false);
            return member is Field_bool boolMember
                ? boolMember.Value
                : throw new InvalidOperationException(
                    $"Unsupported boolean member type: componentId={componentId} member={memberName} type={member.GetType().Name}");
        }

        private async Task<string> ReadStringMemberAsync(string componentId, string memberName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Member member = await ReadComponentMemberAsync(componentId, memberName).ConfigureAwait(false);
            return member is Field_string stringMember
                ? stringMember.Value
                : throw new InvalidOperationException(
                    $"Unsupported string member type: componentId={componentId} member={memberName} type={member.GetType().Name}");
        }

        private async Task UpdateNumericMemberAsync(string componentId, string memberName, float value)
        {
            EnsureDirectConnection();

            Response response = await _linkInterface!.UpdateComponent(
                new UpdateComponent
                {
                    Data = new Component
                    {
                        ID = componentId,
                        Members = new Dictionary<string, Member>(StringComparer.Ordinal)
                        {
                            [memberName] = new Field_float { Value = value }
                        }
                    }
                }).ConfigureAwait(false);
            _ = EnsureSuccess(response);
        }

        private async Task<Member> ReadComponentMemberAsync(string componentId, string memberName)
        {
            EnsureDirectConnection();
            ComponentData componentData = EnsureSuccess(await _linkInterface!.GetComponentData(
                new GetComponent { ComponentID = componentId }).ConfigureAwait(false));

            if (componentData.Data is null || !componentData.Data.Members.TryGetValue(memberName, out Member? member))
            {
                throw new InvalidOperationException($"Component member not found: componentId={componentId} member={memberName}");
            }

            return member;
        }

        private async Task<AssetData?> ImportTextureAssetAsync(byte[]? textureBytes, string? extension, CancellationToken cancellationToken)
        {
            EnsureDirectConnection();
            if (textureBytes is null || textureBytes.Length == 0)
            {
                return null;
            }

            _ = Directory.CreateDirectory(_textureTempDir);

            string hash = Convert.ToHexString(SHA256.HashData(textureBytes));
            string ext = NormalizeExtension(extension);
            string path = Path.Combine(_textureTempDir, $"{hash}{ext}");

            if (!File.Exists(path))
            {
                await File.WriteAllBytesAsync(path, textureBytes, cancellationToken).ConfigureAwait(false);
            }

            _ = _tempTextureFiles.Add(path);
            return EnsureSuccess(await _linkInterface!.ImportTexture(new ImportTexture2DFile { FilePath = path }).ConfigureAwait(false));
        }

        private async Task<string?> ResolveMaterialTextureMemberNameAsync()
        {
            if (_materialTextureFieldResolved)
            {
                return _materialTextureFieldName;
            }

            _materialTextureFieldResolved = true;

            try
            {
                Dictionary<string, MemberDefinition> members = await ResolveMaterialMemberDefinitionsAsync().ConfigureAwait(false);
                var textureFields = members
                    .Where(x => x.Value is ReferenceDefinition refDef && IsTextureProvider(refDef.TargetType))
                    .Select(x => x.Key)
                    .ToHashSet(StringComparer.Ordinal);

                if (textureFields.Count == 0)
                {
                    foreach (string preferred in PreferredTextureFieldNames)
                    {
                        if (members.ContainsKey(preferred))
                        {
                            _materialTextureFieldName = preferred;
                            return _materialTextureFieldName;
                        }
                    }

                    List<string> byName = members.Keys
                        .Where(static key => key.Contains("Texture", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (byName.Count == 0)
                    {
                        return null;
                    }

                    _materialTextureFieldName = byName[0];
                    return _materialTextureFieldName;
                }

                foreach (string preferred in PreferredTextureFieldNames)
                {
                    if (textureFields.Contains(preferred))
                    {
                        _materialTextureFieldName = preferred;
                        return _materialTextureFieldName;
                    }
                }

                _materialTextureFieldName = textureFields.First();
                return _materialTextureFieldName;
            }
            catch
            {
                return null;
            }
        }

        private async Task AttachSessionMetadataAsync(string sessionRootSlotId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string creditFieldId = $"t3dtile_field_{Guid.NewGuid():N}";
            string? licenseComponentId = await TryAddComponentAsync(
                sessionRootSlotId,
                LicenseComponentType,
                new Dictionary<string, Member>
                {
                    ["RequireCredit"] = new Field_bool { Value = true },
                    ["CreditString"] = new Field_string
                    {
                        ID = creditFieldId,
                        Value = DefaultGoogleMapsCreditText
                    },
                    ["CanExport"] = new Field_bool { Value = false }
                }).ConfigureAwait(false);

            if (licenseComponentId is null)
            {
                return;
            }

            _sessionLicenseComponentId = licenseComponentId;
            _sessionLicenseCreditText = DefaultGoogleMapsCreditText;

            _ = await TryAddComponentAsync(
                sessionRootSlotId,
                DynamicVariableSpaceComponentType,
                new Dictionary<string, Member>
                {
                    ["SpaceName"] = new Field_string { Value = GoogleTilesDynamicSpaceName },
                    ["OnlyDirectBinding"] = new Field_bool { Value = true }
                }).ConfigureAwait(false);

            _ = await TryAddComponentAsync(
                sessionRootSlotId,
                DynamicFieldStringComponentType,
                new Dictionary<string, Member>
                {
                    ["VariableName"] = new Field_string { Value = LicenseDynamicVariablePath },
                    ["TargetField"] = new Reference { TargetID = creditFieldId, TargetType = StringFieldType },
                    ["OverrideOnLink"] = new Field_bool { Value = true }
                }).ConfigureAwait(false);
        }

        private async Task ResolveAvatarProtectionContextAsync()
        {
            if (_avatarProtectionUnavailable)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(_avatarProtectionComponentType) &&
                !string.IsNullOrWhiteSpace(_avatarProtectionUserTargetType) &&
                !string.IsNullOrWhiteSpace(_localUserId))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_sessionRootSlotId))
            {
                throw new InvalidOperationException("Session root slot is not initialized.");
            }

            (string protectionComponentType, Dictionary<string, MemberDefinition> protectionMembers) protectionContext;
            try
            {
                protectionContext = await ResolveComponentDefinitionAsync(
                    SimpleAvatarProtectionComponentTypeCandidates).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                _avatarProtectionUnavailable = true;
                return;
            }

            if (!protectionContext.protectionMembers.TryGetValue(SimpleAvatarProtectionUserMemberName, out MemberDefinition? userMemberDefinition) ||
                userMemberDefinition is not ReferenceDefinition userReferenceDefinition ||
                string.IsNullOrWhiteSpace(userReferenceDefinition.TargetType?.Type))
            {
                _avatarProtectionUnavailable = true;
                return;
            }

            (string loginStatusComponentType, Dictionary<string, MemberDefinition> _) loginStatusContext;
            try
            {
                loginStatusContext = await ResolveComponentDefinitionAsync(
                    UserLoginStatusComponentTypeCandidates).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                _avatarProtectionUnavailable = true;
                return;
            }

            string loginStatusComponentId = await AddComponentAsync(
                _sessionRootSlotId,
                loginStatusContext.loginStatusComponentType,
                []).ConfigureAwait(false);

            try
            {
                bool isLoggedIn = await ReadBooleanMemberAsync(
                    loginStatusComponentId,
                    UserLoginStatusLoggedInMemberName,
                    CancellationToken.None).ConfigureAwait(false);

                string userId = await ReadStringMemberAsync(
                    loginStatusComponentId,
                    UserLoginStatusUserIdMemberName,
                    CancellationToken.None).ConfigureAwait(false);

                if (!isLoggedIn || string.IsNullOrWhiteSpace(userId))
                {
                    _avatarProtectionUnavailable = true;
                    return;
                }

                _localUserId = userId.Trim();
            }
            finally
            {
                await RemoveComponentAsync(loginStatusComponentId).ConfigureAwait(false);
            }

            _avatarProtectionComponentType = protectionContext.protectionComponentType;
            _avatarProtectionUserTargetType = userReferenceDefinition.TargetType.Type;
            _avatarProtectionHasReassignUserOnPackageImport = protectionContext.protectionMembers.ContainsKey(SimpleAvatarProtectionReassignMemberName);
        }

        private async Task AttachAvatarProtectionAsync(string slotId)
        {
            if (string.IsNullOrWhiteSpace(slotId))
            {
                throw new InvalidOperationException("Target slot is not initialized.");
            }

            await ResolveAvatarProtectionContextAsync().ConfigureAwait(false);
            if (_avatarProtectionUnavailable ||
                string.IsNullOrWhiteSpace(_avatarProtectionComponentType) ||
                string.IsNullOrWhiteSpace(_avatarProtectionUserTargetType) ||
                string.IsNullOrWhiteSpace(_localUserId))
            {
                return;
            }

            var members = new Dictionary<string, Member>(StringComparer.Ordinal)
            {
                [SimpleAvatarProtectionUserMemberName] = new Reference
                {
                    TargetID = _localUserId,
                    TargetType = _avatarProtectionUserTargetType
                }
            };

            if (_avatarProtectionHasReassignUserOnPackageImport)
            {
                members[SimpleAvatarProtectionReassignMemberName] = new Field_bool { Value = false };
            }

            _ = await AddComponentAsync(
                slotId,
                _avatarProtectionComponentType,
                members).ConfigureAwait(false);
        }

        private async Task<string?> TryAddComponentAsync(
            string slotId,
            string componentType,
            Dictionary<string, Member> members)
        {
            try
            {
                return await AddComponentAsync(slotId, componentType, members).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        private async Task<(string ComponentType, Dictionary<string, MemberDefinition> Members)> ResolveComponentDefinitionAsync(
            IEnumerable<string> componentTypeCandidates)
        {
            EnsureDirectConnection();

            foreach (string componentType in componentTypeCandidates)
            {
                try
                {
                    ComponentDefinitionData definition = EnsureSuccess(await _linkInterface!.GetComponentDefinition(componentType, flattened: true).ConfigureAwait(false));
                    return (componentType, definition.Definition?.Members ?? new Dictionary<string, MemberDefinition>(StringComparer.Ordinal));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                }
            }

            throw new InvalidOperationException(
                $"No supported component type was found. Candidates: {string.Join(", ", componentTypeCandidates)}");
        }

        private async Task<Dictionary<string, MemberDefinition>> ResolveMaterialMemberDefinitionsAsync()
        {
            if (_materialMemberDefinitions is not null)
            {
                return _materialMemberDefinitions;
            }

            EnsureDirectConnection();
            ComponentDefinitionData definition = EnsureSuccess(await _linkInterface!.GetComponentDefinition(MaterialComponentType, flattened: true).ConfigureAwait(false));
            _materialMemberDefinitions = definition.Definition?.Members ?? new Dictionary<string, MemberDefinition>(StringComparer.Ordinal);
            return _materialMemberDefinitions;
        }

        private async Task RemoveComponentAsync(string componentId)
        {
            EnsureDirectConnection();
            if (string.IsNullOrWhiteSpace(componentId))
            {
                return;
            }

            Response response = await _linkInterface!.RemoveComponent(new RemoveComponent { ComponentID = componentId }).ConfigureAwait(false);
            _ = EnsureSuccess(response);
        }

        private static bool IsTextureProvider(TypeReference? typeRef)
        {
            if (typeRef is null)
            {
                return false;
            }

            return (typeRef.Type?.Contains("Texture2D", StringComparison.OrdinalIgnoreCase) ?? false) &&
                (typeRef.Type?.Contains("IAssetProvider", StringComparison.OrdinalIgnoreCase) ?? true) ||
                typeRef.GenericArguments is not null && typeRef.GenericArguments.Any(IsTextureProvider);
        }

        private static string NormalizeExtension(string? ext)
        {
            return string.IsNullOrWhiteSpace(ext) ? ".png" : ext.StartsWith('.') ? ext : $".{ext}";
        }

        private static T EnsureSuccess<T>(T response) where T : Response
        {
            return !response.Success
                ? throw new InvalidOperationException($"ResoniteLink request failed: {response.ErrorInfo}")
                : response;
        }

        private void EnsureDirectMode()
        {
            if (_linkInterface is null)
            {
                throw new InvalidOperationException("This TileStreamingService instance is using the test seam, not direct ResoniteLink access.");
            }
        }

        private void EnsureDirectConnection()
        {
            EnsureDirectMode();
            if (!_directClientInitialized || !_linkInterface!.IsConnected)
            {
                throw new InvalidOperationException("ResoniteLink is not connected.");
            }
        }

        private void DisposeDirectLink()
        {
            try
            {
                if (_directClientInitialized)
                {
                    _linkInterface?.Dispose();
                }
            }
            catch
            {
            }
            finally
            {
                _directClientInitialized = false;
                _sessionRootSlotId = null;
                _sessionLicenseComponentId = null;
                _sessionLicenseCreditText = null;
                _materialTextureFieldName = null;
                _materialTextureFieldResolved = false;
                _materialMemberDefinitions = null;
                _avatarProtectionComponentType = null;
                _avatarProtectionUserTargetType = null;
                _avatarProtectionHasReassignUserOnPackageImport = false;
                _avatarProtectionUnavailable = false;
                _localUserId = null;

                foreach (string textureFile in _tempTextureFiles)
                {
                    try
                    {
                        if (File.Exists(textureFile))
                        {
                            File.Delete(textureFile);
                        }
                    }
                    catch
                    {
                    }
                }

                _tempTextureFiles.Clear();
            }
        }
    }
}
