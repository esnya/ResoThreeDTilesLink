namespace ThreeDTilesLink.Core.Contracts
{
    internal interface IResoniteSessionConnection
    {
        Task ConnectAsync(string host, int port, CancellationToken cancellationToken);
        Task DisconnectAsync(CancellationToken cancellationToken);
    }
}
