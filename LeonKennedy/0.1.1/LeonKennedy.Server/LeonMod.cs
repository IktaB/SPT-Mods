using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using WTTServerCommonLib.Services;

namespace LeonKennedy;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.ikta.leonkennedy.re4";
    public string Name { get; init; } = "LeonKennedyRE4R";
    public string Author { get; init; } = "Ikta";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("0.1.1");
    public SemanticVersioning.Range SptVersion { get; init; } = new("=4.1.6");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new()
    {
        ["com.wtt.commonlib"] = new(">=3.0.6 <4.0.0")
    };
    public string? Url { get; init; }
    public string License { get; init; } = "Code: MIT; game assets: Capcom";
    public bool HasPrepatcher { get; init; }
}

// Profiles are migrated during SaveCallbacks. Register clothing before validation,
// after WTT's preload patches, so purchased Leon suites remain valid on restart.
[Injectable(TypePriority = OnLoadOrder.Preload + 1)]
public class LeonMod(WTTCustomHeadService heads, WTTCustomClothingService clothing,
    WTTCustomVoiceService voices, TemplateTable templates) : IOnLoad
{
    // Keep customization IDs stable for profiles created with earlier builds.
    private static readonly string[] CustomizationIds =
    [
        "d5e6a2adcc35a7e6c4880d57", "ac2bb2cd66298f430f65d7e0",
        "9446de91f3827c498970c961", "0933acb7abf9fe57e674f147",
        "bf205161a5387d77ea7ef6bf", "4d7f418d3caf12a2f8252564",
        "d415f67248ec1473f3bfb277"
    ];

    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var assembly = Assembly.GetExecutingAssembly();
        await heads.CreateCustomHeads(assembly);
        await clothing.CreateCustomClothing(assembly);
        await voices.CreateCustomVoices(assembly);
        foreach (var id in CustomizationIds)
        {
            if (!templates.Customization.ContainsKey(id))
                throw new InvalidOperationException($"Leon customization registration failed: {id}");
        }
    }
}
