using System.Reflection;
using FluentAssertions;
using ResoniteLink;
using ThreeDTilesLink.Core.Resonite;

namespace ThreeDTilesLink.Tests
{
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
    }
}
