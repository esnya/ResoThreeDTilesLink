using System.Reflection;
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

            _ = tempTextureFilesField.Should().NotBeNull();

            string tempFile = Path.GetTempFileName();
            try
            {
                var tempTextureFiles = (HashSet<string>)tempTextureFilesField!.GetValue(session)!;
                _ = tempTextureFiles.Add(tempFile);

                await session.DisconnectAsync(CancellationToken.None);

                _ = File.Exists(tempFile).Should().BeFalse();
                _ = tempTextureFiles.Should().BeEmpty();
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
