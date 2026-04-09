using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Numerics;
using System.Reflection;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ResoniteLink;
using ThreeDTilesLink.Core.Contracts;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Resonite;

namespace ThreeDTilesLink.Tests
{
#pragma warning disable CA2000, CA2007
    public sealed class ResoniteSessionTests
    {
        [Fact]
        public void EnsureSuccess_ThrowsMeaningfulError_WhenResponseIsNull()
        {
            MethodInfo? ensureSuccessDefinition = typeof(ResoniteSession)
                .GetMethod("EnsureSuccess", BindingFlags.NonPublic | BindingFlags.Static);
            _ = ensureSuccessDefinition.Should().NotBeNull();
            MethodInfo ensureSuccess = ensureSuccessDefinition!.MakeGenericMethod(typeof(ComponentData));

            Action act = () => _ = ensureSuccess.Invoke(null, [null]);

            TargetInvocationException assertion = act.Should().Throw<TargetInvocationException>().Which;
            _ = assertion.InnerException.Should().BeOfType<ResoniteLinkNoResponseException>()
                .Which.Message.Should().Be("ResoniteLink request returned no response.");
        }

        [Fact]
        public void BuildAvatarProtectionMembers_DoesNotSetUserReference()
        {
            MethodInfo? buildMembers = typeof(ResoniteSession)
                .GetMethod("BuildAvatarProtectionMembers", BindingFlags.NonPublic | BindingFlags.Static);
            _ = buildMembers.Should().NotBeNull();

            var members = buildMembers!.Invoke(null, []).Should().BeOfType<Dictionary<string, Member>>().Subject;

            _ = members.Should().ContainKey("ReassignUserOnPackageImport");
            _ = members.Should().NotContainKey("User");
            _ = members["ReassignUserOnPackageImport"].Should().BeOfType<Field_bool>().Which.Value.Should().BeFalse();
        }

        [Fact]
        public void BuildPackageExportableMembers_TargetsWarningRootSlot()
        {
            MethodInfo? buildMembers = typeof(ResoniteSession)
                .GetMethod("BuildPackageExportableMembers", BindingFlags.NonPublic | BindingFlags.Static);
            _ = buildMembers.Should().NotBeNull();

            const string warningSlotId = "warning-slot";
            var members = buildMembers!.Invoke(null, [warningSlotId]).Should().BeOfType<Dictionary<string, Member>>().Subject;

            _ = members.Should().ContainSingle();
            _ = members.Should().ContainKey("Root");
            Reference root = members["Root"].Should().BeOfType<Reference>().Subject;
            _ = root.TargetID.Should().Be(warningSlotId);
            _ = root.TargetType.Should().Be("[FrooxEngine]FrooxEngine.Slot");
        }

