// Diagnostic console tool; localized resources are unnecessary here.
#pragma warning disable CA1303
using System.Globalization;
using System.Numerics;
using ThreeDTilesLink.Core.Models;
using ThreeDTilesLink.Core.Resonite;

var host = args.Length > 0 ? args[0] : "localhost";
var port = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 49379;

var client = new ResoniteLinkClientAdapter();
await using (client.ConfigureAwait(false))
{
    await client.ConnectAsync(host, port, CancellationToken.None).ConfigureAwait(false);

    var vertices = new List<Vector3>
    {
        new(0f, 0f, 0f),
        new(0f, 1f, 0f),
        new(1f, 0f, 0f)
    };
    var indices = new List<int> { 0, 1, 2 };

    Console.WriteLine("Send no UV");
    _ = await client.SendTileMeshAsync(new TileMeshPayload(
        "probe_no_uv",
        vertices,
        indices,
        new List<Vector2>(),
        false,
        Vector3.Zero,
        Quaternion.Identity,
        Vector3.One,
        null,
        null), CancellationToken.None).ConfigureAwait(false);
    Console.WriteLine("no UV ok");

    Console.WriteLine("Send with UV");
    _ = await client.SendTileMeshAsync(new TileMeshPayload(
        "probe_uv",
        vertices,
        indices,
        new List<Vector2>
        {
            new(0f, 0f),
            new(0f, 1f),
            new(1f, 0f)
        },
        true,
        Vector3.Zero,
        Quaternion.Identity,
        Vector3.One,
        null,
        null), CancellationToken.None).ConfigureAwait(false);
    Console.WriteLine("with UV ok");

    await client.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
}
