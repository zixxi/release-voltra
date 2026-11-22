using System.Text.Json;
using System.Text.Json.Serialization;

namespace Voltra;

internal sealed record MappingProfile<T>(string Id, string Name, T[] Channels) where T : class
{
    public override string ToString() => Name;
}

internal sealed record MappingProfiles<T> where T : class
{
    public string ActiveId { get; init; } = "";
    public MappingProfile<T>[] Items { get; init; } = [];
    internal MappingProfile<T> Active => Items.First(p => p.Id == ActiveId);
    internal const int MaxName = 32;

    internal MappingProfiles<T> Restore(T[] legacy, Func<T, T> normalize)
    {
        var valid = new List<MappingProfile<T>>();
        foreach (var p in Items ?? [])
        {
            if (p is null || !Guid.TryParseExact(p.Id, "N", out _) || !ValidName(p.Name) ||
                p.Channels is not { Length: 5 } || p.Channels.Any(c => c is null) ||
                valid.Any(v => v.Id == p.Id || SameName(v.Name, p.Name.Trim()))) continue;
            valid.Add(p with { Name = p.Name.Trim(), Channels = p.Channels.Select(normalize).ToArray() });
        }
        if (valid.Count == 0)
            valid.Add(new("00000000000000000000000000000001", "Default", legacy.Select(normalize).ToArray()));
        return new() { Items = [.. valid], ActiveId = valid.FirstOrDefault(p => p.Id == ActiveId)?.Id ?? valid[0].Id };
    }

    internal MappingProfiles<T> Activate(string id) =>
        Items.Any(p => p.Id == id) ? this with { ActiveId = id } : throw new ArgumentException("Unknown profile.");

    internal MappingProfiles<T> SaveMappings(T[] channels)
    {
        if (channels.Length != 5 || channels.Any(c => c is null)) throw new ArgumentException("Expected five mappings.");
        return Replace(Active with { Channels = [.. channels] });
    }

    internal MappingProfiles<T> Create(string name, T[] channels)
    {
        CheckName(name);
        if (channels.Length != 5 || channels.Any(c => c is null)) throw new ArgumentException("Expected five mappings.");
        var profile = new MappingProfile<T>(Guid.NewGuid().ToString("N"), name.Trim(), [.. channels]);
        return this with { Items = [.. Items, profile], ActiveId = profile.Id };
    }

    internal MappingProfiles<T> Rename(string id, string name)
    {
        CheckName(name, id);
        return Replace(Items.First(p => p.Id == id) with { Name = name.Trim() });
    }

    internal MappingProfiles<T> Delete(string id)
    {
        if (Items.Length <= 1) throw new ArgumentException("Keep at least one profile.");
        var remaining = Items.Where(p => p.Id != id).ToArray();
        return this with { Items = remaining, ActiveId = ActiveId == id ? remaining[0].Id : ActiveId };
    }

    private MappingProfiles<T> Replace(MappingProfile<T> profile) =>
        this with { Items = Items.Select(p => p.Id == profile.Id ? profile : p).ToArray() };

    private static bool SameName(string a, string b) => StringComparer.OrdinalIgnoreCase.Equals(a, b);
    private static bool ValidName(string? name) => name?.Trim().Length is > 0 and <= MaxName && !name.Trim().Any(char.IsControl);
    private void CheckName(string name, string? except = null)
    {
        if (!ValidName(name)) throw new ArgumentException($"Enter a name between 1 and {MaxName} characters.");
        if (Items.Any(p => p.Id != except && SameName(p.Name, name.Trim())))
            throw new ArgumentException("A profile with this name already exists.");
    }
}

// Isolate malformed optional profile data from the existing settings document.
// Channels remains the legacy mirror and can recover the active assignment.
internal sealed class MappingProfilesConverter<T> : JsonConverter<MappingProfiles<T>> where T : class
{
    public override MappingProfiles<T> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(nameof(MappingProfiles<T>.Items), out var items) || items.ValueKind != JsonValueKind.Array)
            return new();
        var profiles = new List<MappingProfile<T>>();
        foreach (var item in items.EnumerateArray())
        {
            try
            {
                if (item.Deserialize<MappingProfile<T>>(options) is { } profile) profiles.Add(profile);
            }
            catch (JsonException) { /* Recover the other profiles independently. */ }
        }
        string? id = root.TryGetProperty(nameof(MappingProfiles<T>.ActiveId), out var active) &&
            active.ValueKind == JsonValueKind.String ? active.GetString() : null;
        return new() { ActiveId = id ?? "", Items = [.. profiles] };
    }

    public override void Write(Utf8JsonWriter writer, MappingProfiles<T> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}
