// Diagnostic console tool; localized resources are unnecessary here.
#pragma warning disable CA1303
using System.Globalization;
using ResoniteLink;

var host = args.Length > 0 ? args[0] : "localhost";
var port = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 49379;
string[] protectionComponentCandidates =
[
    "[FrooxEngine]FrooxEngine.CommonAvatar.SimpleAvatarProtection"
];
const string packageExportableComponentType = "[FrooxEngine]FrooxEngine.PackageExportable";

using var link = new LinkInterface();
await link.Connect(new Uri($"ws://{host}:{port}/"), CancellationToken.None).ConfigureAwait(false);

var sessionData = await link.GetSessionData().ConfigureAwait(false);
if (sessionData.Success)
{
    Console.WriteLine("Session data:");
    Console.WriteLine($"  ResoniteVersion={sessionData.ResoniteVersion}");
    Console.WriteLine($"  ResoniteLinkVersion={sessionData.ResoniteLinkVersion}");
    Console.WriteLine($"  UniqueSessionId={sessionData.UniqueSessionId}");
    Console.WriteLine("  Note: no host/user field is exposed by SessionData in ResoniteLink 0.13.1");
}
else
{
    Console.WriteLine($"GetSessionData failed: {sessionData.ErrorInfo}");
}

Console.WriteLine();
Console.WriteLine("SimpleAvatarProtection component definition probes:");
foreach (string componentType in protectionComponentCandidates)
{
    var protectionDef = await link.GetComponentDefinition(componentType, true).ConfigureAwait(false);
    if (!protectionDef.Success || protectionDef.Definition is null)
    {
        Console.WriteLine($"  {componentType}: not found ({protectionDef.ErrorInfo})");
        continue;
    }

    Console.WriteLine($"  {componentType}: found");
    foreach (var kv in protectionDef.Definition.Members.OrderBy(x => x.Key))
    {
        var extra = kv.Value switch
        {
            ReferenceDefinition rd => $" targetType={rd.TargetType?.Type}",
            _ => string.Empty
        };
        Console.WriteLine($"    {kv.Key}: {kv.Value.GetType().Name}{extra}");
    }
}

Console.WriteLine();
Console.WriteLine("PackageExportable component definition probe:");
var packageExportableDef = await link.GetComponentDefinition(packageExportableComponentType, true).ConfigureAwait(false);
if (!packageExportableDef.Success || packageExportableDef.Definition is null)
{
    Console.WriteLine($"  {packageExportableComponentType}: not found ({packageExportableDef.ErrorInfo})");
}
else
{
    Console.WriteLine($"  {packageExportableComponentType}: found");
    foreach (var kv in packageExportableDef.Definition.Members.OrderBy(x => x.Key))
    {
        var extra = kv.Value switch
        {
            ReferenceDefinition rd => $" targetType={rd.TargetType?.Type}",
            _ => string.Empty
        };
        Console.WriteLine($"    {kv.Key}: {kv.Value.GetType().Name}{extra}");
    }
}

Console.WriteLine();
var materialDef = await link.GetComponentDefinition("[FrooxEngine]FrooxEngine.PBS_Metallic", true).ConfigureAwait(false);
if (materialDef.Success && materialDef.Definition is not null)
{
    Console.WriteLine("Material texture member definitions:");
    foreach (var kv in materialDef.Definition.Members
                 .Where(k => k.Key.Contains("Texture", StringComparison.OrdinalIgnoreCase)))
    {
        var extra = kv.Value is ReferenceDefinition rd
            ? $" targetType={rd.TargetType?.Type}"
            : string.Empty;
        Console.WriteLine($"  {kv.Key}: {kv.Value.GetType().Name}{extra}");
    }
}

var root = await link.GetSlotData(new GetSlot
{
    SlotID = Slot.ROOT_SLOT_ID,
    Depth = 2,
    IncludeComponentData = false
}).ConfigureAwait(false);

