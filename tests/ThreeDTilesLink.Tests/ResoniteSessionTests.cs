using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ResoniteLink;
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
        public async Task DisconnectAsync_DeletesTrackedTemporaryTextures()
        {
            await using var session = new ResoniteSession(new LinkInterface(), NullLogger<ResoniteSession>.Instance);

            FieldInfo? tempTextureFilesField = typeof(ResoniteSession)
                .GetField("_tempTextureFiles", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo? textureFileLocksField = typeof(ResoniteSession)
                .GetField("_textureFileLocks", BindingFlags.Instance | BindingFlags.NonPublic);

            _ = tempTextureFilesField.Should().NotBeNull();
            _ = textureFileLocksField.Should().NotBeNull();

            string tempFile = Path.GetTempFileName();
            try
            {
                var tempTextureFiles = (ConcurrentDictionary<string, byte>)tempTextureFilesField!.GetValue(session)!;
                _ = tempTextureFiles.TryAdd(tempFile, 0);

                await session.DisconnectAsync(CancellationToken.None);

                _ = File.Exists(tempFile).Should().BeFalse();
                _ = tempTextureFiles.Should().BeEmpty();

                var textureFileLocks = (ConcurrentDictionary<string, SemaphoreSlim>)textureFileLocksField!.GetValue(session)!;
                _ = textureFileLocks.Should().BeEmpty();
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }

        [Fact]
        public async Task ImportTextureAssetAsync_RemovesTrackedFileStateOnFailure()
        {
            await using var session = new ResoniteSession(new LinkInterface(), NullLogger<ResoniteSession>.Instance);

            MethodInfo? importTextureAssetMethod = typeof(ResoniteSession).GetMethod(
                "ImportTextureAssetAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo? tempTextureFilesField = typeof(ResoniteSession).GetField(
                "_tempTextureFiles",
                BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo? tempTextureDirField = typeof(ResoniteSession).GetField(
                "_textureTempDir",
                BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo? textureFileLocksField = typeof(ResoniteSession).GetField(
                "_textureFileLocks",
                BindingFlags.Instance | BindingFlags.NonPublic);

            _ = importTextureAssetMethod.Should().NotBeNull();
            _ = tempTextureFilesField.Should().NotBeNull();
            _ = tempTextureDirField.Should().NotBeNull();
            _ = textureFileLocksField.Should().NotBeNull();

            byte[] textureBytes = [0x03, 0x01, 0x02, 0x07];
            string extension = "png";
            string tempTextureDir = (string)tempTextureDirField!.GetValue(session)!;
            string expectedPath = Path.Combine(tempTextureDir, $"{Convert.ToHexString(SHA256.HashData(textureBytes))}.{extension}");

            if (File.Exists(expectedPath))
            {
                File.Delete(expectedPath);
            }

            Task importTextureTask = (Task)importTextureAssetMethod!.Invoke(
                session,
                [textureBytes, extension, CancellationToken.None])!;

            _ = await Assert.ThrowsAnyAsync<Exception>(async () => await importTextureTask);

            var tempTextureFiles = (ConcurrentDictionary<string, byte>)tempTextureFilesField!.GetValue(session)!;
            var textureFileLocks = (ConcurrentDictionary<string, SemaphoreSlim>)textureFileLocksField!.GetValue(session)!;

            _ = tempTextureFiles.Should().BeEmpty();
            _ = textureFileLocks.Should().ContainKey(expectedPath);
            _ = File.Exists(expectedPath).Should().BeFalse();
        }

        [Fact]
        public async Task CleanupConnectInitializationFailureAsync_ClearsTrackedState()
        {
            await using var session = new ResoniteSession(new LinkInterface(), NullLogger<ResoniteSession>.Instance);

            MethodInfo? cleanupMethod = typeof(ResoniteSession).GetMethod(
                "CleanupConnectInitializationFailureAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo? connectionUriField = typeof(ResoniteSession)
                .GetField("_connectionUri", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo? sessionRootSlotField = typeof(ResoniteSession)
                .GetField("_sessionRootSlotId", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo? tempTextureFilesField = typeof(ResoniteSession)
                .GetField("_tempTextureFiles", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo? tempTextureDirField = typeof(ResoniteSession)
                .GetField("_textureTempDir", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo? tempTextureFileLocksField = typeof(ResoniteSession)
                .GetField("_textureFileLocks", BindingFlags.Instance | BindingFlags.NonPublic);

            _ = cleanupMethod.Should().NotBeNull();
            _ = connectionUriField.Should().NotBeNull();
            _ = sessionRootSlotField.Should().NotBeNull();
            _ = tempTextureFilesField.Should().NotBeNull();
            _ = tempTextureDirField.Should().NotBeNull();
            _ = tempTextureFileLocksField.Should().NotBeNull();

            string tempTextureDir = (string)tempTextureDirField!.GetValue(session)!;
            string expectedPath = Path.Combine(tempTextureDir, $"{Guid.NewGuid():N}.png");
            _ = Directory.CreateDirectory(tempTextureDir);
            await File.WriteAllTextAsync(expectedPath, "cleanup failure test");

            connectionUriField!.SetValue(session, new Uri("ws://localhost:6216/"));
            sessionRootSlotField!.SetValue(session, "slot_session_root");
            var tempTextureFiles = (ConcurrentDictionary<string, byte>)tempTextureFilesField!.GetValue(session)!;
            var tempTextureFileLocks = (ConcurrentDictionary<string, SemaphoreSlim>)tempTextureFileLocksField!.GetValue(session)!;
            _ = tempTextureFiles.TryAdd(expectedPath, 0);
            _ = tempTextureFileLocks.TryAdd(expectedPath, new SemaphoreSlim(1, 1));

            await ((Task)cleanupMethod!.Invoke(session, null)!);

            _ = connectionUriField.GetValue(session).Should().BeNull();
            _ = sessionRootSlotField.GetValue(session).Should().BeNull();
            _ = tempTextureFiles.Should().BeEmpty();
            _ = tempTextureFileLocks.Should().BeEmpty();
            _ = File.Exists(expectedPath).Should().BeFalse();
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
        public void BuildSessionVariablePath_AddsSpaceNameAndStripsWorldPrefix()
        {
            MethodInfo? buildPath = typeof(ResoniteSession)
                .GetMethod("BuildSessionVariablePath", BindingFlags.NonPublic | BindingFlags.Static);
            _ = buildPath.Should().NotBeNull();

            string path = buildPath!.Invoke(null, ["Google3DTiles", "World/ThreeDTilesLink.Latitude"])
                .Should().BeOfType<string>().Subject;

            _ = path.Should().Be("Google3DTiles/ThreeDTilesLink.Latitude");
        }
    }
#pragma warning restore CA2000, CA2007
}
