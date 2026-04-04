using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ResoniteLink;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Resonite
{
    public sealed class ResoniteLinkClientAdapter : IResoniteLinkClient, IAsyncDisposable
    {
        private const string SlotWorkerType = "[FrooxEngine]FrooxEngine.Slot";
        private const string StaticMeshComponentType = "[FrooxEngine]FrooxEngine.StaticMesh";
        private const string StaticTextureComponentType = "[FrooxEngine]FrooxEngine.StaticTexture2D";
        private const string MeshColliderComponentType = "[FrooxEngine]FrooxEngine.MeshCollider";
        private const string MaterialComponentType = "[FrooxEngine]FrooxEngine.PBS_Metallic";
        private const string MeshRendererComponentType = "[FrooxEngine]FrooxEngine.MeshRenderer";
        private const string LicenseComponentType = "[FrooxEngine]FrooxEngine.License";
        private const string DynamicVariableSpaceComponentType = "[FrooxEngine]FrooxEngine.DynamicVariableSpace";
        // Policy: component type names are fixed from live ResoniteLink verification (no fallback guessing).
        private const string DynamicFieldStringComponentType = "[FrooxEngine]FrooxEngine.DynamicField<string>";
        private const string StringFieldType = "[FrooxEngine]FrooxEngine.IField<string>";
        private const string GoogleTilesDynamicSpaceName = "Google3DTiles";
        private const string LicenseDynamicVariablePath = GoogleTilesDynamicSpaceName + "/License";
        private const string DefaultGoogleMapsCreditText = "Google Maps";
        private const string DynamicValueVariableNameFragment = "DynamicValueVariable";

        private const string MeshAssetProviderType = "[FrooxEngine]FrooxEngine.IAssetProvider<[FrooxEngine]FrooxEngine.Mesh>";
        private const string MaterialAssetProviderType = "[FrooxEngine]FrooxEngine.IAssetProvider<[FrooxEngine]FrooxEngine.Material>";
        private const string TextureAssetProviderType = "[FrooxEngine]FrooxEngine.IAssetProvider<[FrooxEngine]FrooxEngine.ITexture2D>";
        private const string ColliderTypeEnumType = "[FrooxEngine]FrooxEngine.ColliderType";

        private static readonly string[] PreferredTextureFieldNames = ["AlbedoTexture", "BaseColorTexture", "MainTexture", "Texture"];

        private static readonly JsonSerializerOptions SerializationOptions = new()
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            AllowOutOfOrderMetadataProperties = true,
            MaxDepth = 4096
        };

        private readonly ConcurrentDictionary<string, TaskCompletionSource<Response>> _pendingResponses = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly string? _hostHeaderOverride;
        private readonly bool _dumpMeshJson;
        private readonly string _meshDumpDir = Path.Combine(Path.GetTempPath(), "3DTilesLink", "mesh-json");
        private readonly HashSet<string> _tempTextureFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _textureTempDir = Path.Combine(Path.GetTempPath(), "3DTilesLink", "textures");

        private ClientWebSocket? _socket;
        private CancellationTokenSource? _receiverCts;
        private Task? _receiverTask;
        private Exception? _failureException;
        private bool _connected;
        private string? _sessionRootSlotId;
        private string? _sessionLicenseComponentId;
        private string? _sessionLicenseCreditText;
        private string? _materialTextureFieldName;
        private bool _materialTextureFieldResolved;
        private Dictionary<string, MemberDefinition>? _materialMemberDefinitions;
        private DynamicValueVariableDefinition? _dynamicValueVariableDoubleDefinition;

        public ResoniteLinkClientAdapter()
        {
            _hostHeaderOverride = Environment.GetEnvironmentVariable("RESONITE_LINK_HOST_HEADER")?.Trim();
            string? dumpMeshJson = Environment.GetEnvironmentVariable("THREEDTILESLINK_DUMP_MESH_JSON")?.Trim();
            _dumpMeshJson = string.Equals(dumpMeshJson, "1", StringComparison.Ordinal) ||
                            string.Equals(dumpMeshJson, "true", StringComparison.OrdinalIgnoreCase);
        }

        public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            if (_connected)
            {
                return;
            }

            _socket = new ClientWebSocket();
            if (!string.IsNullOrWhiteSpace(_hostHeaderOverride))
            {
                _socket.Options.SetRequestHeader("Host", _hostHeaderOverride);
            }

            var uri = new Uri($"ws://{host}:{port}/");
            _receiverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await _socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            _receiverTask = Task.Run(() => ReceiverLoopAsync(_receiverCts.Token), CancellationToken.None);
            _connected = true;

            _sessionRootSlotId = await CreateSlotAsync(
                $"3DTilesLink Session {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}",
                Slot.ROOT_SLOT_ID,
                cancellationToken).ConfigureAwait(false);

            await AttachSessionMetadataAsync(_sessionRootSlotId, cancellationToken).ConfigureAwait(false);
        }

        public async Task SetSessionLicenseCreditAsync(string creditString, CancellationToken cancellationToken)
        {
            if (!_connected)
            {
                throw new InvalidOperationException("ResoniteLink is not connected.");
            }

            if (string.IsNullOrWhiteSpace(_sessionLicenseComponentId) ||
                string.IsNullOrWhiteSpace(creditString))
            {
                return;
            }

            string normalized = creditString.Trim();
            if (string.Equals(normalized, _sessionLicenseCreditText, StringComparison.Ordinal))
            {
                return;
            }

            _ = await SendMessageAsync<Response>(
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
                },
                cancellationToken).ConfigureAwait(false);

            _sessionLicenseCreditText = normalized;
        }

        public async Task<string?> SendTileMeshAsync(TileMeshPayload payload, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(payload);
            if (!_connected)
            {
                throw new InvalidOperationException("ResoniteLink is not connected.");
            }

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
                positionSpan[i] = new float3
                {
                    x = p.X,
                    y = p.Y,
                    z = p.Z
                };
            }

            if (payload.HasUv0)
            {
                Span<float2> uvSpan = importMesh.AccessUV_2D(0);
                for (int i = 0; i < payload.Vertices.Count; i++)
                {
                    Vector2 uv = i < payload.Uvs.Count ? payload.Uvs[i] : default;
                    uvSpan[i] = new float2
                    {
                        x = uv.X,
                        y = uv.Y
                    };
                }
            }

            Span<int> indicesSpan = triangleSubmesh.Indices;
            for (int i = 0; i < payload.Indices.Count; i++)
            {
                indicesSpan[i] = payload.Indices[i];
            }

            AssetData meshAsset = await SendMessageAsync<AssetData>(importMesh, cancellationToken).ConfigureAwait(false);

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
                cancellationToken,
                payload.SlotPosition,
                payload.SlotRotation,
                payload.SlotScale).ConfigureAwait(false);

            string staticMeshId = await AddComponentAsync(
                tileSlotId,
                StaticMeshComponentType,
                new Dictionary<string, Member>
                {
                    ["URL"] = new Field_Uri { Value = meshAsset.AssetURL }
                },
                cancellationToken).ConfigureAwait(false);

            var colliderMembers = new Dictionary<string, Member>
            {
                ["Mesh"] = new Reference { TargetType = MeshAssetProviderType, TargetID = staticMeshId },
                ["CharacterCollider"] = new Field_bool { Value = true },
                ["Type"] = new Field_Enum
                {
                    EnumType = ColliderTypeEnumType,
                    Value = "Static"
                }
            };

            _ = await AddComponentAsync(
                tileSlotId,
                MeshColliderComponentType,
                colliderMembers,
                cancellationToken).ConfigureAwait(false);

            var materialMembers = new Dictionary<string, Member>();
            Dictionary<string, MemberDefinition> materialMembersDefinition = await ResolveMaterialMemberDefinitionsAsync(cancellationToken).ConfigureAwait(false);
            if (materialMembersDefinition.ContainsKey("Smoothness"))
            {
                materialMembers["Smoothness"] = new Field_float { Value = 0f };
            }

            AssetData? textureAsset = await ImportTextureAssetAsync(
                payload.BaseColorTextureBytes,
                payload.BaseColorTextureExtension,
                cancellationToken).ConfigureAwait(false);

            string? textureMemberName = await ResolveMaterialTextureMemberNameAsync(cancellationToken).ConfigureAwait(false);
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

        public Task<string> CreateSessionChildSlotAsync(string name, CancellationToken cancellationToken)
        {
            if (!_connected)
            {
                throw new InvalidOperationException("ResoniteLink is not connected.");
            }

            if (string.IsNullOrWhiteSpace(_sessionRootSlotId))
            {
                throw new InvalidOperationException("Session root slot is not initialized.");
            }

            return CreateSlotAsync(name, _sessionRootSlotId, cancellationToken);
        }

        public async Task<ProbeBinding> CreateProbeAsync(ProbeConfiguration configuration, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            if (!_connected)
            {
                throw new InvalidOperationException("ResoniteLink is not connected.");
            }

            if (string.IsNullOrWhiteSpace(_sessionRootSlotId))
            {
                throw new InvalidOperationException("Session root slot is not initialized.");
            }

            DynamicValueVariableDefinition definition = await ResolveDynamicValueVariableDoubleDefinitionAsync(cancellationToken).ConfigureAwait(false);
            string probeSlotId = await CreateSlotAsync(configuration.SlotName, _sessionRootSlotId, cancellationToken).ConfigureAwait(false);

            string latComponentId = await AddDynamicValueVariableAsync(
                probeSlotId,
                definition,
                configuration.LatitudeVariablePath,
                configuration.InitialLatitude,
                cancellationToken).ConfigureAwait(false);

            string lonComponentId = await AddDynamicValueVariableAsync(
                probeSlotId,
                definition,
                configuration.LongitudeVariablePath,
                configuration.InitialLongitude,
                cancellationToken).ConfigureAwait(false);

            string rangeComponentId = await AddDynamicValueVariableAsync(
                probeSlotId,
                definition,
                configuration.RangeVariablePath,
                configuration.InitialRangeM,
                cancellationToken).ConfigureAwait(false);

            return new ProbeBinding(
                probeSlotId,
                latComponentId,
                definition.ValueMemberName,
                lonComponentId,
                definition.ValueMemberName,
                rangeComponentId,
                definition.ValueMemberName);
        }

        public async Task<ProbeValues?> ReadProbeValuesAsync(ProbeBinding binding, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(binding);

            if (!_connected)
            {
                throw new InvalidOperationException("ResoniteLink is not connected.");
            }

            double lat = await ReadNumericMemberAsync(binding.LatitudeComponentId, binding.LatitudeValueMemberName, cancellationToken).ConfigureAwait(false);
            double lon = await ReadNumericMemberAsync(binding.LongitudeComponentId, binding.LongitudeValueMemberName, cancellationToken).ConfigureAwait(false);
            double range = await ReadNumericMemberAsync(binding.RangeComponentId, binding.RangeValueMemberName, cancellationToken).ConfigureAwait(false);

            if (!double.IsFinite(lat) || !double.IsFinite(lon) || !double.IsFinite(range))
            {
                return null;
            }

            return new ProbeValues(lat, lon, range);
        }

        public async Task RemoveSlotAsync(string slotId, CancellationToken cancellationToken)
        {
            if (!_connected)
            {
                throw new InvalidOperationException("ResoniteLink is not connected.");
            }

            if (string.IsNullOrWhiteSpace(slotId))
            {
                return;
            }

            _ = await SendMessageAsync<Response>(
                new RemoveSlot { SlotID = slotId },
                cancellationToken).ConfigureAwait(false);
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            return DisconnectInternalAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectInternalAsync(CancellationToken.None).ConfigureAwait(false);
            _sendLock.Dispose();
        }

        private async Task<string> CreateSlotAsync(
            string name,
            string? parentSlotId,
            CancellationToken cancellationToken,
            Vector3? position = null,
            Quaternion? rotation = null,
            Vector3? scale = null)
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

            NewEntityId response = await SendMessageAsync<NewEntityId>(new AddSlot { Data = slot }, cancellationToken).ConfigureAwait(false);
            return response.EntityId;
        }

        private async Task<string> AddComponentAsync(
            string slotId,
            string componentType,
            Dictionary<string, Member> members,
            CancellationToken cancellationToken)
        {
            var request = new AddComponent
            {
                ContainerSlotId = slotId,
                Data = new Component
                {
                    ID = $"t3dtile_comp_{Guid.NewGuid():N}",
                    ComponentType = componentType,
                    Members = members
                }
            };

            return (await SendMessageAsync<NewEntityId>(request, cancellationToken).ConfigureAwait(false)).EntityId;
        }

        private async Task<string> AddDynamicValueVariableAsync(
            string slotId,
            DynamicValueVariableDefinition definition,
            string variablePath,
            double initialValue,
            CancellationToken cancellationToken)
        {
            var members = new Dictionary<string, Member>(StringComparer.Ordinal)
            {
                [definition.VariableNameMemberName] = new Field_string { Value = variablePath },
                [definition.ValueMemberName] = definition.UsesFloatValue
                    ? new Field_float { Value = (float)initialValue }
                    : new Field_double { Value = initialValue }
            };

            return await AddComponentAsync(
                slotId,
                definition.ComponentType,
                members,
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<double> ReadNumericMemberAsync(string componentId, string memberName, CancellationToken cancellationToken)
        {
            ComponentData componentData = await SendMessageAsync<ComponentData>(
                new GetComponent { ComponentID = componentId },
                cancellationToken).ConfigureAwait(false);

            if (componentData.Data is null ||
                !componentData.Data.Members.TryGetValue(memberName, out Member? member))
            {
                throw new InvalidOperationException($"Component member not found: componentId={componentId} member={memberName}");
            }

            return member switch
            {
                Field_double doubleMember => doubleMember.Value,
                Field_float floatMember => floatMember.Value,
                Field_decimal decimalMember => (double)decimalMember.Value,
                _ => throw new InvalidOperationException(
                    $"Unsupported numeric member type: componentId={componentId} member={memberName} type={member.GetType().Name}")
            };
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

            if (!File.Exists(path))
            {
                await File.WriteAllBytesAsync(path, textureBytes, cancellationToken).ConfigureAwait(false);
            }

            _ = _tempTextureFiles.Add(path);

            return await SendMessageAsync<AssetData>(
                new ImportTexture2DFile { FilePath = path },
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<string?> ResolveMaterialTextureMemberNameAsync(CancellationToken cancellationToken)
        {
            if (_materialTextureFieldResolved)
            {
                return _materialTextureFieldName;
            }

            _materialTextureFieldResolved = true;

            try
            {
                Dictionary<string, MemberDefinition> members = await ResolveMaterialMemberDefinitionsAsync(cancellationToken).ConfigureAwait(false);
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

                    var byName = members.Keys
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
            string creditFieldId = $"t3dtile_field_{Guid.NewGuid():N}";
            var licenseMembers = new Dictionary<string, Member>
            {
                ["RequireCredit"] = new Field_bool { Value = true },
                ["CreditString"] = new Field_string
                {
                    ID = creditFieldId,
                    Value = DefaultGoogleMapsCreditText
                },
                ["CanExport"] = new Field_bool { Value = false }
            };

            string? licenseComponentId = await TryAddComponentAsync(
                sessionRootSlotId,
                LicenseComponentType,
                licenseMembers,
                cancellationToken).ConfigureAwait(false);

            if (licenseComponentId is null)
            {
                return;
            }

            _sessionLicenseComponentId = licenseComponentId;
            _sessionLicenseCreditText = DefaultGoogleMapsCreditText;

            var variableSpaceMembers = new Dictionary<string, Member>
            {
                ["SpaceName"] = new Field_string { Value = GoogleTilesDynamicSpaceName },
                ["OnlyDirectBinding"] = new Field_bool { Value = true }
            };

            _ = await TryAddComponentAsync(
                sessionRootSlotId,
                DynamicVariableSpaceComponentType,
                variableSpaceMembers,
                cancellationToken).ConfigureAwait(false);

            var dynamicFieldMembers = new Dictionary<string, Member>
            {
                ["VariableName"] = new Field_string { Value = LicenseDynamicVariablePath },
                ["TargetField"] = new Reference { TargetID = creditFieldId, TargetType = StringFieldType },
                ["OverrideOnLink"] = new Field_bool { Value = true }
            };

            _ = await TryAddComponentAsync(
                sessionRootSlotId,
                DynamicFieldStringComponentType,
                dynamicFieldMembers,
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<string?> TryAddComponentAsync(
            string slotId,
            string componentType,
            Dictionary<string, Member> members,
            CancellationToken cancellationToken)
        {
            try
            {
                return await AddComponentAsync(
                    slotId,
                    componentType,
                    members,
                    cancellationToken).ConfigureAwait(false);
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

        private async Task<DynamicValueVariableDefinition> ResolveDynamicValueVariableDoubleDefinitionAsync(CancellationToken cancellationToken)
        {
            if (_dynamicValueVariableDoubleDefinition is not null)
            {
                return _dynamicValueVariableDoubleDefinition;
            }

            string componentType = await ResolveDynamicValueVariableDoubleTypeAsync(cancellationToken).ConfigureAwait(false);
            ComponentDefinitionData definitionData = await SendMessageAsync<ComponentDefinitionData>(
                new GetComponentDefinition
                {
                    ComponentType = componentType,
                    Flattened = true
                },
                cancellationToken).ConfigureAwait(false);

            Dictionary<string, MemberDefinition> members = definitionData.Definition?.Members ?? new Dictionary<string, MemberDefinition>(StringComparer.Ordinal);
            string variableNameMember = ResolveVariableNameMemberName(members);
            (string valueMember, bool usesFloatValue) = ResolveNumericValueMemberName(members);

            _dynamicValueVariableDoubleDefinition = new DynamicValueVariableDefinition(
                componentType,
                variableNameMember,
                valueMember,
                usesFloatValue);

            return _dynamicValueVariableDoubleDefinition;
        }

        private async Task<string> ResolveDynamicValueVariableDoubleTypeAsync(CancellationToken cancellationToken)
        {
            var queue = new Queue<string>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            queue.Enqueue(string.Empty);

            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string categoryPath = queue.Dequeue();
                if (!visited.Add(categoryPath))
                {
                    continue;
                }

                ComponentTypeList list = await SendMessageAsync<ComponentTypeList>(
                    new GetComponentTypeList
                    {
                        CategoryPath = categoryPath
                    },
                    cancellationToken).ConfigureAwait(false);

                string? resolved = list.ComponentTypes.FirstOrDefault(static type =>
                    type.Contains(DynamicValueVariableNameFragment, StringComparison.Ordinal) &&
                    type.Contains("System.Double", StringComparison.Ordinal));
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    return resolved;
                }

                foreach (string subCategory in list.SubCategories)
                {
                    string next = string.IsNullOrWhiteSpace(categoryPath)
                        ? subCategory
                        : $"{categoryPath}/{subCategory}";
                    queue.Enqueue(next);
                }
            }

            throw new InvalidOperationException("DynamicValueVariable<double> component type was not found via ResoniteLink.");
        }

        private static string ResolveVariableNameMemberName(IReadOnlyDictionary<string, MemberDefinition> members)
        {
            if (members.ContainsKey("VariableName"))
            {
                return "VariableName";
            }

            foreach ((string key, MemberDefinition value) in members)
            {
                if (value is not FieldDefinition fieldDefinition)
                {
                    continue;
                }

                if (fieldDefinition.ValueType?.Type?.Contains("String", StringComparison.Ordinal) == true)
                {
                    return key;
                }
            }

            throw new InvalidOperationException("Failed to resolve DynamicValueVariable string member for variable path.");
        }

        private static (string MemberName, bool UsesFloat) ResolveNumericValueMemberName(IReadOnlyDictionary<string, MemberDefinition> members)
        {
            if (members.TryGetValue("Value", out MemberDefinition? exact) &&
                exact is FieldDefinition exactField &&
                IsNumericTypeReference(exactField.ValueType, out bool exactUsesFloat))
            {
                return ("Value", exactUsesFloat);
            }

            foreach ((string key, MemberDefinition value) in members)
            {
                if (value is not FieldDefinition fieldDefinition)
                {
                    continue;
                }

                if (IsNumericTypeReference(fieldDefinition.ValueType, out bool usesFloat))
                {
                    return (key, usesFloat);
                }
            }

            throw new InvalidOperationException("Failed to resolve DynamicValueVariable numeric value member.");
        }

        private static bool IsNumericTypeReference(TypeReference? typeReference, out bool usesFloat)
        {
            usesFloat = false;
            string? name = typeReference?.Type;
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            if (name.Contains("Double", StringComparison.Ordinal))
            {
                return true;
            }

            if (name.Contains("Single", StringComparison.Ordinal) ||
                name.Contains("Float", StringComparison.Ordinal))
            {
                usesFloat = true;
                return true;
            }

            return false;
        }

        private async Task<Dictionary<string, MemberDefinition>> ResolveMaterialMemberDefinitionsAsync(CancellationToken cancellationToken)
        {
            if (_materialMemberDefinitions is not null)
            {
                return _materialMemberDefinitions;
            }

            ComponentDefinitionData definition = await SendMessageAsync<ComponentDefinitionData>(
                new GetComponentDefinition
                {
                    ComponentType = MaterialComponentType,
                    Flattened = true
                },
                cancellationToken).ConfigureAwait(false);

            Dictionary<string, MemberDefinition> members = definition.Definition?.Members ?? new Dictionary<string, MemberDefinition>(StringComparer.Ordinal);
            _materialMemberDefinitions = members;
            return _materialMemberDefinitions;
        }

        private static bool IsTextureProvider(TypeReference? typeRef)
        {
            if (typeRef is null)
            {
                return false;
            }

            return (typeRef.Type?.Contains("Texture2D", StringComparison.OrdinalIgnoreCase) ?? false) &&
                (typeRef.Type?.Contains("IAssetProvider", StringComparison.OrdinalIgnoreCase) ?? true) || typeRef.GenericArguments is not null && typeRef.GenericArguments.Any(IsTextureProvider);
        }

        private static string NormalizeExtension(string? ext)
        {
            return string.IsNullOrWhiteSpace(ext) ? ".png" : ext.StartsWith('.') ? ext : $".{ext}";
        }

        private async Task<TResponse> SendMessageAsync<TResponse>(Message message, CancellationToken cancellationToken)
            where TResponse : Response
        {
            if (!_connected || _socket is null)
            {
                throw new InvalidOperationException("ResoniteLink is not connected.");
            }

            ThrowIfFailed();

            message.Validate();
            if (string.IsNullOrWhiteSpace(message.MessageID))
            {
                message.MessageID = Guid.NewGuid().ToString("N");
            }

            var completion = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pendingResponses.TryAdd(message.MessageID, completion))
            {
                throw new InvalidOperationException($"Duplicate message ID detected: {message.MessageID}");
            }

            byte[] textPayload = JsonSerializer.SerializeToUtf8Bytes(message, SerializationOptions);

            if (_dumpMeshJson && message is ImportMeshJSON)
            {
                _ = Directory.CreateDirectory(_meshDumpDir);
                string path = Path.Combine(_meshDumpDir, $"mesh_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.json");
                await File.WriteAllBytesAsync(path, textPayload, cancellationToken).ConfigureAwait(false);
            }

            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _socket.SendAsync(textPayload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);

                if (message is BinaryPayloadMessage binaryPayloadMessage &&
                    binaryPayloadMessage.RawBinaryPayload is { Length: > 0 })
                {
                    await _socket.SendAsync(binaryPayloadMessage.RawBinaryPayload, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                _ = _pendingResponses.TryRemove(message.MessageID, out _);
                throw;
            }
            finally
            {
                _ = _sendLock.Release();
            }

            Response response = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return !response.Success
                ? throw new InvalidOperationException($"ResoniteLink request failed: {response.ErrorInfo}")
                : response is not TResponse typed
                ? throw new InvalidOperationException(
                    $"Unexpected response type for message {message.MessageID}. Expected {typeof(TResponse).Name}, got {response.GetType().Name}.")
                : typed;
        }

        private async Task ReceiverLoopAsync(CancellationToken cancellationToken)
        {
            if (_socket is null)
            {
                return;
            }

            byte[] buffer = new byte[8192];
            var stream = new MemoryStream(16384);

            try
            {
                while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
                {
                    stream.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }

                        if (result.MessageType == WebSocketMessageType.Binary)
                        {
                            continue;
                        }

                        stream.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        continue;
                    }

                    stream.Position = 0;
                    Response? response = JsonSerializer.Deserialize<Response>(stream, SerializationOptions);
                    if (response is null || string.IsNullOrWhiteSpace(response.SourceMessageID))
                    {
                        continue;
                    }

                    if (_pendingResponses.TryRemove(response.SourceMessageID, out TaskCompletionSource<Response>? completion))
                    {
                        _ = completion.TrySetResult(response);
                    }
                }
            }
            catch (Exception ex)
            {
                _failureException = ex;
                foreach ((string _, TaskCompletionSource<Response>? pending) in _pendingResponses)
                {
                    _ = pending.TrySetException(ex);
                }
                _pendingResponses.Clear();
            }
        }

        private void ThrowIfFailed()
        {
            if (_failureException is not null)
            {
                throw new InvalidOperationException("ResoniteLink receive loop failed.", _failureException);
            }
        }

        private async Task DisconnectInternalAsync(CancellationToken cancellationToken)
        {
            if (!_connected)
            {
                return;
            }

            try
            {
                if (_receiverCts is not null && !_receiverCts.IsCancellationRequested)
                {
                    _receiverCts.Cancel();
                }

                if (_socket is not null && _socket.State == WebSocketState.Open)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken).ConfigureAwait(false);
                }

                if (_receiverTask is not null)
                {
                    await _receiverTask.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                foreach ((string _, TaskCompletionSource<Response>? pending) in _pendingResponses)
                {
                    _ = pending.TrySetCanceled(cancellationToken);
                }
                _pendingResponses.Clear();

                _socket?.Dispose();
                _socket = null;
                _receiverTask = null;
                _receiverCts?.Dispose();
                _receiverCts = null;
                _failureException = null;
                _connected = false;
                _sessionRootSlotId = null;
                _sessionLicenseComponentId = null;
                _sessionLicenseCreditText = null;
                _materialTextureFieldName = null;
                _materialTextureFieldResolved = false;
                _materialMemberDefinitions = null;
                _dynamicValueVariableDoubleDefinition = null;

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

        private sealed record DynamicValueVariableDefinition(
            string ComponentType,
            string VariableNameMemberName,
            string ValueMemberName,
            bool UsesFloatValue);
    }
}
