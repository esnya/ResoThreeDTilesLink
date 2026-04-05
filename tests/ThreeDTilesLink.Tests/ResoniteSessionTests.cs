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
    }
#pragma warning restore CA2000, CA2007
}
