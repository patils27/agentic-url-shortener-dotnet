// Scenario runner CLI.
//
// Usage:
//   dotnet run --project src/Scenarios -- greenfield --auto
//   dotnet run --project src/Scenarios -- brownfield --auto
//   dotnet run --project src/Scenarios -- ambiguous --auto

using AgenticUrlShortener.Scenarios;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: Scenarios <greenfield|brownfield|ambiguous> [--auto]");
    return 1;
}

var scenario = args[0].ToLowerInvariant();
var auto = args.Contains("--auto");

switch (scenario)
{
    case "greenfield":
        Greenfield.Run(auto);
        break;
    case "brownfield":
        Brownfield.Run(auto);
        break;
    case "ambiguous":
        Ambiguous.Run(auto);
        break;
    default:
        Console.Error.WriteLine($"unknown scenario: {scenario}");
        return 1;
}

return 0;
