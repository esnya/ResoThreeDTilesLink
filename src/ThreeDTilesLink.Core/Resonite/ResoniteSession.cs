using System.Numerics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using ResoniteLink;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Resonite
{
    internal sealed partial class ResoniteSession(
        LinkInterface resoniteLink,
        ILogger<ResoniteSession> logger,
        Func<LinkInterface>? linkInterfaceFactory = null,
        int assetImportWorkers = 1) : IResoniteSession, IWatchStore, IAsyncDisposable
    {
        private static partial class Log
        {
            [LoggerMessage(
                EventId = 2001,
                Level = LogLevel.Debug,
                Message = "Opening Resonite Link session to {Host}:{Port}.")]
            public static partial void OpeningSession(ILogger logger, string host, int port);

            [LoggerMessage(
                EventId = 2002,
                Level = LogLevel.Information,
                Message = "Reconnected to Resonite Link at {Host}:{Port}; reusing session root {SlotId}.")]
            public static partial void ReconnectedSession(
                ILogger logger,
                string host,
                int port,
                string? slotId);

            [LoggerMessage(
                EventId = 2003,
                Level = LogLevel.Debug,
                Message = "Closing Resonite Link transport.")]
            public static partial void ClosingSession(ILogger logger);

            [LoggerMessage(
                EventId = 2004,
                Level = LogLevel.Warning,
                Message = "Resonite Link transport is disconnected. Reconnecting to {Host}:{Port}.")]
            public static partial void ReconnectingSession(ILogger logger, string host, int port);

            [LoggerMessage(
                EventId = 2005,
                Level = LogLevel.Warning,
                Message = "Resonite Link transport failed. Reconnecting to {Host}:{Port}.")]
            public static partial void ReconnectingSession(
                ILogger logger,
                string host,
                int port,
                Exception exception);

            [LoggerMessage(
                EventId = 2006,
                Level = LogLevel.Information,
                Message = "Reconnected to Resonite Link at {Host}:{Port}.")]
            public static partial void ReconnectedTransport(ILogger logger, string host, int port);

            [LoggerMessage(
                EventId = 2007,
                Level = LogLevel.Warning,
                Message = "Failed to update mirrored numeric alias component {ComponentId}.")]
            public static partial void MirroredNumericAliasUpdateFailed(ILogger logger, Exception exception, string componentId);

            [LoggerMessage(
                EventId = 2008,
                Level = LogLevel.Warning,
                Message = "Failed to update mirrored string alias component {ComponentId}.")]
            public static partial void MirroredStringAliasUpdateFailed(ILogger logger, Exception exception, string componentId);
        }

        private const string SlotWorkerType = "[FrooxEngine]FrooxEngine.Slot";
        private const string StaticMeshComponentType = "[FrooxEngine]FrooxEngine.StaticMesh";
        private const string StaticTextureComponentType = "[FrooxEngine]FrooxEngine.StaticTexture2D";
        private const string MeshColliderComponentType = "[FrooxEngine]FrooxEngine.MeshCollider";
        private const string MaterialComponentType = "[FrooxEngine]FrooxEngine.PBS_Metallic";
        private const string MeshRendererComponentType = "[FrooxEngine]FrooxEngine.MeshRenderer";
        private const string LicenseComponentType = "[FrooxEngine]FrooxEngine.License";
        private const string PackageExportableComponentType = "[FrooxEngine]FrooxEngine.PackageExportable";
        private const string PackageExportableRootMemberName = "Root";
        private const string SimpleAvatarProtectionComponentType = "[FrooxEngine]FrooxEngine.CommonAvatar.SimpleAvatarProtection";
        private const string SimpleAvatarProtectionUserMemberName = "User";
        private const string SimpleAvatarProtectionCloudUserRefType = "[FrooxEngine]FrooxEngine.CloudUserRef";
        private const string SimpleAvatarProtectionReassignMemberName = "ReassignUserOnPackageImport";
        private const string DynamicVariableSpaceComponentType = "[FrooxEngine]FrooxEngine.DynamicVariableSpace";
        private const string DynamicValueVariableFloatComponentType = "[FrooxEngine]FrooxEngine.DynamicValueVariable<float>";
        private const string DynamicValueVariableStringComponentType = "[FrooxEngine]FrooxEngine.DynamicValueVariable<string>";
        private const string ValueCopyFloatComponentType = "[FrooxEngine]FrooxEngine.ValueCopy<float>";
        private const string ValueCopyStringComponentType = "[FrooxEngine]FrooxEngine.ValueCopy<string>";
        private const string DynamicValueVariableValueMemberName = "Value";
        private const string StringFieldType = "[FrooxEngine]FrooxEngine.IField<string>";
        private const string FloatFieldType = "[FrooxEngine]FrooxEngine.IField<float>";
        private const string GoogleTilesDynamicSpaceName = "Google3DTiles";
        private const string LicenseDynamicVariablePath = "World/ThreeDTilesLink.License";
        private const string ProgressValueVariableLocalName = "Progress";
        private const string ProgressTextVariableLocalName = "ProgressText";
        private const string ProgressDynamicVariablePath = "World/ThreeDTilesLink.Progress";
        private const string ProgressTextDynamicVariablePath = "World/ThreeDTilesLink.ProgressText";
        private const string ParentDynamicSpaceNamePrefix = "ThreeDTilesLink.Parent";
        private const string DefaultGoogleMapsCreditText = "Google Maps";
        private const string PackageExportWarningSlotName = "EXPORT PROHIBITED: STREAMED GOOGLE 3D TILES";
        private const string MeshAssetProviderType = "[FrooxEngine]FrooxEngine.IAssetProvider<[FrooxEngine]FrooxEngine.Mesh>";
        private const string MaterialAssetProviderType = "[FrooxEngine]FrooxEngine.IAssetProvider<[FrooxEngine]FrooxEngine.Material>";
        private const string TextureAssetProviderType = "[FrooxEngine]FrooxEngine.IAssetProvider<[FrooxEngine]FrooxEngine.ITexture2D>";
        private const string ColliderTypeEnumType = "[FrooxEngine]FrooxEngine.ColliderType";
        private const int ReadComponentMemberMaxAttempts = 3;
        private const int LinkRequestMaxAttempts = 2;
        private static readonly TimeSpan DefaultLinkRequestTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan MeshImportRequestTimeout = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan ReadComponentMemberRetryDelay = TimeSpan.FromMilliseconds(25);

        private static readonly string[] PreferredTextureFieldNames = ["AlbedoTexture", "BaseColorTexture", "MainTexture", "Texture"];
        private LinkInterface _linkInterface = resoniteLink ?? throw new ArgumentNullException(nameof(resoniteLink));
        private readonly Func<LinkInterface> _linkInterfaceFactory = linkInterfaceFactory ?? (() => new LinkInterface());
        private readonly ILogger<ResoniteSession> _logger = logger;
        private readonly bool _dumpMeshJson = string.Equals(Environment.GetEnvironmentVariable("THREEDTILESLINK_DUMP_MESH_JSON")?.Trim(), "1", StringComparison.Ordinal) ||
                                              string.Equals(Environment.GetEnvironmentVariable("THREEDTILESLINK_DUMP_MESH_JSON")?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        private readonly string _meshDumpDir = Path.Combine(Path.GetTempPath(), "3DTilesLink", "mesh-json");
        private readonly ConcurrentDictionary<string, byte> _tempTextureFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _textureFileLocks = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _textureTempDir = Path.Combine(Path.GetTempPath(), "3DTilesLink", "textures");
        private readonly Dictionary<string, SlotProgressBinding> _progressBindingsByParentSlotId = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _connectionGate = new(1, 1);
        private readonly SemaphoreSlim _streamPlacementGate = new(1, 1);
        private readonly ResoniteAssetImportPool _assetImportPool = new(
            assetImportWorkers > 0 ? assetImportWorkers : throw new ArgumentOutOfRangeException(nameof(assetImportWorkers), "Asset import worker count must be positive."),
            linkInterfaceFactory ?? (() => new LinkInterface()));

        private bool _directClientInitialized;
        private Uri? _connectionUri;
        private string? _sessionRootSlotId;
        private string? _sessionLicenseComponentId;
        private string? _sessionLicenseCreditText;
        private string? _sessionLicenseAliasComponentId;
        private string? _materialTextureFieldName;
        private bool _materialTextureFieldResolved;
        private Dictionary<string, MemberDefinition>? _materialMemberDefinitions;
        private string? _avatarProtectionComponentType;
        private bool _avatarProtectionUnavailable;
        private string? _packageExportWarningSlotId;
        private bool _sessionDynamicSpaceInitialized;

        public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            Uri endpoint = new($"ws://{host}:{port}/");
            bool reusedExistingSessionState = false;
            ExceptionDispatchInfo? initializationFailure = null;

            await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (_linkInterface.IsConnected)
            {
                if (!MatchesEndpoint(endpoint))
                {
                    _ = _connectionGate.Release();
                    throw new InvalidOperationException("ResoniteLink is already connected to a different endpoint.");
                }

                _ = _connectionGate.Release();
                return;
            }

            try
            {
                bool reuseSessionState = MatchesEndpoint(endpoint) && !string.IsNullOrWhiteSpace(_sessionRootSlotId);
                if (!reuseSessionState)
                {
                    ResetSessionState();
                }

                _connectionUri = endpoint;
                Log.OpeningSession(_logger, host, port);
                await ConnectTransportAsync(endpoint, cancellationToken).ConfigureAwait(false);
                await _assetImportPool.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
                reusedExistingSessionState = !string.IsNullOrWhiteSpace(_sessionRootSlotId);
            }
            catch (Exception ex) when (IsConnectInitializationFailure(ex))
            {
                initializationFailure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                _ = _connectionGate.Release();
            }

            if (initializationFailure is not null)
            {
                await CleanupConnectInitializationFailureAsync().ConfigureAwait(false);
                initializationFailure.Throw();
            }

            if (reusedExistingSessionState)
            {
                Log.ReconnectedSession(
                    _logger,
                    host,
                    port,
                    _sessionRootSlotId);
                return;
            }

            try
            {
                _sessionRootSlotId = await CreateSlotAsync(
                    $"3DTilesLink Session {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}",
                    Slot.ROOT_SLOT_ID,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                await AttachSessionMetadataAsync(_sessionRootSlotId, cancellationToken).ConfigureAwait(false);
                await AttachAvatarProtectionAsync(_sessionRootSlotId).ConfigureAwait(false);
                await AttachPackageExportableAsync(_sessionRootSlotId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await CleanupConnectInitializationFailureAsync().ConfigureAwait(false);
                throw;
            }
            catch (ResoniteLinkNoResponseException)
            {
                await CleanupConnectInitializationFailureAsync().ConfigureAwait(false);
                throw;
            }
            catch (ResoniteLinkDisconnectedException)
            {
                await CleanupConnectInitializationFailureAsync().ConfigureAwait(false);
                throw;
            }
            catch (TimeoutException)
            {
                await CleanupConnectInitializationFailureAsync().ConfigureAwait(false);
                throw;
            }
            catch (ObjectDisposedException)
            {
                await CleanupConnectInitializationFailureAsync().ConfigureAwait(false);
                throw;
            }
            catch (WebSocketException)
            {
                await CleanupConnectInitializationFailureAsync().ConfigureAwait(false);
                throw;
            }
            catch (InvalidOperationException)
            {
                await CleanupConnectInitializationFailureAsync().ConfigureAwait(false);
                throw;
            }
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            return DisconnectCoreAsync(cancellationToken);
        }

        private async Task CleanupConnectInitializationFailureAsync()
        {
            await _connectionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                try
                {
                    await _assetImportPool.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                }
                catch (InvalidOperationException)
                {
                }

                try
                {
                    await CleanupTemporaryFilesAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                }
                catch (InvalidOperationException)
                {
                }

                DisposeDirectLink(clearSessionState: true);
            }
            finally
            {
                _ = _connectionGate.Release();
            }
        }

        private static bool IsConnectInitializationFailure(Exception exception)
        {
            return exception is OperationCanceledException or
                ResoniteLinkNoResponseException or
                ResoniteLinkDisconnectedException or
                TimeoutException or
                ObjectDisposedException or
                WebSocketException or
                InvalidOperationException;
        }

        public async Task<string> CreateSessionChildSlotAsync(string name, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(_sessionRootSlotId))
            {
                throw new InvalidOperationException("Session root slot is not initialized.");
            }

            return await CreateSlotAsync(name, _sessionRootSlotId, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public async Task<WatchBinding> CreateWatchAsync(WatchConfiguration configuration, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(_sessionRootSlotId))
            {
                throw new InvalidOperationException("Session root slot is not initialized.");
            }

            DynamicVariableBinding latBinding = await AddDynamicFloatValueVariableAsync(
                _sessionRootSlotId,
                BuildSessionVariablePath(GoogleTilesDynamicSpaceName, configuration.LatitudeVariablePath),
                0f,
                cancellationToken).ConfigureAwait(false);
            DynamicVariableBinding lonBinding = await AddDynamicFloatValueVariableAsync(
                _sessionRootSlotId,
                BuildSessionVariablePath(GoogleTilesDynamicSpaceName, configuration.LongitudeVariablePath),
                0f,
                cancellationToken).ConfigureAwait(false);
            DynamicVariableBinding rangeBinding = await AddDynamicFloatValueVariableAsync(
                _sessionRootSlotId,
                BuildSessionVariablePath(GoogleTilesDynamicSpaceName, configuration.RangeVariablePath),
                0f,
                cancellationToken).ConfigureAwait(false);
            DynamicVariableBinding searchBinding = await AddDynamicStringValueVariableAsync(
                _sessionRootSlotId,
                BuildSessionVariablePath(GoogleTilesDynamicSpaceName, configuration.SearchVariablePath),
                string.Empty,
                cancellationToken).ConfigureAwait(false);

            DynamicVariableBinding latAliasBinding = await AddWorldFloatAliasAsync(
                _sessionRootSlotId,
                configuration.LatitudeVariablePath,
                latBinding.ValueFieldId,
                writeBack: true,
                cancellationToken).ConfigureAwait(false);
            DynamicVariableBinding lonAliasBinding = await AddWorldFloatAliasAsync(
                _sessionRootSlotId,
                configuration.LongitudeVariablePath,
                lonBinding.ValueFieldId,
                writeBack: true,
                cancellationToken).ConfigureAwait(false);
            DynamicVariableBinding rangeAliasBinding = await AddWorldFloatAliasAsync(
                _sessionRootSlotId,
                configuration.RangeVariablePath,
                rangeBinding.ValueFieldId,
                writeBack: true,
                cancellationToken).ConfigureAwait(false);
            DynamicVariableBinding searchAliasBinding = await AddWorldStringAliasAsync(
                _sessionRootSlotId,
                configuration.SearchVariablePath,
                searchBinding.ValueFieldId,
                writeBack: true,
                cancellationToken).ConfigureAwait(false);

            return new WatchBinding(
                latBinding.ComponentId,
                DynamicValueVariableValueMemberName,
                latAliasBinding.ComponentId,
                DynamicValueVariableValueMemberName,
                lonBinding.ComponentId,
                DynamicValueVariableValueMemberName,
                lonAliasBinding.ComponentId,
                DynamicValueVariableValueMemberName,
                rangeBinding.ComponentId,
                DynamicValueVariableValueMemberName,
                rangeAliasBinding.ComponentId,
                DynamicValueVariableValueMemberName,
                searchBinding.ComponentId,
                DynamicValueVariableValueMemberName,
                searchAliasBinding.ComponentId,
                DynamicValueVariableValueMemberName);
        }

        public async Task<SelectionInputValues?> ReadSelectionInputValuesAsync(WatchBinding binding, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(binding);

            float lat = await ReadNumericMemberAsFloatAsync(binding.LatitudeComponentId, binding.LatitudeValueMemberName, cancellationToken).ConfigureAwait(false);
            float lon = await ReadNumericMemberAsFloatAsync(binding.LongitudeComponentId, binding.LongitudeValueMemberName, cancellationToken).ConfigureAwait(false);
            float range = await ReadNumericMemberAsFloatAsync(binding.RangeComponentId, binding.RangeValueMemberName, cancellationToken).ConfigureAwait(false);

            if (!float.IsFinite(lat) || !float.IsFinite(lon) || !float.IsFinite(range))
            {
                return null;
            }

            return new SelectionInputValues(lat, lon, range);
        }

        public async Task<string?> ReadWatchSearchAsync(WatchBinding binding, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(binding);

            return await ReadStringMemberAsync(binding.SearchComponentId, binding.SearchValueMemberName, cancellationToken).ConfigureAwait(false);
        }

        public async Task UpdateWatchCoordinatesAsync(WatchBinding binding, double latitude, double longitude, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(binding);

            float lat = checked((float)latitude);
            float lon = checked((float)longitude);
            if (!float.IsFinite(lat) || !float.IsFinite(lon))
            {
                throw new InvalidOperationException("Watch coordinates must be finite values.");
            }

            await UpdateMirroredNumericMemberAsync(
                binding.LatitudeComponentId,
                binding.LatitudeValueMemberName,
                binding.LatitudeAliasComponentId,
                binding.LatitudeAliasValueMemberName,
                lat,
                cancellationToken).ConfigureAwait(false);
            await UpdateMirroredNumericMemberAsync(
                binding.LongitudeComponentId,
                binding.LongitudeValueMemberName,
                binding.LongitudeAliasComponentId,
                binding.LongitudeAliasValueMemberName,
                lon,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        public async Task SetSessionLicenseCreditAsync(string creditString, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_sessionLicenseComponentId) || string.IsNullOrWhiteSpace(creditString))
            {
                return;
            }

            string normalized = creditString.Trim();
            if (string.Equals(normalized, _sessionLicenseCreditText, StringComparison.Ordinal))
            {
                await TryUpdateAliasStringMemberAsync(
                    _sessionLicenseAliasComponentId,
                    DynamicValueVariableValueMemberName,
                    normalized,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            Response response = await ExecuteLinkRequestAsync(
                link => link.UpdateComponent(
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
                    }),
                cancellationToken).ConfigureAwait(false);
            _ = EnsureSuccess(response);
            _sessionLicenseCreditText = normalized;
            await TryUpdateAliasStringMemberAsync(
                _sessionLicenseAliasComponentId,
                DynamicValueVariableValueMemberName,
                normalized,
                cancellationToken).ConfigureAwait(false);
        }

        public async Task SetProgressAsync(string? parentSlotId, float progress01, string progressText, CancellationToken cancellationToken)
        {
            await SetProgressTextAsync(parentSlotId, progressText, cancellationToken).ConfigureAwait(false);
            await SetProgressValueAsync(parentSlotId, progress01, cancellationToken).ConfigureAwait(false);

            if (System.Math.Clamp(progress01, 0f, 1f) >= 1f)
            {
                await SetProgressTextAsync(parentSlotId, progressText, cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task SetProgressValueAsync(string? parentSlotId, float progress01, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            float normalizedProgress = System.Math.Clamp(progress01, 0f, 1f);
            string effectiveParentSlotId = ResolveEffectiveParentSlotId(parentSlotId);
            SlotProgressBinding binding = await EnsureProgressBindingAsync(effectiveParentSlotId).ConfigureAwait(false);
            await UpdateMirroredNumericMemberAsync(
                binding.ProgressValueComponentId,
                DynamicValueVariableValueMemberName,
                binding.ProgressValueAliasComponentId,
                DynamicValueVariableValueMemberName,
                normalizedProgress,
                cancellationToken).ConfigureAwait(false);
        }

        public async Task SetProgressTextAsync(string? parentSlotId, string progressText, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(progressText);
            cancellationToken.ThrowIfCancellationRequested();

            string effectiveParentSlotId = ResolveEffectiveParentSlotId(parentSlotId);
            string normalizedText = progressText.Trim();
            SlotProgressBinding binding = await EnsureProgressBindingAsync(effectiveParentSlotId).ConfigureAwait(false);
            await UpdateMirroredStringMemberAsync(
                binding.ProgressTextComponentId,
                DynamicValueVariableValueMemberName,
                binding.ProgressTextAliasComponentId,
                DynamicValueVariableValueMemberName,
                normalizedText,
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<string?> StreamPlacedMeshAsync(PlacedMeshPayload payload, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(payload);
            if (payload.Vertices.Count == 0 || payload.Indices.Count == 0)
            {
                return null;
            }

            ImportMeshRawData importMesh = BuildImportMesh(payload, cancellationToken);
            Task<AssetData> meshAssetTask = ImportMeshAssetAsync(importMesh, cancellationToken);
            Task<AssetData?> textureAssetTask = ImportTextureAssetAsync(
                payload.BaseColorTextureBytes,
                payload.BaseColorTextureExtension,
                cancellationToken);

            AssetData meshAsset = await meshAssetTask.ConfigureAwait(false);
            AssetData? textureAsset = await textureAssetTask.ConfigureAwait(false);

            await _streamPlacementGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
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
                    payload.SlotScale,
                    cancellationToken).ConfigureAwait(false);

                await AttachAvatarProtectionAsync(tileSlotId).ConfigureAwait(false);
                await AttachPackageExportableAsync(tileSlotId, cancellationToken).ConfigureAwait(false);

                string staticMeshId = await AddComponentAsync(
                    tileSlotId,
                    StaticMeshComponentType,
                    new Dictionary<string, Member>
                    {
                        ["URL"] = new Field_Uri { Value = meshAsset.AssetURL }
                    },
                    cancellationToken).ConfigureAwait(false);

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
                    },
                    cancellationToken).ConfigureAwait(false);

                var materialMembers = new Dictionary<string, Member>();
                Dictionary<string, MemberDefinition> materialMembersDefinition = await ResolveMaterialMemberDefinitionsAsync().ConfigureAwait(false);
                if (materialMembersDefinition.ContainsKey("Smoothness"))
                {
                    materialMembers["Smoothness"] = new Field_float { Value = 0f };
                }

                string? textureMemberName = await ResolveMaterialTextureMemberNameAsync().ConfigureAwait(false);
                if (textureAsset is not null && !string.IsNullOrWhiteSpace(textureMemberName))
                {
                    string staticTextureId = await AddComponentAsync(
                        tileSlotId,
                        StaticTextureComponentType,
                        new Dictionary<string, Member>
                        {
                            ["URL"] = new Field_Uri { Value = textureAsset.AssetURL }
                        },
                        cancellationToken).ConfigureAwait(false);

                    materialMembers[textureMemberName] = new Reference
                    {
                        TargetType = TextureAssetProviderType,
                        TargetID = staticTextureId
                    };
                }

                string materialId = await AddComponentAsync(tileSlotId, MaterialComponentType, materialMembers, cancellationToken).ConfigureAwait(false);

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
                    },
                    cancellationToken).ConfigureAwait(false);
                return tileSlotId;
            }
            finally
            {
                _ = _streamPlacementGate.Release();
            }
        }

        private static ImportMeshRawData BuildImportMesh(PlacedMeshPayload payload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var triangleSubmesh = new TriangleSubmeshRawData
            {
                TriangleCount = payload.Indices.Count / 3
            };

            bool hasNormals = payload.HasNormals && payload.Normals is { Count: > 0 } normals && normals.Count == payload.Vertices.Count;
            bool hasTangents = payload.HasTangents && payload.Tangents is { Count: > 0 } tangents && tangents.Count == payload.Vertices.Count;

            var importMesh = new ImportMeshRawData
            {
                VertexCount = payload.Vertices.Count,
                HasNormals = hasNormals,
                HasTangents = hasTangents,
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

            if (hasNormals)
            {
                Span<float3> normalSpan = importMesh.Normals;
                for (int i = 0; i < payload.Vertices.Count; i++)
                {
                    Vector3 normal = payload.Normals![i];
                    normalSpan[i] = new float3 { x = normal.X, y = normal.Y, z = normal.Z };
                }
            }

            if (hasTangents)
            {
                Span<float4> tangentSpan = importMesh.Tangents;
                for (int i = 0; i < payload.Vertices.Count; i++)
                {
                    Vector4 tangent = payload.Tangents![i];
                    tangentSpan[i] = new float4 { x = tangent.X, y = tangent.Y, z = tangent.Z, w = tangent.W };
                }
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

            return importMesh;
        }

        private async Task<AssetData> ImportMeshAssetAsync(ImportMeshRawData importMesh, CancellationToken cancellationToken)
        {
            if (_dumpMeshJson)
            {
                _ = Directory.CreateDirectory(_meshDumpDir);
                byte[] dumpBytes = JsonSerializer.SerializeToUtf8Bytes(importMesh, LinkInterface.SerializationOptions);
                string path = Path.Combine(_meshDumpDir, $"mesh_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.json");
                await File.WriteAllBytesAsync(path, dumpBytes, cancellationToken).ConfigureAwait(false);
            }

            return EnsureSuccess(await _assetImportPool.ImportMeshAsync(
                importMesh,
                MeshImportRequestTimeout,
                cancellationToken).ConfigureAwait(false));
        }

        public async Task RemoveSlotAsync(string slotId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(slotId))
            {
                return;
            }

            _ = _progressBindingsByParentSlotId.Remove(slotId);
            Response response = await ExecuteLinkRequestAsync(
                link => link.RemoveSlot(new RemoveSlot { SlotID = slotId }),
                cancellationToken).ConfigureAwait(false);
            _ = EnsureSuccess(response);
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectCoreAsync(CancellationToken.None).ConfigureAwait(false);
            _connectionGate.Dispose();
            _streamPlacementGate.Dispose();
            await _assetImportPool.DisposeAsync().ConfigureAwait(false);
        }

        private async Task DisconnectCoreAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Log.ClosingSession(_logger);
                await _assetImportPool.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                await CleanupTemporaryFilesAsync().ConfigureAwait(false);
                DisposeDirectLink(clearSessionState: false);
            }
            finally
            {
                _ = _connectionGate.Release();
            }
        }

        private async Task<string> CreateSlotAsync(
            string name,
            string? parentSlotId,
            Vector3? position = null,
            Quaternion? rotation = null,
            Vector3? scale = null,
            CancellationToken cancellationToken = default)
        {
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

            NewEntityId response = EnsureSuccess(await ExecuteLinkRequestAsync(
                link => link.AddSlot(new AddSlot { Data = slot }),
                cancellationToken).ConfigureAwait(false));
            return response.EntityId;
        }

        private async Task<string> AddComponentAsync(
            string slotId,
            string componentType,
            Dictionary<string, Member> members,
            CancellationToken cancellationToken = default)
        {
            NewEntityId response = EnsureSuccess(await ExecuteLinkRequestAsync(
                link => link.AddComponent(
                    new AddComponent
                    {
                        ContainerSlotId = slotId,
                        Data = new Component
                        {
                            ID = $"t3dtile_comp_{Guid.NewGuid():N}",
                            ComponentType = componentType,
                            Members = members
                        }
                    }),
                cancellationToken).ConfigureAwait(false));
            return response.EntityId;
        }

        private async Task<string?> TryAddComponentAsync(
            string slotId,
            string componentType,
            Dictionary<string, Member> members,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await AddComponentAsync(slotId, componentType, members, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            catch (ResoniteLinkNoResponseException)
            {
                return null;
            }
            catch (ResoniteLinkDisconnectedException)
            {
                return null;
            }
            catch (TimeoutException)
            {
                return null;
            }
            catch (WebSocketException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private async Task<DynamicVariableBinding> AddDynamicFloatValueVariableAsync(
            string slotId,
            string variablePath,
            float initialValue,
            CancellationToken cancellationToken = default)
        {
            string valueFieldId = $"t3dtile_field_{Guid.NewGuid():N}";
            string componentId = await AddComponentAsync(
                slotId,
                DynamicValueVariableFloatComponentType,
                BuildDynamicFloatValueVariableMembers(variablePath, valueFieldId, initialValue),
                cancellationToken).ConfigureAwait(false);
            return new DynamicVariableBinding(componentId, valueFieldId);
        }

        private async Task<DynamicVariableBinding?> TryAddDynamicFloatValueVariableAsync(
            string slotId,
            string variablePath,
            float initialValue,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await AddDynamicFloatValueVariableAsync(slotId, variablePath, initialValue, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            catch (ResoniteLinkNoResponseException)
            {
                return null;
            }
            catch (ResoniteLinkDisconnectedException)
            {
                return null;
            }
            catch (TimeoutException)
            {
                return null;
            }
            catch (WebSocketException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private async Task<DynamicVariableBinding> AddDynamicStringValueVariableAsync(
            string slotId,
            string variablePath,
            string initialValue,
            CancellationToken cancellationToken = default)
        {
            string valueFieldId = $"t3dtile_field_{Guid.NewGuid():N}";
            string componentId = await AddComponentAsync(
                slotId,
                DynamicValueVariableStringComponentType,
                BuildDynamicStringValueVariableMembers(variablePath, valueFieldId, initialValue),
                cancellationToken).ConfigureAwait(false);
            return new DynamicVariableBinding(componentId, valueFieldId);
        }

        private async Task<DynamicVariableBinding?> TryAddDynamicStringValueVariableAsync(
            string slotId,
            string variablePath,
            string initialValue,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await AddDynamicStringValueVariableAsync(slotId, variablePath, initialValue, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            catch (ResoniteLinkNoResponseException)
            {
                return null;
            }
            catch (ResoniteLinkDisconnectedException)
            {
                return null;
            }
            catch (TimeoutException)
            {
                return null;
            }
            catch (WebSocketException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private async Task<DynamicVariableBinding> AddWorldFloatAliasAsync(
            string slotId,
            string variablePath,
            string sourceFieldId,
            bool writeBack,
            CancellationToken cancellationToken = default)
        {
            DynamicVariableBinding aliasBinding = await AddDynamicFloatValueVariableAsync(
                slotId,
                variablePath,
                0f,
                cancellationToken).ConfigureAwait(false);
            _ = await AddComponentAsync(
                slotId,
                ValueCopyFloatComponentType,
                BuildValueCopyMembers(sourceFieldId, aliasBinding.ValueFieldId, FloatFieldType, writeBack),
                cancellationToken).ConfigureAwait(false);
            return aliasBinding;
        }

        private async Task<DynamicVariableBinding?> TryAddWorldFloatAliasAsync(
            string slotId,
            string variablePath,
            string sourceFieldId,
            bool writeBack,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await AddWorldFloatAliasAsync(slotId, variablePath, sourceFieldId, writeBack, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            catch (ResoniteLinkNoResponseException)
            {
                return null;
            }
            catch (ResoniteLinkDisconnectedException)
            {
                return null;
            }
            catch (TimeoutException)
            {
                return null;
            }
            catch (WebSocketException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private async Task<DynamicVariableBinding> AddWorldStringAliasAsync(
            string slotId,
            string variablePath,
            string sourceFieldId,
            bool writeBack,
            CancellationToken cancellationToken = default)
        {
            DynamicVariableBinding aliasBinding = await AddDynamicStringValueVariableAsync(
                slotId,
                variablePath,
                string.Empty,
                cancellationToken).ConfigureAwait(false);
            _ = await AddComponentAsync(
                slotId,
                ValueCopyStringComponentType,
                BuildValueCopyMembers(sourceFieldId, aliasBinding.ValueFieldId, StringFieldType, writeBack),
                cancellationToken).ConfigureAwait(false);
            return aliasBinding;
        }

        private async Task<DynamicVariableBinding?> TryAddWorldStringAliasAsync(
            string slotId,
            string variablePath,
            string sourceFieldId,
            bool writeBack,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await AddWorldStringAliasAsync(slotId, variablePath, sourceFieldId, writeBack, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            catch (ResoniteLinkNoResponseException)
            {
                return null;
            }
            catch (ResoniteLinkDisconnectedException)
            {
                return null;
            }
            catch (TimeoutException)
            {
                return null;
            }
            catch (WebSocketException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private async Task<float> ReadNumericMemberAsFloatAsync(string componentId, string memberName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Member member = await ReadComponentMemberAsync(componentId, memberName, cancellationToken).ConfigureAwait(false);
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
            Member member = await ReadComponentMemberAsync(componentId, memberName, cancellationToken).ConfigureAwait(false);
            return member is Field_bool boolMember
                ? boolMember.Value
                : throw new InvalidOperationException(
                    $"Unsupported boolean member type: componentId={componentId} member={memberName} type={member.GetType().Name}");
        }

        private async Task<string> ReadStringMemberAsync(string componentId, string memberName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Member member = await ReadComponentMemberAsync(componentId, memberName, cancellationToken).ConfigureAwait(false);
            return member is Field_string stringMember
                ? stringMember.Value
                : throw new InvalidOperationException(
                    $"Unsupported string member type: componentId={componentId} member={memberName} type={member.GetType().Name}");
        }

        private async Task UpdateNumericMemberAsync(string componentId, string memberName, float value, CancellationToken cancellationToken = default)
        {
            Response response = await ExecuteLinkRequestAsync(
                link => link.UpdateComponent(
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
                    }),
                cancellationToken).ConfigureAwait(false);
            _ = EnsureSuccess(response);
        }

        private async Task UpdateMirroredNumericMemberAsync(
            string sourceComponentId,
            string sourceMemberName,
            string? aliasComponentId,
            string aliasMemberName,
            float value,
            CancellationToken cancellationToken = default)
        {
            await UpdateNumericMemberAsync(sourceComponentId, sourceMemberName, value, cancellationToken).ConfigureAwait(false);
            await TryUpdateAliasNumericMemberAsync(aliasComponentId, aliasMemberName, value, cancellationToken).ConfigureAwait(false);
        }

        private async Task TryUpdateAliasNumericMemberAsync(
            string? componentId,
            string memberName,
            float value,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(componentId))
            {
                return;
            }

            try
            {
                await UpdateNumericMemberAsync(componentId, memberName, value, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ObjectDisposedException ex)
            {
                Log.MirroredNumericAliasUpdateFailed(_logger, ex, componentId);
            }
            catch (ResoniteLinkNoResponseException ex)
            {
                Log.MirroredNumericAliasUpdateFailed(_logger, ex, componentId);
            }
            catch (ResoniteLinkDisconnectedException ex)
            {
                Log.MirroredNumericAliasUpdateFailed(_logger, ex, componentId);
            }
            catch (TimeoutException ex)
            {
                Log.MirroredNumericAliasUpdateFailed(_logger, ex, componentId);
            }
            catch (WebSocketException ex)
            {
                Log.MirroredNumericAliasUpdateFailed(_logger, ex, componentId);
            }
            catch (InvalidOperationException ex)
            {
                Log.MirroredNumericAliasUpdateFailed(_logger, ex, componentId);
            }
        }

        private async Task UpdateStringMemberAsync(string componentId, string memberName, string value, CancellationToken cancellationToken = default)
        {
            Response response = await ExecuteLinkRequestAsync(
                link => link.UpdateComponent(
                    new UpdateComponent
                    {
                        Data = new Component
                        {
                            ID = componentId,
                            Members = new Dictionary<string, Member>(StringComparer.Ordinal)
                            {
                                [memberName] = new Field_string { Value = value }
                            }
                        }
                    }),
                cancellationToken).ConfigureAwait(false);
            _ = EnsureSuccess(response);
        }

        private async Task UpdateMirroredStringMemberAsync(
            string sourceComponentId,
            string sourceMemberName,
            string? aliasComponentId,
            string aliasMemberName,
            string value,
            CancellationToken cancellationToken = default)
        {
            await UpdateStringMemberAsync(sourceComponentId, sourceMemberName, value, cancellationToken).ConfigureAwait(false);
            await TryUpdateAliasStringMemberAsync(aliasComponentId, aliasMemberName, value, cancellationToken).ConfigureAwait(false);
        }

        private async Task TryUpdateAliasStringMemberAsync(
            string? componentId,
            string memberName,
            string value,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(componentId))
            {
                return;
            }

            try
            {
                await UpdateStringMemberAsync(componentId, memberName, value, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ObjectDisposedException ex)
            {
                Log.MirroredStringAliasUpdateFailed(_logger, ex, componentId);
            }
            catch (ResoniteLinkNoResponseException ex)
            {
                Log.MirroredStringAliasUpdateFailed(_logger, ex, componentId);
            }
            catch (ResoniteLinkDisconnectedException ex)
            {
                Log.MirroredStringAliasUpdateFailed(_logger, ex, componentId);
            }
            catch (TimeoutException ex)
            {
                Log.MirroredStringAliasUpdateFailed(_logger, ex, componentId);
            }
            catch (WebSocketException ex)
            {
                Log.MirroredStringAliasUpdateFailed(_logger, ex, componentId);
            }
            catch (InvalidOperationException ex)
            {
                Log.MirroredStringAliasUpdateFailed(_logger, ex, componentId);
            }
        }

        private async Task<Member> ReadComponentMemberAsync(string componentId, string memberName, CancellationToken cancellationToken)
        {
            for (int attempt = 1; attempt <= ReadComponentMemberMaxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    ComponentData componentData = EnsureSuccess(await ExecuteLinkRequestAsync(
                        link => link.GetComponentData(new GetComponent { ComponentID = componentId }),
                        cancellationToken).ConfigureAwait(false));

                    if (componentData.Data is null || !componentData.Data.Members.TryGetValue(memberName, out Member? member))
                    {
                        throw new InvalidOperationException($"Component member not found: componentId={componentId} member={memberName}");
                    }

                    return member;
                }
                catch (ResoniteLinkNoResponseException) when (attempt < ReadComponentMemberMaxAttempts)
                {
                    await Task.Delay(ReadComponentMemberRetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }

            throw new ResoniteLinkNoResponseException();
        }

        private async Task<AssetData?> ImportTextureAssetAsync(byte[]? textureBytes, string? extension, CancellationToken cancellationToken)
        {
            if (textureBytes is null || textureBytes.Length == 0)
            {
                return null;
            }

            _ = Directory.CreateDirectory(_textureTempDir);

            string hash = Convert.ToHexString(SHA256.HashData(textureBytes));
            string ext = NormalizeExtension(extension);
            string path = Path.Combine(_textureTempDir, $"{hash}{ext}");
            SemaphoreSlim textureFileLock = _textureFileLocks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
            bool lockTaken = false;

            await textureFileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            lockTaken = true;
            try
            {
                if (!File.Exists(path))
                {
                    await File.WriteAllBytesAsync(path, textureBytes, cancellationToken).ConfigureAwait(false);
                }

                _ = _tempTextureFiles.TryAdd(path, 0);

                return EnsureSuccess(await _assetImportPool.ImportTextureAsync(
                    path,
                    DefaultLinkRequestTimeout,
                    cancellationToken).ConfigureAwait(false));
            }
            finally
            {
                if (lockTaken)
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            File.Delete(path);
                        }
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }

                    _ = _tempTextureFiles.TryRemove(path, out _);
                    _ = textureFileLock.Release();
                }
            }
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
            catch (ObjectDisposedException)
            {
                return null;
            }
            catch (ResoniteLinkNoResponseException)
            {
                return null;
            }
            catch (ResoniteLinkDisconnectedException)
            {
                return null;
            }
            catch (TimeoutException)
            {
                return null;
            }
            catch (WebSocketException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private async Task AttachSessionMetadataAsync(string sessionRootSlotId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureSessionDynamicSpaceAsync(sessionRootSlotId, cancellationToken).ConfigureAwait(false);
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
                },
                cancellationToken).ConfigureAwait(false);

            if (licenseComponentId is null)
            {
                return;
            }

            _sessionLicenseComponentId = licenseComponentId;
            _sessionLicenseCreditText = DefaultGoogleMapsCreditText;

            DynamicVariableBinding? licenseAliasBinding = await TryAddWorldStringAliasAsync(
                sessionRootSlotId,
                LicenseDynamicVariablePath,
                creditFieldId,
                writeBack: false,
                cancellationToken).ConfigureAwait(false);
            _sessionLicenseAliasComponentId = licenseAliasBinding?.ComponentId;
        }

        private async Task EnsureSessionDynamicSpaceAsync(string sessionRootSlotId, CancellationToken cancellationToken = default)
        {
            if (_sessionDynamicSpaceInitialized)
            {
                return;
            }

            _ = await TryAddComponentAsync(
                sessionRootSlotId,
                DynamicVariableSpaceComponentType,
                new Dictionary<string, Member>
                {
                    ["SpaceName"] = new Field_string { Value = GoogleTilesDynamicSpaceName },
                    ["OnlyDirectBinding"] = new Field_bool { Value = true }
                },
                cancellationToken).ConfigureAwait(false);
            _sessionDynamicSpaceInitialized = true;
        }

        private async Task<SlotProgressBinding> EnsureProgressBindingAsync(string parentSlotId)
        {
            if (_progressBindingsByParentSlotId.TryGetValue(parentSlotId, out SlotProgressBinding? existingBinding))
            {
                return existingBinding;
            }

            _ = await TryAddComponentAsync(
                parentSlotId,
                DynamicVariableSpaceComponentType,
                new Dictionary<string, Member>
                {
                    ["SpaceName"] = new Field_string { Value = BuildParentDynamicSpaceName(parentSlotId) },
                    ["OnlyDirectBinding"] = new Field_bool { Value = true }
                }).ConfigureAwait(false);

            DynamicVariableBinding? progressValueBinding = await TryAddDynamicFloatValueVariableAsync(
                parentSlotId,
                BuildScopedVariablePath(BuildParentDynamicSpaceName(parentSlotId), ProgressValueVariableLocalName),
                0f).ConfigureAwait(false);

            DynamicVariableBinding? progressTextBinding = await TryAddDynamicStringValueVariableAsync(
                parentSlotId,
                BuildScopedVariablePath(BuildParentDynamicSpaceName(parentSlotId), ProgressTextVariableLocalName),
                string.Empty).ConfigureAwait(false);

            if (progressValueBinding is null || progressTextBinding is null)
            {
                throw new InvalidOperationException($"Failed to create progress dynamic variable for slot {parentSlotId}.");
            }

            DynamicVariableBinding? progressValueAliasBinding = await TryAddWorldFloatAliasAsync(
                parentSlotId,
                BuildWorldProgressPath(),
                progressValueBinding.ValueFieldId,
                writeBack: false).ConfigureAwait(false);

            DynamicVariableBinding? progressTextAliasBinding = await TryAddWorldStringAliasAsync(
                parentSlotId,
                BuildWorldProgressTextPath(),
                progressTextBinding.ValueFieldId,
                writeBack: false).ConfigureAwait(false);

            var binding = new SlotProgressBinding(
                progressValueBinding.ComponentId,
                progressTextBinding.ComponentId,
                progressValueAliasBinding?.ComponentId,
                progressTextAliasBinding?.ComponentId);
            _progressBindingsByParentSlotId[parentSlotId] = binding;
            return binding;
        }

        private async Task ResolveAvatarProtectionContextAsync()
        {
            if (_avatarProtectionUnavailable)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(_avatarProtectionComponentType))
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
                protectionContext = await ResolveComponentDefinitionAsync([SimpleAvatarProtectionComponentType]).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                _avatarProtectionUnavailable = true;
                return;
            }

            if (!protectionContext.protectionMembers.TryGetValue(SimpleAvatarProtectionUserMemberName, out MemberDefinition? userMemberDefinition) ||
                userMemberDefinition is not SyncObjectMemberDefinition userSyncObjectDefinition ||
                !string.Equals(userSyncObjectDefinition.Type?.Type, SimpleAvatarProtectionCloudUserRefType, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Unexpected {SimpleAvatarProtectionComponentType}.{SimpleAvatarProtectionUserMemberName} definition.");
            }

            if (!protectionContext.protectionMembers.TryGetValue(SimpleAvatarProtectionReassignMemberName, out MemberDefinition? reassignMemberDefinition) ||
                reassignMemberDefinition is not FieldDefinition reassignFieldDefinition ||
                !string.Equals(reassignFieldDefinition.ValueType?.Type, "bool", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Unexpected {SimpleAvatarProtectionComponentType}.{SimpleAvatarProtectionReassignMemberName} definition.");
            }

            _avatarProtectionComponentType = protectionContext.protectionComponentType;
        }

        private async Task AttachAvatarProtectionAsync(string slotId)
        {
            if (string.IsNullOrWhiteSpace(slotId))
            {
                throw new InvalidOperationException("Target slot is not initialized.");
            }

            await ResolveAvatarProtectionContextAsync().ConfigureAwait(false);
            if (_avatarProtectionUnavailable ||
                string.IsNullOrWhiteSpace(_avatarProtectionComponentType))
            {
                return;
            }

            _ = await AddComponentAsync(
                slotId,
                _avatarProtectionComponentType,
                BuildAvatarProtectionMembers()).ConfigureAwait(false);
        }

        private static Dictionary<string, Member> BuildAvatarProtectionMembers()
        {
            return new Dictionary<string, Member>(StringComparer.Ordinal)
            {
                [SimpleAvatarProtectionReassignMemberName] = new Field_bool { Value = false }
            };
        }

        private async Task AttachPackageExportableAsync(string slotId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(slotId))
            {
                throw new InvalidOperationException("Target slot is not initialized.");
            }

            string warningSlotId = await EnsurePackageExportWarningSlotAsync(cancellationToken).ConfigureAwait(false);
            _ = await AddComponentAsync(
                slotId,
                PackageExportableComponentType,
                BuildPackageExportableMembers(warningSlotId),
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<string> EnsurePackageExportWarningSlotAsync(CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(_packageExportWarningSlotId))
            {
                return _packageExportWarningSlotId;
            }

            if (string.IsNullOrWhiteSpace(_sessionRootSlotId))
            {
                throw new InvalidOperationException("Session root slot is not initialized.");
            }

            _packageExportWarningSlotId = await CreateSlotAsync(
                PackageExportWarningSlotName,
                _sessionRootSlotId,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return _packageExportWarningSlotId;
        }

        private static Dictionary<string, Member> BuildPackageExportableMembers(string warningSlotId)
        {
            if (string.IsNullOrWhiteSpace(warningSlotId))
            {
                throw new InvalidOperationException("Package export warning slot is not initialized.");
            }

            return new Dictionary<string, Member>(StringComparer.Ordinal)
            {
                [PackageExportableRootMemberName] = new Reference
                {
                    TargetID = warningSlotId,
                    TargetType = SlotWorkerType
                }
            };
        }

        private async Task<(string ComponentType, Dictionary<string, MemberDefinition> Members)> ResolveComponentDefinitionAsync(IEnumerable<string> componentTypeCandidates)
        {
            foreach (string componentType in componentTypeCandidates)
            {
                try
                {
                    ComponentDefinitionData definition = EnsureSuccess(await ExecuteLinkRequestAsync(
                        link => link.GetComponentDefinition(componentType, flattened: true),
                        CancellationToken.None).ConfigureAwait(false));
                    return (componentType, definition.Definition?.Members ?? new Dictionary<string, MemberDefinition>(StringComparer.Ordinal));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (ObjectDisposedException)
                {
                }
                catch (ResoniteLinkNoResponseException)
                {
                }
                catch (ResoniteLinkDisconnectedException)
                {
                }
                catch (TimeoutException)
                {
                }
                catch (WebSocketException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }

            throw new InvalidOperationException($"No supported component type was found. Candidates: {string.Join(", ", componentTypeCandidates)}");
        }

        private async Task<Dictionary<string, MemberDefinition>> ResolveMaterialMemberDefinitionsAsync()
        {
            if (_materialMemberDefinitions is not null)
            {
                return _materialMemberDefinitions;
            }

            ComponentDefinitionData definition = EnsureSuccess(await ExecuteLinkRequestAsync(
                link => link.GetComponentDefinition(MaterialComponentType, flattened: true),
                CancellationToken.None).ConfigureAwait(false));
            _materialMemberDefinitions = definition.Definition?.Members ?? new Dictionary<string, MemberDefinition>(StringComparer.Ordinal);
            return _materialMemberDefinitions;
        }

        private async Task RemoveComponentAsync(string componentId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(componentId))
            {
                return;
            }

            Response response = await ExecuteLinkRequestAsync(
                link => link.RemoveComponent(new RemoveComponent { ComponentID = componentId }),
                cancellationToken).ConfigureAwait(false);
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

        private static string BuildParentDynamicSpaceName(string parentSlotId)
        {
            return $"{ParentDynamicSpaceNamePrefix}.{parentSlotId}";
        }

        private static string BuildWorldProgressPath()
        {
            return ProgressDynamicVariablePath;
        }

        private static string BuildWorldProgressTextPath()
        {
            return ProgressTextDynamicVariablePath;
        }

        private static string BuildSessionVariablePath(string spaceName, string worldVariablePath)
        {
            const string worldPrefix = "World/";
            string localPath = worldVariablePath.StartsWith(worldPrefix, StringComparison.Ordinal)
                ? worldVariablePath[worldPrefix.Length..]
                : worldVariablePath;
            return BuildScopedVariablePath(spaceName, localPath);
        }

        private static string BuildScopedVariablePath(string spaceName, string variableName)
        {
            return $"{spaceName}/{variableName}";
        }

        private static Dictionary<string, Member> BuildDynamicFloatValueVariableMembers(
            string variablePath,
            string valueFieldId,
            float initialValue)
        {
            return new Dictionary<string, Member>(StringComparer.Ordinal)
            {
                ["VariableName"] = new Field_string { Value = variablePath },
                [DynamicValueVariableValueMemberName] = new Field_float
                {
                    ID = valueFieldId,
                    Value = initialValue
                }
            };
        }

        private static Dictionary<string, Member> BuildDynamicStringValueVariableMembers(
            string variablePath,
            string valueFieldId,
            string initialValue)
        {
            return new Dictionary<string, Member>(StringComparer.Ordinal)
            {
                ["VariableName"] = new Field_string { Value = variablePath },
                [DynamicValueVariableValueMemberName] = new Field_string
                {
                    ID = valueFieldId,
                    Value = initialValue
                }
            };
        }

        private static Dictionary<string, Member> BuildValueCopyMembers(
            string sourceFieldId,
            string targetFieldId,
            string fieldType,
            bool writeBack)
        {
            return new Dictionary<string, Member>(StringComparer.Ordinal)
            {
                ["Source"] = new Reference
                {
                    TargetID = sourceFieldId,
                    TargetType = fieldType
                },
                ["Target"] = new Reference
                {
                    TargetID = targetFieldId,
                    TargetType = fieldType
                },
                ["WriteBack"] = new Field_bool { Value = writeBack }
            };
        }

        private static T EnsureSuccess<T>(T? response) where T : Response
        {
            return response is null
                ? throw new ResoniteLinkNoResponseException()
                : !response.Success
                ? throw new InvalidOperationException($"ResoniteLink request failed: {response.ErrorInfo}")
                : response;
        }

        private async Task<T> ExecuteLinkRequestAsync<T>(
            Func<LinkInterface, Task<T>> operation,
            CancellationToken cancellationToken,
            TimeSpan? timeout = null,
            bool allowReconnect = true)
        {
            ArgumentNullException.ThrowIfNull(operation);

            for (int attempt = 1; attempt <= LinkRequestMaxAttempts; attempt++)
            {
                LinkInterface link = await EnsureConnectedLinkAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await WaitForLinkRequestAsync(
                        () => operation(link),
                        timeout ?? DefaultLinkRequestTimeout,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                    when (allowReconnect && attempt < LinkRequestMaxAttempts && IsRecoverableTransportFailure(ex))
                {
                    await ReconnectTransportAsync(ex, cancellationToken).ConfigureAwait(false);
                }
                catch (ObjectDisposedException ex)
                    when (allowReconnect && attempt < LinkRequestMaxAttempts && IsRecoverableTransportFailure(ex))
                {
                    await ReconnectTransportAsync(ex, cancellationToken).ConfigureAwait(false);
                }
                catch (WebSocketException ex)
                    when (allowReconnect && attempt < LinkRequestMaxAttempts && IsRecoverableTransportFailure(ex))
                {
                    await ReconnectTransportAsync(ex, cancellationToken).ConfigureAwait(false);
                }
                catch (ResoniteLinkNoResponseException ex)
                    when (allowReconnect && attempt < LinkRequestMaxAttempts && IsRecoverableTransportFailure(ex))
                {
                    await ReconnectTransportAsync(ex, cancellationToken).ConfigureAwait(false);
                }
                catch (ResoniteLinkDisconnectedException ex)
                    when (allowReconnect && attempt < LinkRequestMaxAttempts && IsRecoverableTransportFailure(ex))
                {
                    await ReconnectTransportAsync(ex, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                    when (allowReconnect && attempt < LinkRequestMaxAttempts && IsRecoverableTransportFailure(ex))
                {
                    await ReconnectTransportAsync(ex, cancellationToken).ConfigureAwait(false);
                }
            }

            throw new InvalidOperationException("ResoniteLink request retry loop exited unexpectedly.");
        }

        private static async Task<T> WaitForLinkRequestAsync<T>(
            Func<Task<T>> operation,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);

            try
            {
                return await operation().WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
            {
                throw new TimeoutException($"ResoniteLink request timed out after {timeout}.", ex);
            }
        }

        private async Task<LinkInterface> EnsureConnectedLinkAsync(CancellationToken cancellationToken)
        {
            if (_linkInterface.IsConnected)
            {
                return _linkInterface;
            }

            if (_connectionUri is null)
            {
                throw new ResoniteLinkDisconnectedException();
            }

            await ReconnectTransportAsync(reason: null, cancellationToken).ConfigureAwait(false);
            if (_linkInterface.IsConnected)
            {
                return _linkInterface;
            }

            throw new ResoniteLinkDisconnectedException();
        }

        private async Task ReconnectTransportAsync(Exception? reason, CancellationToken cancellationToken)
        {
            if (_connectionUri is null)
            {
                throw new ResoniteLinkDisconnectedException(reason);
            }

            await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_linkInterface.IsConnected)
                {
                    return;
                }

                if (reason is null)
                {
                    Log.ReconnectingSession(
                        _logger,
                        _connectionUri.Host,
                        _connectionUri.Port);
                }
                else
                {
                    Log.ReconnectingSession(
                        _logger,
                        _connectionUri.Host,
                        _connectionUri.Port,
                        reason);
                }

                DisposeDirectLink(clearSessionState: false);
                await ConnectTransportAsync(_connectionUri, cancellationToken).ConfigureAwait(false);
                Log.ReconnectedTransport(_logger, _connectionUri.Host, _connectionUri.Port);
            }
            finally
            {
                _ = _connectionGate.Release();
            }
        }

        private async Task ConnectTransportAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            try
            {
                await _linkInterface.Connect(endpoint, cancellationToken).ConfigureAwait(false);
                _directClientInitialized = true;
            }
            catch (TimeoutException)
            {
                DisposeDirectLink(clearSessionState: false);
                throw;
            }
            catch (OperationCanceledException)
            {
                DisposeDirectLink(clearSessionState: false);
                throw;
            }
            catch (ObjectDisposedException)
            {
                DisposeDirectLink(clearSessionState: false);
                throw;
            }
            catch (WebSocketException)
            {
                DisposeDirectLink(clearSessionState: false);
                throw;
            }
            catch (InvalidOperationException)
            {
                DisposeDirectLink(clearSessionState: false);
                throw;
            }
        }

        private bool MatchesEndpoint(Uri endpoint)
        {
            return _connectionUri is not null &&
                Uri.Compare(_connectionUri, endpoint, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;
        }

        private static bool IsRecoverableTransportFailure(Exception exception)
        {
            return exception switch
            {
                TimeoutException => true,
                ObjectDisposedException => true,
                WebSocketException => true,
                ResoniteLinkNoResponseException => true,
                ResoniteLinkDisconnectedException => true,
                OperationCanceledException => false,
                _ when exception.InnerException is not null => IsRecoverableTransportFailure(exception.InnerException),
                _ => false
            };
        }

        private string ResolveEffectiveParentSlotId(string? parentSlotId)
        {
            string? effectiveParentSlotId = string.IsNullOrWhiteSpace(parentSlotId)
                ? _sessionRootSlotId
                : parentSlotId;

            return string.IsNullOrWhiteSpace(effectiveParentSlotId)
                ? throw new InvalidOperationException("Session root slot is not initialized.")
                : effectiveParentSlotId;
        }

        private void DisposeDirectLink(bool clearSessionState)
        {
            try
            {
                if (_directClientInitialized)
                {
                    _linkInterface.Dispose();
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                _directClientInitialized = false;
                _linkInterface = _linkInterfaceFactory();
                if (clearSessionState)
                {
                    _connectionUri = null;
                    ResetSessionState();
                }
            }
        }

        private async Task CleanupTemporaryFilesAsync()
        {
            foreach (string textureFile in _tempTextureFiles.Keys)
            {
                SemaphoreSlim? textureFileLock = null;
                bool lockTaken = false;

                if (_textureFileLocks.TryGetValue(textureFile, out SemaphoreSlim? trackedLock))
                {
                    textureFileLock = trackedLock;
                    await textureFileLock.WaitAsync().ConfigureAwait(false);
                    lockTaken = true;
                }

                try
                {
                    if (File.Exists(textureFile))
                    {
                        File.Delete(textureFile);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                finally
                {
                    _ = _tempTextureFiles.TryRemove(textureFile, out _);

                    if (lockTaken && textureFileLock is not null)
                    {
                        _ = textureFileLock.Release();
                    }
                }
            }

            _tempTextureFiles.Clear();
            _textureFileLocks.Clear();
        }

        private void ResetSessionState()
        {
            _sessionRootSlotId = null;
            _sessionLicenseComponentId = null;
            _sessionLicenseCreditText = null;
            _sessionLicenseAliasComponentId = null;
            _materialTextureFieldName = null;
            _materialTextureFieldResolved = false;
            _materialMemberDefinitions = null;
            _avatarProtectionComponentType = null;
            _avatarProtectionUnavailable = false;
            _packageExportWarningSlotId = null;
            _sessionDynamicSpaceInitialized = false;
            _progressBindingsByParentSlotId.Clear();
        }

        private sealed record SlotProgressBinding(
            string ProgressValueComponentId,
            string ProgressTextComponentId,
            string? ProgressValueAliasComponentId,
            string? ProgressTextAliasComponentId);
        private sealed record DynamicVariableBinding(string ComponentId, string ValueFieldId);
    }
}