        [Fact]
        public async Task DisconnectAsync_PreservesSessionRootState()
        {
            await using var session = new ResoniteSession(new LinkInterface(), NullLogger<ResoniteSession>.Instance);

            FieldInfo? sessionRootSlotField = typeof(ResoniteSession)
                .GetField("_sessionRootSlotId", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo? connectionUriField = typeof(ResoniteSession)
                .GetField("_connectionUri", BindingFlags.Instance | BindingFlags.NonPublic);

            _ = sessionRootSlotField.Should().NotBeNull();
            _ = connectionUriField.Should().NotBeNull();

            sessionRootSlotField!.SetValue(session, "slot_session_root");
            connectionUriField!.SetValue(session, new Uri("ws://localhost:6216/"));

            await session.DisconnectAsync(CancellationToken.None);

            _ = sessionRootSlotField.GetValue(session).Should().Be("slot_session_root");
            _ = connectionUriField.GetValue(session).Should().BeEquivalentTo(new Uri("ws://localhost:6216/"));
        }

        [Fact]
        public void MetadataOperations_RemainConcreteSessionMembers()
        {
            Type sessionType = typeof(ResoniteSession);

            _ = typeof(IResoniteSessionMetadataPort).IsAssignableFrom(sessionType).Should().BeTrue();
            _ = sessionType.GetMethod(nameof(ResoniteSession.SetSessionLicenseCreditAsync)).Should().NotBeNull();
            _ = sessionType.GetMethod(nameof(ResoniteSession.SetProgressAsync)).Should().NotBeNull();
            _ = sessionType.GetMethod(nameof(ResoniteSession.SetProgressValueAsync)).Should().NotBeNull();
            _ = sessionType.GetMethod(nameof(ResoniteSession.SetProgressTextAsync)).Should().NotBeNull();
        }

        [Fact]
        public void BuildImportTexture_DecodesPngWithoutTemporaryFiles()
        {
            MethodInfo? buildImportTextureMethod = typeof(ResoniteSession).GetMethod(
                "BuildImportTexture",
                BindingFlags.Static | BindingFlags.NonPublic);

            _ = buildImportTextureMethod.Should().NotBeNull();
            _ = typeof(ResoniteSession).GetField("_tempTextureFiles", BindingFlags.Instance | BindingFlags.NonPublic).Should().BeNull();
            _ = typeof(ResoniteSession).GetField("_textureFileLocks", BindingFlags.Instance | BindingFlags.NonPublic).Should().BeNull();
            _ = typeof(ResoniteSession).GetField("_textureTempDir", BindingFlags.Instance | BindingFlags.NonPublic).Should().BeNull();

            byte[] pngBytes = CreateSolidPngBytes(2, 1, 0x11, 0x22, 0x33, 0x44);

            ImportTexture2DRawData importTexture = buildImportTextureMethod!
                .Invoke(null, [pngBytes, ".png"])
                .Should().BeOfType<ImportTexture2DRawData>().Subject;

            _ = importTexture.Width.Should().Be(2);
            _ = importTexture.Height.Should().Be(1);
            _ = importTexture.ColorProfile.Should().Be("sRGB");
            _ = importTexture.RawBinaryPayload.Should().Equal(
                0x11, 0x22, 0x33, 0x44,
                0x11, 0x22, 0x33, 0x44);
        }

        [Fact]
        public void BuildImportTexture_ThrowsMeaningfulError_ForUnknownFormat()
        {
            MethodInfo? buildImportTextureMethod = typeof(ResoniteSession).GetMethod(
                "BuildImportTexture",
                BindingFlags.Static | BindingFlags.NonPublic);

            _ = buildImportTextureMethod.Should().NotBeNull();

            Action act = () => _ = buildImportTextureMethod!.Invoke(null, [new byte[] { 0x03, 0x01, 0x02, 0x07 }, ".png"]);

            TargetInvocationException exception = act.Should().Throw<TargetInvocationException>().Which;
            _ = exception.InnerException.Should().BeOfType<InvalidOperationException>()
                .Which.Message.Should().StartWith("Unsupported texture format:");
        }

        [Fact]
        public async Task CleanupConnectInitializationFailureAsync_ClearsSessionState()
        {
            await using var session = new ResoniteSession(new LinkInterface(), NullLogger<ResoniteSession>.Instance);

            MethodInfo? cleanupMethod = typeof(ResoniteSession).GetMethod(
                "CleanupConnectInitializationFailureAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo? connectionUriField = typeof(ResoniteSession)
                .GetField("_connectionUri", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo? sessionRootSlotField = typeof(ResoniteSession)
                .GetField("_sessionRootSlotId", BindingFlags.Instance | BindingFlags.NonPublic);

            _ = cleanupMethod.Should().NotBeNull();
            _ = connectionUriField.Should().NotBeNull();
            _ = sessionRootSlotField.Should().NotBeNull();

            connectionUriField!.SetValue(session, new Uri("ws://localhost:6216/"));
            sessionRootSlotField!.SetValue(session, "slot_session_root");

            await ((Task)cleanupMethod!.Invoke(session, null)!);

            _ = connectionUriField.GetValue(session).Should().BeNull();
            _ = sessionRootSlotField.GetValue(session).Should().BeNull();
        }

        [Fact]
        public async Task EnsureSessionDynamicSpaceAsync_LeavesRetryAvailable_WhenComponentAddFails()
        {
            await using var session = new ResoniteSession(new LinkInterface(), NullLogger<ResoniteSession>.Instance);

            MethodInfo? ensureMethod = typeof(ResoniteSession)
                .GetMethod("EnsureSessionDynamicSpaceAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo? sessionRootSlotField = typeof(ResoniteSession)
                .GetField("_sessionRootSlotId", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo? sessionDynamicSpaceInitializedField = typeof(ResoniteSession)
                .GetField("_sessionDynamicSpaceInitialized", BindingFlags.Instance | BindingFlags.NonPublic);

            _ = ensureMethod.Should().NotBeNull();
            _ = sessionRootSlotField.Should().NotBeNull();
            _ = sessionDynamicSpaceInitializedField.Should().NotBeNull();

            sessionRootSlotField!.SetValue(session, "slot_session_root");

            await ((Task)ensureMethod!.Invoke(session, ["slot_session_root", CancellationToken.None])!);
            _ = ((bool)sessionDynamicSpaceInitializedField.GetValue(session)!).Should().BeFalse();

            await ((Task)ensureMethod!.Invoke(session, ["slot_session_root", CancellationToken.None])!);
            _ = ((bool)sessionDynamicSpaceInitializedField.GetValue(session)!).Should().BeFalse();
        }

        [Fact]
        public async Task ResolveMaterialTextureMemberNameAsync_RetriesAfterDisconnectedLookupFailure()
        {
            await using var session = new ResoniteSession(new LinkInterface(), NullLogger<ResoniteSession>.Instance);

            MethodInfo? resolveMethod = typeof(ResoniteSession)
                .GetMethod("ResolveMaterialTextureMemberNameAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo? resolvedField = typeof(ResoniteSession)
                .GetField("_materialTextureFieldResolved", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo? definitionsField = typeof(ResoniteSession)
                .GetField("_materialMemberDefinitions", BindingFlags.Instance | BindingFlags.NonPublic);

            _ = resolveMethod.Should().NotBeNull();
            _ = resolvedField.Should().NotBeNull();
            _ = definitionsField.Should().NotBeNull();

            string? firstResult = await ((Task<string?>)resolveMethod!.Invoke(session, [CancellationToken.None])!);
            _ = firstResult.Should().BeNull();
            _ = resolvedField!.GetValue(session).Should().Be(false);

            definitionsField!.SetValue(session, new Dictionary<string, MemberDefinition>(StringComparer.Ordinal)
            {
                ["MainTexture"] = new ReferenceDefinition
                {
                    TargetType = new TypeReference
                    {
                        Type = "[FrooxEngine]FrooxEngine.Texture2D"
                    }
                }
            });

            string? retryResult = await ((Task<string?>)resolveMethod.Invoke(session, [CancellationToken.None])!);

            _ = retryResult.Should().Be("MainTexture");
            _ = resolvedField.GetValue(session).Should().Be(true);
        }

        [Fact]
        public async Task ConnectAsync_ThrowsWhenCancellationIsAlreadyRequested()
        {
            await using var session = new ResoniteSession(new LinkInterface(), NullLogger<ResoniteSession>.Instance);

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            Func<Task> act = () => session.ConnectAsync("127.0.0.1", 1, cts.Token);
            _ = await act.Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public async Task ConnectAsync_Initializes_CleanupState_OnInitializationFailure()
        {
            const string staleRootSlotId = "slot_session_root_for_failure";
            int failedPort;

            await using var session = new ResoniteSession(new LinkInterface(), NullLogger<ResoniteSession>.Instance);

            FieldInfo? connectionUriField = typeof(ResoniteSession)
                .GetField("_connectionUri", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo? sessionRootSlotField = typeof(ResoniteSession)
                .GetField("_sessionRootSlotId", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo? sessionLicenseComponentIdField = typeof(ResoniteSession)
                .GetField("_sessionLicenseComponentId", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo? sessionLicenseCreditTextField = typeof(ResoniteSession)
                .GetField("_sessionLicenseCreditText", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo? sessionDynamicSpaceInitializedField = typeof(ResoniteSession)
                .GetField("_sessionDynamicSpaceInitialized", BindingFlags.Instance | BindingFlags.NonPublic);

            _ = connectionUriField.Should().NotBeNull();
            _ = sessionRootSlotField.Should().NotBeNull();
            _ = sessionLicenseComponentIdField.Should().NotBeNull();
            _ = sessionLicenseCreditTextField.Should().NotBeNull();
            _ = sessionDynamicSpaceInitializedField.Should().NotBeNull();

            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            failedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task acceptTask = Task.Run(async () =>
            {
                try
                {
                    using TcpClient _ = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                }
                catch (SocketException)
                {
                }
            });

            connectionUriField!.SetValue(session, new Uri($"ws://127.0.0.1:{failedPort}/"));
            sessionRootSlotField!.SetValue(session, staleRootSlotId);
            sessionLicenseComponentIdField!.SetValue(session, "asset_license_comp");
            sessionLicenseCreditTextField!.SetValue(session, "stale-license");
            sessionDynamicSpaceInitializedField!.SetValue(session, true);

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                _ = await Assert.ThrowsAnyAsync<Exception>(async () =>
                {
                    await session.ConnectAsync("127.0.0.1", failedPort, timeout.Token);
                });

                _ = connectionUriField.GetValue(session).Should().BeNull();
                _ = sessionRootSlotField.GetValue(session).Should().BeNull();
                _ = sessionLicenseComponentIdField.GetValue(session).Should().BeNull();
                _ = sessionLicenseCreditTextField.GetValue(session).Should().BeNull();
                _ = sessionDynamicSpaceInitializedField.GetValue(session).Should().Be(false);
            }
            finally
            {
                listener.Stop();
                await acceptTask;
            }
        }

        [Fact]
        public void BuildValueCopyMembers_UsesSourceTargetAndWriteBack()
        {
            MethodInfo? buildMembers = typeof(ResoniteSession)
                .GetMethod("BuildValueCopyMembers", BindingFlags.NonPublic | BindingFlags.Static);
            _ = buildMembers.Should().NotBeNull();

            var members = buildMembers!.Invoke(null, ["field_source", "field_target", "[FrooxEngine]FrooxEngine.IField<float>", true])
                .Should().BeOfType<Dictionary<string, Member>>().Subject;

            _ = members.Should().ContainKeys("Source", "Target", "WriteBack");

            Reference source = members["Source"].Should().BeOfType<Reference>().Subject;
            _ = source.TargetID.Should().Be("field_source");
            _ = source.TargetType.Should().Be("[FrooxEngine]FrooxEngine.IField<float>");

            Reference target = members["Target"].Should().BeOfType<Reference>().Subject;
            _ = target.TargetID.Should().Be("field_target");
            _ = target.TargetType.Should().Be("[FrooxEngine]FrooxEngine.IField<float>");

            _ = members["WriteBack"].Should().BeOfType<Field_bool>().Which.Value.Should().BeTrue();
        }

        [Fact]
        public async Task StreamPlacedMeshAsync_UsesBatchOperationsAndReturnsCreatedSlotId()
        {
            await using var session = CreateStreamPlacedMeshSession();
            List<DataModelOperation>? capturedOperations = null;
            List<string> removedSlotIds = [];

            session.Hooks = new ResoniteSession.TestHooks
            {
                ImportMeshAssetAsync = static (_, _) => Task.FromResult(new AssetData
                {
                    Success = true,
                    AssetURL = new Uri("resdb:///mesh")
                }),
                RunDataModelOperationBatchAsync = (operations, _) =>
                {
                    capturedOperations = [.. operations];
                    return Task.FromResult(new BatchResponse
                    {
                        Success = true,
                        Responses = operations.Select(static _ => new Response { Success = true }).ToList()
                    });
                },
                RemoveSlotAsync = (slotId, _) =>
                {
                    removedSlotIds.Add(slotId);
                    return Task.FromResult(new Response { Success = true });
                }
            };

            string? slotId = await session.StreamPlacedMeshAsync(CreatePlacedMeshPayload(), CancellationToken.None);

            _ = slotId.Should().NotBeNullOrWhiteSpace();
            _ = removedSlotIds.Should().BeEmpty();
            _ = capturedOperations.Should().NotBeNull();
            _ = capturedOperations.Should().HaveCount(6);
            AddSlot addSlot = capturedOperations![0].Should().BeOfType<AddSlot>().Subject;
            _ = addSlot.Data.ID.Should().Be(slotId);
            _ = addSlot.Data.Name.Should().BeOfType<Field_string>().Which.Value.Should().Be("tile_a");
            _ = addSlot.Data.Parent.Should().NotBeNull();
            _ = addSlot.Data.Parent!.TargetID.Should().Be("slot_parent");

            AddComponent staticMesh = capturedOperations[1].Should().BeOfType<AddComponent>().Subject;
            _ = staticMesh.ContainerSlotId.Should().Be(slotId);
            _ = staticMesh.Data.ComponentType.Should().Be("[FrooxEngine]FrooxEngine.StaticMesh");

            AddComponent meshCollider = capturedOperations[2].Should().BeOfType<AddComponent>().Subject;
            _ = meshCollider.ContainerSlotId.Should().Be(slotId);
            _ = meshCollider.Data.ComponentType.Should().Be("[FrooxEngine]FrooxEngine.MeshCollider");

            AddComponent packageExportable = capturedOperations[3].Should().BeOfType<AddComponent>().Subject;
            _ = packageExportable.Data.ComponentType.Should().Be("[FrooxEngine]FrooxEngine.PackageExportable");

            AddComponent material = capturedOperations[4].Should().BeOfType<AddComponent>().Subject;
            _ = material.Data.ComponentType.Should().Be("[FrooxEngine]FrooxEngine.PBS_Metallic");

            AddComponent meshRenderer = capturedOperations[5].Should().BeOfType<AddComponent>().Subject;
            _ = meshRenderer.Data.ComponentType.Should().Be("[FrooxEngine]FrooxEngine.MeshRenderer");
        }

        [Fact]
        public async Task StreamPlacedMeshAsync_RemovesCreatedSlot_WhenBatchOperationFails()
        {
            await using var session = CreateStreamPlacedMeshSession();
            List<string> removedSlotIds = [];

            session.Hooks = new ResoniteSession.TestHooks
            {
                ImportMeshAssetAsync = static (_, _) => Task.FromResult(new AssetData
                {
                    Success = true,
                    AssetURL = new Uri("resdb:///mesh")
                }),
                RunDataModelOperationBatchAsync = (operations, _) => Task.FromResult(new BatchResponse
                {
                    Success = true,
                    Responses =
                    [
                        new Response { Success = true },
                        new Response { Success = false, ErrorInfo = "mesh renderer failed" },
                        .. operations.Skip(2).Select(static _ => new Response { Success = true })
                    ]
                }),
                RemoveSlotAsync = (slotId, _) =>
                {
                    removedSlotIds.Add(slotId);
                    return Task.FromResult(new Response { Success = true });
                }
            };

            Func<Task> act = () => session.StreamPlacedMeshAsync(CreatePlacedMeshPayload(), CancellationToken.None);

            InvalidOperationException exception = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
            _ = exception.Message.Should().Contain("batch operation failed at index 1");
            _ = removedSlotIds.Should().ContainSingle();
            _ = removedSlotIds[0].Should().StartWith("t3dtile_slot_");
        }

        [Fact]
        public async Task StreamPlacedMeshAsync_PreservesBatchFailure_WhenCleanupRemoveFails()
        {
            await using var session = CreateStreamPlacedMeshSession();
            List<string> removedSlotIds = [];

            session.Hooks = new ResoniteSession.TestHooks
            {
                ImportMeshAssetAsync = static (_, _) => Task.FromResult(new AssetData
                {
                    Success = true,
                    AssetURL = new Uri("resdb:///mesh")
                }),
                RunDataModelOperationBatchAsync = (operations, _) => Task.FromResult(new BatchResponse
                {
                    Success = true,
                    Responses = operations.Select(static _ => new Response { Success = true }).ToList()
                        .Select((response, index) => index == 3
                            ? new Response { Success = false, ErrorInfo = "package export failed" }
                            : response)
                        .ToList()
                }),
                RemoveSlotAsync = (slotId, _) =>
                {
                    removedSlotIds.Add(slotId);
                    return Task.FromResult(new Response
                    {
                        Success = false,
                        ErrorInfo = "remove failed"
                    });
                }
            };

            Func<Task> act = () => session.StreamPlacedMeshAsync(CreatePlacedMeshPayload(), CancellationToken.None);

            InvalidOperationException exception = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
            _ = exception.Message.Should().Contain("batch operation failed at index 3");
            _ = removedSlotIds.Should().ContainSingle();
        }

        private static ResoniteSession CreateStreamPlacedMeshSession()
        {
            var session = new ResoniteSession(new LinkInterface(), NullLogger<ResoniteSession>.Instance);
            SetPrivateField(session, "_sessionRootSlotId", "slot_session_root");
            SetPrivateField(session, "_packageExportWarningSlotId", "slot_warning");
            SetPrivateField(session, "_avatarProtectionUnavailable", true);
            SetPrivateField(session, "_materialMemberDefinitions", new Dictionary<string, MemberDefinition>(StringComparer.Ordinal));
            SetPrivateField(session, "_materialTextureFieldResolved", true);
            SetPrivateField(session, "_materialTextureFieldName", null);
            return session;
        }

        private static PlacedMeshPayload CreatePlacedMeshPayload()
        {
            return new PlacedMeshPayload(
                "tile_a",
                [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
                [0, 1, 2],
                [],
                false,
                new Vector3(1f, 2f, 3f),
                Quaternion.Identity,
                Vector3.One,
                null,
                null,
                "slot_parent");
        }

        private static void SetPrivateField(object target, string fieldName, object? value)
        {
            FieldInfo? field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            _ = field.Should().NotBeNull();
            field!.SetValue(target, value);
        }

        private static byte[] CreateSolidPngBytes(int width, int height, byte r, byte g, byte b, byte a)
        {
            byte[] scanlineData = new byte[height * ((width * 4) + 1)];
            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * ((width * 4) + 1);
                for (int x = 0; x < width; x++)
                {
                    int pixelOffset = rowOffset + 1 + (x * 4);
                    scanlineData[pixelOffset] = r;
                    scanlineData[pixelOffset + 1] = g;
                    scanlineData[pixelOffset + 2] = b;
                    scanlineData[pixelOffset + 3] = a;
                }
            }

            using var stream = new MemoryStream();
            stream.Write([137, 80, 78, 71, 13, 10, 26, 10]);
            WritePngChunk(stream, "IHDR", BuildPngHeader(width, height));
            WritePngChunk(stream, "IDAT", Compress(scanlineData));
            WritePngChunk(stream, "IEND", []);
            return stream.ToArray();
        }

        private static byte[] BuildPngHeader(int width, int height)
        {
            byte[] header = new byte[13];
            WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
            WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
            header[8] = 8;
            header[9] = 6;
            return header;
        }

        private static byte[] Compress(byte[] data)
        {
            using var output = new MemoryStream();
            using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(data, 0, data.Length);
            }

            return output.ToArray();
        }

        private static void WritePngChunk(Stream stream, string chunkType, byte[] data)
        {
            byte[] typeBytes = Encoding.ASCII.GetBytes(chunkType);
            Span<byte> buffer = stackalloc byte[4];
            WriteUInt32BigEndian(buffer, (uint)data.Length);
            stream.Write(buffer);
            stream.Write(typeBytes, 0, typeBytes.Length);
            stream.Write(data, 0, data.Length);
            WriteUInt32BigEndian(buffer, ComputeCrc(typeBytes, data));
            stream.Write(buffer);
        }

        private static uint ComputeCrc(byte[] typeBytes, byte[] data)
        {
            uint crc = 0xFFFFFFFFu;
            crc = UpdateCrc(crc, typeBytes);
            crc = UpdateCrc(crc, data);
            return crc ^ 0xFFFFFFFFu;
        }

        private static uint UpdateCrc(uint crc, byte[] bytes)
        {
            foreach (byte value in bytes)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
                }
            }

            return crc;
        }

        private static void WriteUInt32BigEndian(Span<byte> destination, uint value)
        {
            destination[0] = (byte)(value >> 24);
            destination[1] = (byte)(value >> 16);
            destination[2] = (byte)(value >> 8);
            destination[3] = (byte)value;
        }
    }
#pragma warning restore CA2000, CA2007
}
