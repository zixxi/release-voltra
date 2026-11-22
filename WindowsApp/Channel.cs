namespace Voltra;

internal sealed record Channel(string Label, string Processes)
{
    internal const string SystemVolume = "@master", Game = "@game", Focused = "@focused";
    internal bool Dynamic => Processes is Game or Focused;
    internal string Inactive => Processes == Game ? "No active game" : "No audio session";
}