if (!root.Success || root.Data is null)
{
    Console.WriteLine($"GetSlot root failed: {root.ErrorInfo}");
    return;
}

var sessions = new List<Slot>();
Collect(root.Data, sessions);

if (sessions.Count == 0)
{
    Console.WriteLine("No 3DTilesLink session slots found.");
    return;
}

var latest = sessions
    .OrderByDescending(s => s.Name?.Value ?? string.Empty)
    .First();

Console.WriteLine($"Session: {latest.Name?.Value} ({latest.ID})");

var sessionDetail = await link.GetSlotData(new GetSlot
{
    SlotID = latest.ID,
    Depth = 1,
    IncludeComponentData = true
}).ConfigureAwait(false);

if (!sessionDetail.Success || sessionDetail.Data is null)
{
    Console.WriteLine($"GetSlot session failed: {sessionDetail.ErrorInfo}");
    return;
}

Console.WriteLine($"Children: {sessionDetail.Data.Children?.Count ?? 0}");

var printed = 0;
foreach (var child in sessionDetail.Data.Children ?? [])
{
    if (printed >= 5)
    {
        break;
    }

    var childDetail = await link.GetSlotData(new GetSlot
    {
        SlotID = child.ID,
        Depth = 0,
        IncludeComponentData = true
    }).ConfigureAwait(false);

    if (!childDetail.Success || childDetail.Data is null)
    {
        Console.WriteLine($"  - {child.Name?.Value} ({child.ID}) [failed to fetch details]");
        continue;
    }

    printed++;
    var c = childDetail.Data;
    var components = c.Components ?? [];
    Console.WriteLine($"  - Slot {printed}: {c.Name?.Value} ({c.ID}) components={components.Count}");
    Console.WriteLine(
        $"    transform: pos=({c.Position?.Value.x:F3},{c.Position?.Value.y:F3},{c.Position?.Value.z:F3}) " +
        $"rot=({c.Rotation?.Value.x:F4},{c.Rotation?.Value.y:F4},{c.Rotation?.Value.z:F4},{c.Rotation?.Value.w:F4}) " +
        $"scale=({c.Scale?.Value.x:F4},{c.Scale?.Value.y:F4},{c.Scale?.Value.z:F4})");

    var typeSet = new HashSet<string>(components.Select(x => x.ComponentType ?? string.Empty), StringComparer.Ordinal);
    Console.WriteLine($"    component types: {string.Join(", ", typeSet.OrderBy(x => x))}");

    var material = components.FirstOrDefault(x => string.Equals(x.ComponentType, "[FrooxEngine]FrooxEngine.PBS_Metallic", StringComparison.Ordinal));
    if (material is not null)
    {
        var textureMembers = material.Members
            .Where(kv => kv.Key.Contains("Texture", StringComparison.OrdinalIgnoreCase))
            .Select(kv => $"{kv.Key}:{kv.Value.GetType().Name}")
            .ToList();

        Console.WriteLine($"    material members: {string.Join(", ", material.Members.Keys.OrderBy(x => x).Take(12))}");
        if (textureMembers.Count > 0)
        {
            Console.WriteLine($"    texture members: {string.Join(", ", textureMembers)}");
        }

        if (material.Members.TryGetValue("AlbedoTexture", out var albedoTexMember) && albedoTexMember is Reference albedoRef)
        {
            Console.WriteLine($"    AlbedoTexture ref: targetType={albedoRef.TargetType} targetId={albedoRef.TargetID}");
        }

        if (material.Members.TryGetValue("Smoothness", out var smoothnessMember) && smoothnessMember is Field_float smoothness)
        {
            Console.WriteLine($"    Smoothness={smoothness.Value}");
        }
    }
}

static void Collect(Slot slot, List<Slot> output)
{
    var name = slot.Name?.Value ?? string.Empty;
    if (name.StartsWith("3DTilesLink Session", StringComparison.Ordinal))
    {
        output.Add(slot);
    }

    foreach (var child in slot.Children ?? [])
    {
        Collect(child, output);
    }
}
