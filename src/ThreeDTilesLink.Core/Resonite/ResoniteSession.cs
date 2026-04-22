using System.Numerics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using ResoniteLink;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Resonite
{
    internal sealed partial class ResoniteSession(
        LinkInterface resoniteLink,
        ILicenseCreditPolicy licenseCreditPolicy,
        ResoniteDestinationPolicyOptions destinationPolicyOptions,
        ILogger<ResoniteSession> logger,
        Func<LinkInterface>? linkInterfaceFactory = null,
        int assetImportWorkers = 1) : IResoniteSession, IResoniteSessionMetadataPort, IAsyncDisposable
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
        private const string InteractiveLatitudeVariableLocalName = "Latitude";
        private const string InteractiveLongitudeVariableLocalName = "Longitude";
        private const string InteractiveRangeVariableLocalName = "Range";
        private const string InteractiveSearchVariableLocalName = "Search";
        private const string InteractiveLatitudeAliasPath = "World/ThreeDTilesLink.Latitude";
        private const string InteractiveLongitudeAliasPath = "World/ThreeDTilesLink.Longitude";
        private const string InteractiveRangeAliasPath = "World/ThreeDTilesLink.Range";
        private const string InteractiveSearchAliasPath = "World/ThreeDTilesLink.Search";
        private const string ProgressValueVariableLocalName = "Progress";
        private const string ProgressTextVariableLocalName = "ProgressText";
        private const string ProgressDynamicVariablePath = "World/ThreeDTilesLink.Progress";
        private const string ProgressTextDynamicVariablePath = "World/ThreeDTilesLink.ProgressText";
        private const string ParentDynamicSpaceNamePrefix = "ThreeDTilesLink.Parent";
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

        internal sealed class TestHooks
        {
            public Func<ImportMeshRawData, CancellationToken, Task<AssetData>>? ImportMeshAssetAsync { get; set; }
            public Func<byte[]?, string?, CancellationToken, Task<AssetData?>>? ImportTextureAssetAsync { get; set; }
            public Func<List<DataModelOperation>, CancellationToken, Task<BatchResponse>>? RunDataModelOperationBatchAsync { get; set; }
            public Func<string, CancellationToken, Task<Response>>? RemoveSlotAsync { get; set; }
        }

        private LinkInterface _linkInterface = resoniteLink ?? throw new ArgumentNullException(nameof(resoniteLink));
        private readonly ILicenseCreditPolicy _licenseCreditPolicy = licenseCreditPolicy ?? throw new ArgumentNullException(nameof(licenseCreditPolicy));
        private readonly ResoniteDestinationPolicyOptions _destinationPolicyOptions = destinationPolicyOptions ?? throw new ArgumentNullException(nameof(destinationPolicyOptions));
        private readonly Func<LinkInterface> _linkInterfaceFactory = linkInterfaceFactory ?? (() => new LinkInterface());
        private readonly ILogger<ResoniteSession> _logger = logger;
        private readonly bool _dumpMeshJson = string.Equals(Environment.GetEnvironmentVariable("THREEDTILESLINK_DUMP_MESH_JSON")?.Trim(), "1", StringComparison.Ordinal) ||
                                              string.Equals(Environment.GetEnvironmentVariable("THREEDTILESLINK_DUMP_MESH_JSON")?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        private readonly string _meshDumpDir = Path.Combine(Path.GetTempPath(), "3DTilesLink", "mesh-json");
        private readonly Dictionary<string, SlotProgressBinding> _progressBindingsByParentSlotId = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _connectionGate = new(1, 1);
        private readonly SemaphoreSlim _requestGate = new(1, 1);
        private readonly SemaphoreSlim _streamPlacementGate = new(1, 1);
        private readonly ResoniteAssetImportPool _assetImportPool = new(
            assetImportWorkers > 0 ? assetImportWorkers : throw new ArgumentOutOfRangeException(nameof(assetImportWorkers), "Asset import worker count must be positive."),
            linkInterfaceFactory ?? (() => new LinkInterface()));
        private readonly Lock _disposeLock = new();
        private Task? _disposeTask;

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

        internal TestHooks? Hooks { get; set; }

        internal ResoniteSession(
            LinkInterface resoniteLink,
            ILogger<ResoniteSession> logger,
            Func<LinkInterface>? linkInterfaceFactory = null,
            int assetImportWorkers = 1)
            : this(
                resoniteLink,
                new Google.GoogleTileLicenseCreditPolicy(),
                ResoniteDestinationPolicyOptions.CreateGoogleDefaults(),
                logger,
                linkInterfaceFactory,
                assetImportWorkers)
        {
        }

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
                if (_destinationPolicyOptions.ApplyAvatarProtectionToSessionRoot)
                {
                    await AttachAvatarProtectionAsync(_sessionRootSlotId, cancellationToken).ConfigureAwait(false);
                }

                if (_destinationPolicyOptions.ApplyPackageExportProtectionToSessionRoot)
                {
                    await AttachPackageExportableAsync(_sessionRootSlotId, cancellationToken).ConfigureAwait(false);
                }
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

        public async Task<ResoniteDynamicValueInteractiveUiBinding> CreateResoniteDynamicValueInteractiveUiBindingAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(_sessionRootSlotId))
            {
                throw new InvalidOperationException("Session root slot is not initialized.");
            }

            DynamicVariableBinding latBinding = CreateDynamicFloatBindingSpec();
            DynamicVariableBinding lonBinding = CreateDynamicFloatBindingSpec();
            DynamicVariableBinding rangeBinding = CreateDynamicFloatBindingSpec();
            DynamicVariableBinding searchBinding = CreateDynamicStringBindingSpec();
            DynamicVariableBinding latAliasBinding = CreateDynamicFloatBindingSpec();
            DynamicVariableBinding lonAliasBinding = CreateDynamicFloatBindingSpec();
            DynamicVariableBinding rangeAliasBinding = CreateDynamicFloatBindingSpec();
            DynamicVariableBinding searchAliasBinding = CreateDynamicStringBindingSpec();

            List<DataModelOperation> operations =
            [
                BuildAddComponentOperation(
                    _sessionRootSlotId,
                    latBinding.ComponentId,
                    DynamicValueVariableFloatComponentType,
                    BuildDynamicFloatValueVariableMembers(
                        BuildScopedVariablePath(_destinationPolicyOptions.SessionDynamicSpaceName, InteractiveLatitudeVariableLocalName),
                        latBinding.ValueFieldId,
                        0f)),
                BuildAddComponentOperation(
                    _sessionRootSlotId,
                    lonBinding.ComponentId,
                    DynamicValueVariableFloatComponentType,
                    BuildDynamicFloatValueVariableMembers(
                        BuildScopedVariablePath(_destinationPolicyOptions.SessionDynamicSpaceName, InteractiveLongitudeVariableLocalName),
                        lonBinding.ValueFieldId,
                        0f)),
                BuildAddComponentOperation(
                    _sessionRootSlotId,
                    rangeBinding.ComponentId,
                    DynamicValueVariableFloatComponentType,
                    BuildDynamicFloatValueVariableMembers(
                        BuildScopedVariablePath(_destinationPolicyOptions.SessionDynamicSpaceName, InteractiveRangeVariableLocalName),
                        rangeBinding.ValueFieldId,
                        0f)),
                BuildAddComponentOperation(
                    _sessionRootSlotId,
                    searchBinding.ComponentId,
                    DynamicValueVariableStringComponentType,
                    BuildDynamicStringValueVariableMembers(
                        BuildScopedVariablePath(_destinationPolicyOptions.SessionDynamicSpaceName, InteractiveSearchVariableLocalName),
                        searchBinding.ValueFieldId,
                        string.Empty)),
                BuildAddComponentOperation(
                    _sessionRootSlotId,
                    latAliasBinding.ComponentId,
                    DynamicValueVariableFloatComponentType,
                    BuildDynamicFloatValueVariableMembers(
                        InteractiveLatitudeAliasPath,
                        latAliasBinding.ValueFieldId,
                        0f)),
                BuildAddComponentOperation(
                    _sessionRootSlotId,
                    CreateComponentId(),
                    ValueCopyFloatComponentType,
                    BuildValueCopyMembers(latBinding.ValueFieldId, latAliasBinding.ValueFieldId, FloatFieldType, writeBack: true)),
                BuildAddComponentOperation(
                    _sessionRootSlotId,
                    lonAliasBinding.ComponentId,
                    DynamicValueVariableFloatComponentType,
                    BuildDynamicFloatValueVariableMembers(
                        InteractiveLongitudeAliasPath,
                        lonAliasBinding.ValueFieldId,
                        0f)),
                BuildAddComponentOperation(
                    _sessionRootSlotId,
                    CreateComponentId(),
                    ValueCopyFloatComponentType,
                    BuildValueCopyMembers(lonBinding.ValueFieldId, lonAliasBinding.ValueFieldId, FloatFieldType, writeBack: true)),
                BuildAddComponentOperation(
                    _sessionRootSlotId,
                    rangeAliasBinding.ComponentId,
                    DynamicValueVariableFloatComponentType,
                    BuildDynamicFloatValueVariableMembers(
                        InteractiveRangeAliasPath,
                        rangeAliasBinding.ValueFieldId,
                        0f)),
                BuildAddComponentOperation(
                    _sessionRootSlotId,
                    CreateComponentId(),
                    ValueCopyFloatComponentType,
                    BuildValueCopyMembers(rangeBinding.ValueFieldId, rangeAliasBinding.ValueFieldId, FloatFieldType, writeBack: true)),
                BuildAddComponentOperation(
                    _sessionRootSlotId,
                    searchAliasBinding.ComponentId,
                    DynamicValueVariableStringComponentType,
                    BuildDynamicStringValueVariableMembers(
                        InteractiveSearchAliasPath,
                        searchAliasBinding.ValueFieldId,
                        string.Empty)),
                BuildAddComponentOperation(
                    _sessionRootSlotId,
                    CreateComponentId(),
                    ValueCopyStringComponentType,
                    BuildValueCopyMembers(searchBinding.ValueFieldId, searchAliasBinding.ValueFieldId, StringFieldType, writeBack: true))
            ];

            BatchResponse response = await ExecuteDataModelOperationBatchAsync(operations, cancellationToken).ConfigureAwait(false);

            string latitudeComponentId = ResolveCreatedEntityId(response.Responses![0], latBinding.ComponentId);
            string latitudeAliasComponentId = ResolveCreatedEntityId(response.Responses[4], latAliasBinding.ComponentId);
            string longitudeComponentId = ResolveCreatedEntityId(response.Responses[1], lonBinding.ComponentId);
            string longitudeAliasComponentId = ResolveCreatedEntityId(response.Responses[6], lonAliasBinding.ComponentId);
            string rangeComponentId = ResolveCreatedEntityId(response.Responses[2], rangeBinding.ComponentId);
            string rangeAliasComponentId = ResolveCreatedEntityId(response.Responses[8], rangeAliasBinding.ComponentId);
            string searchComponentId = ResolveCreatedEntityId(response.Responses[3], searchBinding.ComponentId);
            string searchAliasComponentId = ResolveCreatedEntityId(response.Responses[10], searchAliasBinding.ComponentId);

            return new ResoniteDynamicValueInteractiveUiBinding(
                latitudeComponentId,
                latitudeAliasComponentId,
                longitudeComponentId,
                longitudeAliasComponentId,
                rangeComponentId,
                rangeAliasComponentId,
                searchComponentId,
                searchAliasComponentId);
        }

        public async Task<SelectionInputValues?> ReadResoniteDynamicValueInteractiveUiValuesAsync(ResoniteDynamicValueInteractiveUiBinding binding, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(binding);

            float lat = await ReadNumericMemberAsFloatAsync(binding.LatitudeReadHandle, DynamicValueVariableValueMemberName, cancellationToken).ConfigureAwait(false);
            float lon = await ReadNumericMemberAsFloatAsync(binding.LongitudeReadHandle, DynamicValueVariableValueMemberName, cancellationToken).ConfigureAwait(false);
            float range = await ReadNumericMemberAsFloatAsync(binding.RangeReadHandle, DynamicValueVariableValueMemberName, cancellationToken).ConfigureAwait(false);

            if (!float.IsFinite(lat) || !float.IsFinite(lon) || !float.IsFinite(range))
            {
                return null;
            }

            return new SelectionInputValues(lat, lon, range);
        }

        public async Task<string?> ReadResoniteDynamicValueInteractiveUiSearchAsync(ResoniteDynamicValueInteractiveUiBinding binding, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(binding);

            return await ReadStringMemberAsync(binding.SearchReadHandle, DynamicValueVariableValueMemberName, cancellationToken).ConfigureAwait(false);
        }

        public async Task UpdateResoniteDynamicValueInteractiveUiCoordinatesAsync(ResoniteDynamicValueInteractiveUiBinding binding, double latitude, double longitude, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(binding);

            float lat = checked((float)latitude);
            float lon = checked((float)longitude);
            if (!float.IsFinite(lat) || !float.IsFinite(lon))
            {
                throw new InvalidOperationException("Interactive input coordinates must be finite values.");
            }

            await UpdateMirroredNumericMemberAsync(
                binding.LatitudeReadHandle,
                DynamicValueVariableValueMemberName,
                binding.LatitudeWriteHandle,
                DynamicValueVariableValueMemberName,
                lat,
                cancellationToken).ConfigureAwait(false);
            await UpdateMirroredNumericMemberAsync(
                binding.LongitudeReadHandle,
                DynamicValueVariableValueMemberName,
                binding.LongitudeWriteHandle,
                DynamicValueVariableValueMemberName,
                lon,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        public async Task SetSessionLicenseCreditAsync(string creditString, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_sessionLicenseComponentId))
            {
                return;
            }

            string normalized = creditString?.Trim() ?? string.Empty;
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
                                ["RequireCredit"] = new Field_bool { Value = _destinationPolicyOptions.RequireCredit },
                                ["CreditString"] = new Field_string { Value = normalized },
                                ["CanExport"] = new Field_bool { Value = _destinationPolicyOptions.CanExport }
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

        public async Task SetProgressAsync(string? parentNodeId, float progress01, string progressText, CancellationToken cancellationToken)
        {
            await SetProgressTextAsync(parentNodeId, progressText, cancellationToken).ConfigureAwait(false);
            await SetProgressValueAsync(parentNodeId, progress01, cancellationToken).ConfigureAwait(false);

            if (System.Math.Clamp(progress01, 0f, 1f) >= 1f)
            {
                await SetProgressTextAsync(parentNodeId, progressText, cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task SetProgressValueAsync(string? parentNodeId, float progress01, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            float normalizedProgress = System.Math.Clamp(progress01, 0f, 1f);
            string effectiveParentSlotId = ResolveEffectiveParentSlotId(parentNodeId);
            SlotProgressBinding binding = await EnsureProgressBindingAsync(effectiveParentSlotId).ConfigureAwait(false);
            await UpdateMirroredNumericMemberAsync(
                binding.ProgressValueComponentId,
                DynamicValueVariableValueMemberName,
                binding.ProgressValueAliasComponentId,
                DynamicValueVariableValueMemberName,
                normalizedProgress,
                cancellationToken).ConfigureAwait(false);
        }

        public async Task SetProgressTextAsync(string? parentNodeId, string progressText, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(progressText);
            cancellationToken.ThrowIfCancellationRequested();

            string effectiveParentSlotId = ResolveEffectiveParentSlotId(parentNodeId);
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

        public async Task<string?> StreamMeshAsync(PlacedMeshPayload payload, CancellationToken cancellationToken)
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
                string? parentSlotId = string.IsNullOrWhiteSpace(payload.ParentNodeId)
                    ? _sessionRootSlotId
                    : payload.ParentNodeId;
                if (string.IsNullOrWhiteSpace(parentSlotId))
                {
                    throw new InvalidOperationException("Session root slot is not initialized.");
                }

                string? warningSlotId = _destinationPolicyOptions.ApplyPackageExportProtectionToTileSlots
                    ? await EnsurePackageExportWarningSlotAsync(cancellationToken).ConfigureAwait(false)
                    : null;
                if (_destinationPolicyOptions.ApplyAvatarProtectionToTileSlots)
                {
                    await ResolveAvatarProtectionContextAsync(cancellationToken).ConfigureAwait(false);
                }

                string tileSlotId = $"t3dtile_slot_{Guid.NewGuid():N}";
                string staticMeshId = $"t3dtile_comp_{Guid.NewGuid():N}";
                string meshColliderId = $"t3dtile_comp_{Guid.NewGuid():N}";
                string materialId = $"t3dtile_comp_{Guid.NewGuid():N}";
                string meshRendererId = $"t3dtile_comp_{Guid.NewGuid():N}";
                string? staticTextureId = textureAsset is null ? null : $"t3dtile_comp_{Guid.NewGuid():N}";
                string? avatarProtectionId = _destinationPolicyOptions.ApplyAvatarProtectionToTileSlots &&
                    !_avatarProtectionUnavailable &&
                    !string.IsNullOrWhiteSpace(_avatarProtectionComponentType)
                    ? $"t3dtile_comp_{Guid.NewGuid():N}"
                    : null;

                var materialMembers = new Dictionary<string, Member>(StringComparer.Ordinal);
                Dictionary<string, MemberDefinition> materialMembersDefinition = await ResolveMaterialMemberDefinitionsAsync(cancellationToken).ConfigureAwait(false);
                if (materialMembersDefinition.ContainsKey("Smoothness"))
                {
                    materialMembers["Smoothness"] = new Field_float { Value = 0f };
                }

                string? textureMemberName = await ResolveMaterialTextureMemberNameAsync(cancellationToken).ConfigureAwait(false);
                if (textureAsset is not null &&
                    !string.IsNullOrWhiteSpace(textureMemberName) &&
                    !string.IsNullOrWhiteSpace(staticTextureId))
                {
                    materialMembers[textureMemberName] = new Reference
                    {
                        TargetType = TextureAssetProviderType,
                        TargetID = staticTextureId
                    };
                }

                List<DataModelOperation> operations = BuildMeshPlacementBatchOperations(
                    payload,
                    parentSlotId,
                    warningSlotId,
                    meshAsset.AssetURL,
                    textureAsset?.AssetURL,
                    tileSlotId,
                    staticMeshId,
                    meshColliderId,
                    staticTextureId,
                    materialId,
                    meshRendererId,
                    avatarProtectionId);

                try
                {
                    _ = await ExecuteDataModelOperationBatchAsync(operations, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await TryRemoveCreatedSlotAsync(tileSlotId, cancellationToken).ConfigureAwait(false);
                    throw;
                }

                return tileSlotId;
            }
            finally
            {
                _ = _streamPlacementGate.Release();
            }
        }

        internal Task<string?> StreamPlacedMeshAsync(PlacedMeshPayload payload, CancellationToken cancellationToken)
            => StreamMeshAsync(payload, cancellationToken);

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
            if (Hooks?.ImportMeshAssetAsync is { } importMeshOverride)
            {
                return await importMeshOverride(importMesh, cancellationToken).ConfigureAwait(false);
            }

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

        public async Task RemoveNodeAsync(string slotId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(slotId))
            {
                return;
            }

            _ = _progressBindingsByParentSlotId.Remove(slotId);
            Response response = Hooks?.RemoveSlotAsync is { } removeSlotOverride
                ? await removeSlotOverride(slotId, cancellationToken).ConfigureAwait(false)
                : await ExecuteLinkRequestAsync(
                    link => link.RemoveSlot(new RemoveSlot { SlotID = slotId }),
                    cancellationToken).ConfigureAwait(false);
            _ = EnsureSuccess(response);
        }

        internal Task RemoveSlotAsync(string slotId, CancellationToken cancellationToken)
            => RemoveNodeAsync(slotId, cancellationToken);

        public ValueTask DisposeAsync()
        {
            lock (_disposeLock)
            {
                if (_disposeTask is null || _disposeTask.IsFaulted || _disposeTask.IsCanceled)
                {
                    _disposeTask = DisposeCoreAsync();
                }

                return new ValueTask(_disposeTask);
            }
        }

        private async Task DisposeCoreAsync()
        {
            await DisconnectCoreAsync(CancellationToken.None).ConfigureAwait(false);
            await _assetImportPool.DisposeAsync().ConfigureAwait(false);
            _connectionGate.Dispose();
            _requestGate.Dispose();
            _streamPlacementGate.Dispose();
        }

        private async Task DisconnectCoreAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Log.ClosingSession(_logger);
                await _assetImportPool.DisconnectAsync(cancellationToken).ConfigureAwait(false);
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

        private async Task<BatchResponse> ExecuteDataModelOperationBatchAsync(
            List<DataModelOperation> operations,
            CancellationToken cancellationToken = default)
        {
            if (operations.Count == 0)
            {
                return new BatchResponse
                {
                    Success = true,
                    Responses = []
                };
            }

            BatchResponse response = EnsureSuccess(Hooks?.RunDataModelOperationBatchAsync is { } batchOverride
                ? await batchOverride(operations, cancellationToken).ConfigureAwait(false)
                : await ExecuteLinkRequestAsync(
                    link => link.RunDataModelOperationBatch(operations),
                    cancellationToken).ConfigureAwait(false));

            if (response.Responses is null || response.Responses.Count != operations.Count)
            {
                throw new InvalidOperationException("ResoniteLink batch response did not include the expected operation count.");
            }

            for (int i = 0; i < response.Responses.Count; i++)
            {
                Response operationResponse = response.Responses[i];
                if (!operationResponse.Success)
                {
                    throw new InvalidOperationException(
                        $"ResoniteLink batch operation failed at index {i}: {operationResponse.ErrorInfo}");
                }
            }

            return response;
        }

        private async Task<BatchResponse?> TryExecuteDataModelOperationBatchAsync(
            List<DataModelOperation> operations,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await ExecuteDataModelOperationBatchAsync(operations, cancellationToken).ConfigureAwait(false);
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

        private async Task TryRemoveCreatedSlotAsync(string? slotId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(slotId))
            {
                return;
            }

            try
            {
                await RemoveNodeAsync(slotId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
            if (Hooks?.ImportTextureAssetAsync is { } importTextureOverride)
            {
                return await importTextureOverride(textureBytes, extension, cancellationToken).ConfigureAwait(false);
            }

            if (textureBytes is null || textureBytes.Length == 0)
            {
                return null;
            }

            ImportTexture2DRawData importTexture = BuildImportTexture(textureBytes, extension);
            return EnsureSuccess(await _assetImportPool.ImportTextureAsync(
                importTexture,
                DefaultLinkRequestTimeout,
                cancellationToken).ConfigureAwait(false));
        }

        private async Task<string?> ResolveMaterialTextureMemberNameAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_materialTextureFieldResolved)
            {
                return _materialTextureFieldName;
            }

            try
            {
                Dictionary<string, MemberDefinition> members = await ResolveMaterialMemberDefinitionsAsync(cancellationToken).ConfigureAwait(false);
                _materialTextureFieldName = SelectMaterialTextureMemberName(members);
                _materialTextureFieldResolved = true;
                return _materialTextureFieldName;
            }
            catch (OperationCanceledException)
            {
                _materialTextureFieldName = null;
                _materialTextureFieldResolved = false;
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

        private static string? SelectMaterialTextureMemberName(Dictionary<string, MemberDefinition> members)
        {
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
                        return preferred;
                    }
                }

                List<string> byName = members.Keys
                    .Where(static key => key.Contains("Texture", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                return byName.Count == 0 ? null : byName[0];
            }

            foreach (string preferred in PreferredTextureFieldNames)
            {
                if (textureFields.Contains(preferred))
                {
                    return preferred;
                }
            }

            return textureFields.First();
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
                    ["RequireCredit"] = new Field_bool { Value = _destinationPolicyOptions.RequireCredit },
                    ["CreditString"] = new Field_string
                    {
                        ID = creditFieldId,
                        Value = _licenseCreditPolicy.DefaultCredit
                    },
                    ["CanExport"] = new Field_bool { Value = _destinationPolicyOptions.CanExport }
                },
                cancellationToken).ConfigureAwait(false);

            if (licenseComponentId is null)
            {
                return;
            }

            _sessionLicenseComponentId = licenseComponentId;
            _sessionLicenseCreditText = _licenseCreditPolicy.DefaultCredit;

            DynamicVariableBinding? licenseAliasBinding = await TryAddWorldStringAliasAsync(
                sessionRootSlotId,
                _destinationPolicyOptions.LicenseDynamicVariablePath,
                creditFieldId,
                writeBack: false,
                cancellationToken).ConfigureAwait(false);
            _sessionLicenseAliasComponentId = licenseAliasBinding?.ComponentId;

            DynamicVariableBinding? attributionRequirementsBinding = await TryAddDynamicStringValueVariableAsync(
                sessionRootSlotId,
                BuildScopedVariablePath(
                    _destinationPolicyOptions.SessionDynamicSpaceName,
                    _destinationPolicyOptions.AttributionRequirementsVariableLocalName),
                _licenseCreditPolicy.AttributionRequirements,
                cancellationToken).ConfigureAwait(false);
            if (attributionRequirementsBinding is not null)
            {
                _ = await TryAddWorldStringAliasAsync(
                    sessionRootSlotId,
                    _destinationPolicyOptions.AttributionRequirementsDynamicVariablePath,
                    attributionRequirementsBinding.ValueFieldId,
                    writeBack: false,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task EnsureSessionDynamicSpaceAsync(string sessionRootSlotId, CancellationToken cancellationToken = default)
        {
            if (_sessionDynamicSpaceInitialized)
            {
                return;
            }

            string? dynamicSpaceComponentId = await TryAddComponentAsync(
                sessionRootSlotId,
                DynamicVariableSpaceComponentType,
                new Dictionary<string, Member>
                {
                    ["SpaceName"] = new Field_string { Value = _destinationPolicyOptions.SessionDynamicSpaceName },
                    ["OnlyDirectBinding"] = new Field_bool { Value = true }
                },
                cancellationToken).ConfigureAwait(false);
            if (dynamicSpaceComponentId is not null)
            {
                _sessionDynamicSpaceInitialized = true;
            }
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

            DynamicVariableBinding progressValueBinding = CreateDynamicFloatBindingSpec();
            DynamicVariableBinding progressTextBinding = CreateDynamicStringBindingSpec();
            DynamicVariableBinding progressValueAliasBinding = CreateDynamicFloatBindingSpec();
            DynamicVariableBinding progressTextAliasBinding = CreateDynamicStringBindingSpec();
            string parentDynamicSpace = BuildParentDynamicSpaceName(parentSlotId);

            List<DataModelOperation> coreOperations =
            [
                BuildAddComponentOperation(
                    parentSlotId,
                    progressValueBinding.ComponentId,
                    DynamicValueVariableFloatComponentType,
                    BuildDynamicFloatValueVariableMembers(
                        BuildScopedVariablePath(parentDynamicSpace, ProgressValueVariableLocalName),
                        progressValueBinding.ValueFieldId,
                        0f)),
                BuildAddComponentOperation(
                    parentSlotId,
                    progressTextBinding.ComponentId,
                    DynamicValueVariableStringComponentType,
                    BuildDynamicStringValueVariableMembers(
                        BuildScopedVariablePath(parentDynamicSpace, ProgressTextVariableLocalName),
                        progressTextBinding.ValueFieldId,
                        string.Empty))
            ];

            BatchResponse coreResponse = await ExecuteDataModelOperationBatchAsync(coreOperations).ConfigureAwait(false);

            string progressValueComponentId = ResolveCreatedEntityId(coreResponse.Responses![0], progressValueBinding.ComponentId);
            string progressTextComponentId = ResolveCreatedEntityId(coreResponse.Responses[1], progressTextBinding.ComponentId);
            string? progressValueAliasComponentId = null;
            string? progressTextAliasComponentId = null;

            List<DataModelOperation> aliasOperations =
            [
                BuildAddComponentOperation(
                    parentSlotId,
                    progressValueAliasBinding.ComponentId,
                    DynamicValueVariableFloatComponentType,
                    BuildDynamicFloatValueVariableMembers(
                        BuildWorldProgressPath(),
                        progressValueAliasBinding.ValueFieldId,
                        0f)),
                BuildAddComponentOperation(
                    parentSlotId,
                    CreateComponentId(),
                    ValueCopyFloatComponentType,
                    BuildValueCopyMembers(
                        progressValueBinding.ValueFieldId,
                        progressValueAliasBinding.ValueFieldId,
                        FloatFieldType,
                        writeBack: false)),
                BuildAddComponentOperation(
                    parentSlotId,
                    progressTextAliasBinding.ComponentId,
                    DynamicValueVariableStringComponentType,
                    BuildDynamicStringValueVariableMembers(
                        BuildWorldProgressTextPath(),
                        progressTextAliasBinding.ValueFieldId,
                        string.Empty)),
                BuildAddComponentOperation(
                    parentSlotId,
                    CreateComponentId(),
                    ValueCopyStringComponentType,
                    BuildValueCopyMembers(
                        progressTextBinding.ValueFieldId,
                        progressTextAliasBinding.ValueFieldId,
                        StringFieldType,
                        writeBack: false))
            ];

            BatchResponse? aliasResponse = await TryExecuteDataModelOperationBatchAsync(aliasOperations).ConfigureAwait(false);
            if (aliasResponse is not null)
            {
                progressValueAliasComponentId = ResolveCreatedEntityId(aliasResponse.Responses![0], progressValueAliasBinding.ComponentId);
                progressTextAliasComponentId = ResolveCreatedEntityId(aliasResponse.Responses[2], progressTextAliasBinding.ComponentId);
            }

            var binding = new SlotProgressBinding(
                progressValueComponentId,
                progressTextComponentId,
                progressValueAliasComponentId,
                progressTextAliasComponentId);
            _progressBindingsByParentSlotId[parentSlotId] = binding;
            return binding;
        }

        private async Task ResolveAvatarProtectionContextAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                protectionContext = await ResolveComponentDefinitionAsync([SimpleAvatarProtectionComponentType], cancellationToken).ConfigureAwait(false);
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

        private async Task AttachAvatarProtectionAsync(string slotId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(slotId))
            {
                throw new InvalidOperationException("Target slot is not initialized.");
            }

            await ResolveAvatarProtectionContextAsync(cancellationToken).ConfigureAwait(false);
            if (_avatarProtectionUnavailable ||
                string.IsNullOrWhiteSpace(_avatarProtectionComponentType))
            {
                return;
            }

            _ = await AddComponentAsync(
                slotId,
                _avatarProtectionComponentType,
                BuildAvatarProtectionMembers(),
                cancellationToken).ConfigureAwait(false);
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
                _destinationPolicyOptions.PackageExportWarningSlotName,
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

        private List<DataModelOperation> BuildMeshPlacementBatchOperations(
            PlacedMeshPayload payload,
            string parentSlotId,
            string? warningSlotId,
            Uri meshAssetUri,
            Uri? textureAssetUri,
            string tileSlotId,
            string staticMeshId,
            string meshColliderId,
            string? staticTextureId,
            string materialId,
            string meshRendererId,
            string? avatarProtectionId)
        {
            var operations = new List<DataModelOperation>
            {
                new AddSlot
                {
                    Data = BuildSlotData(
                        tileSlotId,
                        payload.Name,
                        parentSlotId,
                        payload.SlotPosition,
                        payload.SlotRotation,
                        payload.SlotScale)
                },
                new AddComponent
                {
                    ContainerSlotId = tileSlotId,
                    Data = BuildComponentData(
                        staticMeshId,
                        StaticMeshComponentType,
                        new Dictionary<string, Member>(StringComparer.Ordinal)
                        {
                            ["URL"] = new Field_Uri { Value = meshAssetUri }
                        })
                },
                new AddComponent
                {
                    ContainerSlotId = tileSlotId,
                    Data = BuildComponentData(
                        meshColliderId,
                        MeshColliderComponentType,
                        new Dictionary<string, Member>(StringComparer.Ordinal)
                        {
                            ["Mesh"] = new Reference { TargetType = MeshAssetProviderType, TargetID = staticMeshId },
                            ["CharacterCollider"] = new Field_bool { Value = true },
                            ["Type"] = new Field_Enum
                            {
                                EnumType = ColliderTypeEnumType,
                                Value = "Static"
                            }
                        })
                }
            };

            if (!string.IsNullOrWhiteSpace(avatarProtectionId) &&
                !_avatarProtectionUnavailable &&
                !string.IsNullOrWhiteSpace(_avatarProtectionComponentType))
            {
                operations.Add(new AddComponent
                {
                    ContainerSlotId = tileSlotId,
                    Data = BuildComponentData(
                        avatarProtectionId,
                        _avatarProtectionComponentType,
                        BuildAvatarProtectionMembers())
                });
            }

            if (_destinationPolicyOptions.ApplyPackageExportProtectionToTileSlots &&
                !string.IsNullOrWhiteSpace(warningSlotId))
            {
                operations.Add(new AddComponent
                {
                    ContainerSlotId = tileSlotId,
                    Data = BuildComponentData(
                        $"t3dtile_comp_{Guid.NewGuid():N}",
                        PackageExportableComponentType,
                        BuildPackageExportableMembers(warningSlotId))
                });
            }

            if (textureAssetUri is not null && !string.IsNullOrWhiteSpace(staticTextureId))
            {
                operations.Add(new AddComponent
                {
                    ContainerSlotId = tileSlotId,
                    Data = BuildComponentData(
                        staticTextureId,
                        StaticTextureComponentType,
                        new Dictionary<string, Member>(StringComparer.Ordinal)
                        {
                            ["URL"] = new Field_Uri { Value = textureAssetUri }
                        })
                });
            }

            var materialMembers = new Dictionary<string, Member>(StringComparer.Ordinal);
            Dictionary<string, MemberDefinition> materialMembersDefinition = _materialMemberDefinitions
                ?? new Dictionary<string, MemberDefinition>(StringComparer.Ordinal);
            if (materialMembersDefinition.ContainsKey("Smoothness"))
            {
                materialMembers["Smoothness"] = new Field_float { Value = 0f };
            }

            string? textureMemberName = _materialTextureFieldName;
            if (textureAssetUri is not null &&
                !string.IsNullOrWhiteSpace(textureMemberName) &&
                !string.IsNullOrWhiteSpace(staticTextureId))
            {
                materialMembers[textureMemberName] = new Reference
                {
                    TargetType = TextureAssetProviderType,
                    TargetID = staticTextureId
                };
            }

            operations.Add(new AddComponent
            {
                ContainerSlotId = tileSlotId,
                Data = BuildComponentData(materialId, MaterialComponentType, materialMembers)
            });
            operations.Add(new AddComponent
            {
                ContainerSlotId = tileSlotId,
                Data = BuildComponentData(
                    meshRendererId,
                    MeshRendererComponentType,
                    new Dictionary<string, Member>(StringComparer.Ordinal)
                    {
                        ["Mesh"] = new Reference { TargetType = MeshAssetProviderType, TargetID = staticMeshId },
                        ["Materials"] = new SyncList
                        {
                            Elements = [new Reference { TargetType = MaterialAssetProviderType, TargetID = materialId }]
                        }
                    })
            });

            return operations;
        }

        private static Slot BuildSlotData(
            string slotId,
            string name,
            string? parentSlotId,
            Vector3 position,
            Quaternion rotation,
            Vector3 scale)
        {
            var slot = new Slot
            {
                ID = slotId,
                Name = new Field_string { Value = name },
                Position = new Field_float3 { Value = new float3 { x = position.X, y = position.Y, z = position.Z } },
                Rotation = new Field_floatQ { Value = new floatQ { x = rotation.X, y = rotation.Y, z = rotation.Z, w = rotation.W } },
                Scale = new Field_float3 { Value = new float3 { x = scale.X, y = scale.Y, z = scale.Z } },
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

            return slot;
        }

        private static Component BuildComponentData(
            string componentId,
            string componentType,
            Dictionary<string, Member> members)
        {
            return new Component
            {
                ID = componentId,
                ComponentType = componentType,
                Members = members
            };
        }

        private static AddComponent BuildAddComponentOperation(
            string slotId,
            string componentId,
            string componentType,
            Dictionary<string, Member> members)
        {
            return new AddComponent
            {
                ContainerSlotId = slotId,
                Data = BuildComponentData(componentId, componentType, members)
            };
        }

        private async Task<(string ComponentType, Dictionary<string, MemberDefinition> Members)> ResolveComponentDefinitionAsync(
            IEnumerable<string> componentTypeCandidates,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string componentType in componentTypeCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    ComponentDefinitionData definition = EnsureSuccess(await ExecuteLinkRequestAsync(
                        link => link.GetComponentDefinition(componentType, flattened: true),
                        cancellationToken).ConfigureAwait(false));
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

        private async Task<Dictionary<string, MemberDefinition>> ResolveMaterialMemberDefinitionsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_materialMemberDefinitions is not null)
            {
                return _materialMemberDefinitions;
            }

            ComponentDefinitionData definition = EnsureSuccess(await ExecuteLinkRequestAsync(
                link => link.GetComponentDefinition(MaterialComponentType, flattened: true),
                cancellationToken).ConfigureAwait(false));
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

        private static ImportTexture2DRawData BuildImportTexture(byte[] textureBytes, string? extension)
        {
            ArgumentNullException.ThrowIfNull(textureBytes);

            try
            {
                using Image<Rgba32> image = Image.Load<Rgba32>(textureBytes);
                byte[] rawPixelBytes = new byte[checked(image.Width * image.Height * 4)];
                image.CopyPixelDataTo(MemoryMarshal.Cast<byte, Rgba32>(rawPixelBytes.AsSpan()));

                return new ImportTexture2DRawData
                {
                    Width = image.Width,
                    Height = image.Height,
                    ColorProfile = ResolveColorProfile(extension),
                    RawBinaryPayload = rawPixelBytes
                };
            }
            catch (UnknownImageFormatException ex)
            {
                throw new InvalidOperationException($"Unsupported texture format: {extension ?? "<unknown>"}", ex);
            }
        }

        private static string ResolveColorProfile(string? extension)
        {
            return string.Equals(extension?.TrimStart('.'), "hdr", StringComparison.OrdinalIgnoreCase)
                ? "Linear"
                : "sRGB";
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

        private static string BuildScopedVariablePath(string spaceName, string variableName)
        {
            return $"{spaceName}/{variableName}";
        }

        private static string CreateComponentId()
        {
            return $"t3dtile_comp_{Guid.NewGuid():N}";
        }

        private static string CreateFieldId()
        {
            return $"t3dtile_field_{Guid.NewGuid():N}";
        }

        private static DynamicVariableBinding CreateDynamicFloatBindingSpec()
        {
            return new DynamicVariableBinding(CreateComponentId(), CreateFieldId());
        }

        private static DynamicVariableBinding CreateDynamicStringBindingSpec()
        {
            return new DynamicVariableBinding(CreateComponentId(), CreateFieldId());
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

        private static string ResolveCreatedEntityId(Response response, string fallbackEntityId)
        {
            return response is NewEntityId { EntityId: { Length: > 0 } entityId }
                ? entityId
                : fallbackEntityId;
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

            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
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
            finally
            {
                _ = _requestGate.Release();
            }
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
