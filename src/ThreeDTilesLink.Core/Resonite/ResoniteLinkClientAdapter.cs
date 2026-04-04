using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ResoniteLink;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;

namespace ThreeDTilesLink.Core.Resonite;

public sealed class ResoniteLinkClientAdapter : IResoniteLinkClient, IAsyncDisposable
{
    private const string SlotWorkerType = "[FrooxEngine]FrooxEngine.Slot";
    private const string StaticMeshComponentType = "[FrooxEngine]FrooxEngine.StaticMesh";
    private const string StaticTextureComponentType = "[FrooxEngine]FrooxEngine.StaticTexture2D";
    private const string MeshColliderComponentType = "[FrooxEngine]FrooxEngine.MeshCollider";
    private const string MaterialComponentType = "[FrooxEngine]FrooxEngine.PBS_Metallic";
    private const string MeshRendererComponentType = "[FrooxEngine]FrooxEngine.MeshRenderer";

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
    private string? _materialTextureFieldName;
    private bool _materialTextureFieldResolved;
    private Dictionary<string, MemberDefinition>? _materialMemberDefinitions;

    public ResoniteLinkClientAdapter()
    {
        _hostHeaderOverride = Environment.GetEnvironmentVariable("RESONITE_LINK_HOST_HEADER")?.Trim();
        var dumpMeshJson = Environment.GetEnvironmentVariable("THREEDTILESLINK_DUMP_MESH_JSON")?.Trim();
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
        await _socket.ConnectAsync(uri, cancellationToken);
        _receiverTask = Task.Run(() => ReceiverLoopAsync(_receiverCts.Token), CancellationToken.None);
        _connected = true;

        _sessionRootSlotId = await CreateSlotAsync(
            $"3DTilesLink Session {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}",
            Slot.ROOT_SLOT_ID,
            cancellationToken);
    }

