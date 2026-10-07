using RdpManager.Core.Models;

namespace RdpManager.Core.Samples;

/// <summary>
/// MOCK DATA: example systems for demonstrations. They are flagged Sample = true and shown with a "Sample"
/// label in the UI. The host names do not exist (.invalid).
/// </summary>
public static class SampleSystems
{
    private const string Note = "Sample system (mock data). The host name does not exist.";

    public static IReadOnlyList<Connection> Create() =>
    [
        Make("Finance Test Server", "at-grz-srv01.example.invalid", "Windows Server 2025", "Graz", "Finance", ["finance", "sap"], true),
        Make("Finance Production", "de-fra-srv12.example.invalid", "Windows Server 2022", "Frankfurt", "Finance", ["finance"]),
        Make("Data Science Workstation", "at-vie-ws07.example.invalid", "Windows 11 Enterprise", "Vienna", "Analytics", ["gpu", "python"], true),
        Make("Build Agent 03", "nl-ams-build03.example.invalid", "Windows Server 2025", "Amsterdam", "Engineering", ["ci"]),
        Make("Legacy ERP Jump Host", "de-muc-jmp01.example.invalid", "Windows Server 2019", "Munich", "Engineering", ["jump host"]),
        Make("Training Lab VM", "ch-zrh-lab02.example.invalid", "Windows 11 Enterprise", "Zurich", "Training", ["lab"]),
    ];

    private static Connection Make(string name, string host, string os, string location, string folder, List<string> tags, bool favorite = false)
        => new() { Name = name, Host = host, Os = os, Location = location, Folder = folder, Tags = tags, Favorite = favorite, Sample = true, Description = Note };
}
