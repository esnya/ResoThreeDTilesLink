namespace ThreeDTilesLink.Core.Contracts
{
    internal interface ISceneConnection
    {
        Task ConnectAsync(string host, int port, CancellationToken cancellationToken);
        Task DisconnectAsync(CancellationToken cancellationToken);
    }
}