    public async Task SendTileMeshAsync(TileMeshPayload payload, CancellationToken cancellationToken)
    {
        if (!_connected)
        {
            throw new InvalidOperationException("ResoniteLink is not connected.");
        }

        if (payload.Vertices.Count == 0 || payload.Indices.Count == 0)
        {
            return;
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

        var positionSpan = importMesh.Positions;
        for (var i = 0; i < payload.Vertices.Count; i++)
        {
            var p = payload.Vertices[i];
            positionSpan[i] = new float3
            {
                x = p.X,
                y = p.Y,
                z = p.Z
            };
        }

        if (payload.HasUv0)
        {
            var uvSpan = importMesh.AccessUV_2D(0);
            for (var i = 0; i < payload.Vertices.Count; i++)
            {
                var uv = i < payload.Uvs.Count ? payload.Uvs[i] : default;
                uvSpan[i] = new float2
                {
                    x = uv.X,
                    y = uv.Y
                };
            }
        }

        var indicesSpan = triangleSubmesh.Indices;
        for (var i = 0; i < payload.Indices.Count; i++)
        {
            indicesSpan[i] = payload.Indices[i];
        }

        var meshAsset = await SendMessageAsync<AssetData>(importMesh, cancellationToken);

        var tileSlotId = await CreateSlotAsync(
            payload.Name,
            _sessionRootSlotId,
            cancellationToken,
            payload.SlotPosition,
            payload.SlotRotation,
            payload.SlotScale);

        var staticMeshId = await AddComponentAsync(
            tileSlotId,
            StaticMeshComponentType,
            new Dictionary<string, Member>
            {
                ["URL"] = new Field_Uri { Value = meshAsset.AssetURL }
            },
            cancellationToken);

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

        await AddComponentAsync(
            tileSlotId,
            MeshColliderComponentType,
            colliderMembers,
            cancellationToken);

        var materialMembers = new Dictionary<string, Member>();
        var materialMembersDefinition = await ResolveMaterialMemberDefinitionsAsync(cancellationToken);
        if (materialMembersDefinition.ContainsKey("Smoothness"))
        {
            materialMembers["Smoothness"] = new Field_float { Value = 0f };
        }

        AssetData? textureAsset = await ImportTextureAssetAsync(
            payload.BaseColorTextureBytes,
            payload.BaseColorTextureExtension,
            cancellationToken);
        var textureMemberName = await ResolveMaterialTextureMemberNameAsync(cancellationToken);
        if (textureAsset is not null && !string.IsNullOrWhiteSpace(textureMemberName))
        {
            var staticTextureId = await AddComponentAsync(
                tileSlotId,
                StaticTextureComponentType,
                new Dictionary<string, Member>
                {
                    ["URL"] = new Field_Uri { Value = textureAsset.AssetURL }
                },
                cancellationToken);

            materialMembers[textureMemberName] = new Reference
            {
                TargetType = TextureAssetProviderType,
                TargetID = staticTextureId
            };
        }

        var materialId = await AddComponentAsync(tileSlotId, MaterialComponentType, materialMembers, cancellationToken);

        await AddComponentAsync(
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
            cancellationToken);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        return DisconnectInternalAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectInternalAsync(CancellationToken.None);
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
        var p = position ?? Vector3.Zero;
        var r = rotation ?? Quaternion.Identity;
        var s = scale ?? Vector3.One;

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

        var response = await SendMessageAsync<NewEntityId>(new AddSlot { Data = slot }, cancellationToken);
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

        return (await SendMessageAsync<NewEntityId>(request, cancellationToken)).EntityId;
    }

    private async Task<AssetData?> ImportTextureAssetAsync(byte[]? textureBytes, string? extension, CancellationToken cancellationToken)
    {
        if (textureBytes is null || textureBytes.Length == 0)
        {
            return null;
        }

        Directory.CreateDirectory(_textureTempDir);

        var hash = Convert.ToHexString(SHA256.HashData(textureBytes));
        var ext = NormalizeExtension(extension);
        var path = Path.Combine(_textureTempDir, $"{hash}{ext}");

        if (!File.Exists(path))
        {
            await File.WriteAllBytesAsync(path, textureBytes, cancellationToken);
        }

        _tempTextureFiles.Add(path);

        return await SendMessageAsync<AssetData>(
            new ImportTexture2DFile { FilePath = path },
            cancellationToken);
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
            var members = await ResolveMaterialMemberDefinitionsAsync(cancellationToken);
            var textureFields = members
                .Where(x => x.Value is ReferenceDefinition refDef && IsTextureProvider(refDef.TargetType))
                .Select(x => x.Key)
                .ToHashSet(StringComparer.Ordinal);

            if (textureFields.Count == 0)
            {
                foreach (var preferred in PreferredTextureFieldNames)
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

            foreach (var preferred in PreferredTextureFieldNames)
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

    private async Task<Dictionary<string, MemberDefinition>> ResolveMaterialMemberDefinitionsAsync(CancellationToken cancellationToken)
    {
        if (_materialMemberDefinitions is not null)
        {
            return _materialMemberDefinitions;
        }

        var definition = await SendMessageAsync<ComponentDefinitionData>(
            new GetComponentDefinition
            {
                ComponentType = MaterialComponentType,
                Flattened = true
            },
            cancellationToken);

        var members = definition.Definition?.Members ?? new Dictionary<string, MemberDefinition>(StringComparer.Ordinal);
        _materialMemberDefinitions = members;
        return _materialMemberDefinitions;
    }

    private static bool IsTextureProvider(TypeReference? typeRef)
    {
        if (typeRef is null)
        {
            return false;
        }

        if ((typeRef.Type?.Contains("Texture2D", StringComparison.OrdinalIgnoreCase) ?? false) &&
            (typeRef.Type?.Contains("IAssetProvider", StringComparison.OrdinalIgnoreCase) ?? true))
        {
            return true;
        }

        if (typeRef.GenericArguments is null)
        {
            return false;
        }

        return typeRef.GenericArguments.Any(IsTextureProvider);
    }

    private static string NormalizeExtension(string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext))
        {
            return ".png";
        }

        return ext.StartsWith(".", StringComparison.Ordinal) ? ext : $".{ext}";
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

        var textPayload = JsonSerializer.SerializeToUtf8Bytes(message, typeof(Message), SerializationOptions);

        if (_dumpMeshJson && message is ImportMeshJSON)
        {
            Directory.CreateDirectory(_meshDumpDir);
            var path = Path.Combine(_meshDumpDir, $"mesh_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.json");
            await File.WriteAllBytesAsync(path, textPayload, cancellationToken);
        }

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendAsync(textPayload, WebSocketMessageType.Text, true, cancellationToken);

            if (message is BinaryPayloadMessage binaryPayloadMessage &&
                binaryPayloadMessage.RawBinaryPayload is { Length: > 0 })
            {
                await _socket.SendAsync(binaryPayloadMessage.RawBinaryPayload, WebSocketMessageType.Binary, true, cancellationToken);
            }
        }
        catch
        {
            _pendingResponses.TryRemove(message.MessageID, out _);
            throw;
        }
        finally
        {
            _sendLock.Release();
        }

        var response = await completion.Task.WaitAsync(cancellationToken);
        if (!response.Success)
        {
            throw new InvalidOperationException($"ResoniteLink request failed: {response.ErrorInfo}");
        }

        if (response is not TResponse typed)
        {
            throw new InvalidOperationException(
                $"Unexpected response type for message {message.MessageID}. Expected {typeof(TResponse).Name}, got {response.GetType().Name}.");
        }

        return typed;
    }

    private async Task ReceiverLoopAsync(CancellationToken cancellationToken)
    {
        if (_socket is null)
        {
            return;
        }

        var buffer = new byte[8192];
        var stream = new MemoryStream(16384);

        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                stream.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, cancellationToken);
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
                var response = JsonSerializer.Deserialize<Response>(stream, SerializationOptions);
                if (response is null || string.IsNullOrWhiteSpace(response.SourceMessageID))
                {
                    continue;
                }

                if (_pendingResponses.TryRemove(response.SourceMessageID, out var completion))
                {
                    completion.TrySetResult(response);
                }
            }
        }
        catch (Exception ex)
        {
            _failureException = ex;
            foreach (var (_, pending) in _pendingResponses)
            {
                pending.TrySetException(ex);
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
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken);
            }

            if (_receiverTask is not null)
            {
                await _receiverTask;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            foreach (var (_, pending) in _pendingResponses)
            {
                pending.TrySetCanceled();
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
            _materialTextureFieldName = null;
            _materialTextureFieldResolved = false;
            _materialMemberDefinitions = null;

            foreach (var textureFile in _tempTextureFiles)
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
