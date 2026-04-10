using System.Diagnostics.CodeAnalysis;

namespace ThreeDTilesLink.App
{
    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "The DI container instantiates this coordination service.")]
    internal sealed class CommandCompletion
    {
        private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task<int> Completion => _completion.Task;

        internal void TrySetExitCode(int exitCode)
        {
            _ = _completion.TrySetResult(exitCode);
        }

        internal void TrySetException(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            _ = _completion.TrySetException(exception);
        }
    }
}
