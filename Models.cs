using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Havoc;

public class HavocConfig
{
    public bool Enabled { get; set; } = true;
    public string TwitchBotToken { get; set; } = ""; // IRC OAuth Token
    public string TwitchChannelName { get; set; } = "";
    public string ArchiveDbPath { get; set; } = "tshock/Metatron/Archive.sqlite";
    
    public EssenceSystemConfig EssenceSystem { get; set; } = new();
    public WordingConfig Wording { get; set; } = new();
    public List<ManifestationPool> Manifestations { get; set; } = new();
}

public class EssenceSystemConfig
{
    public int BitsToEssenceMultiplier { get; set; } = 10;
    public int RiftIntervalMinMinutes { get; set; } = 15;
    public int RiftIntervalMaxMinutes { get; set; } = 45;
    public int RiftMinAbsorb { get; set; } = 50;
    public int RiftMaxAbsorb { get; set; } = 250;
    public int BatchingIntervalSeconds { get; set; } = 15;
}

public class WordingConfig
{
    public List<string> RiftAnnouncements { get; set; } = new()
    {
        "🌀 A Rift is leaking Essence! Type {code} to absorb the energy!",
        "✨ Aetheric fragments are falling. Absorb them with {code}!"
    };
    public string SuccessfulAbsorb { get; set; } = "Energy absorbed by @{user}! You gained {amount} Essence.";
    public string InsufficientEssence { get; set; } = "@{user}, you do not have enough Essence to manifest this. Check your !aura.";
    public string ResonanceHeader { get; set; } = "✨ Essence Resonated: ";
}

public class ManifestationPool
{
    public string Trigger { get; set; } = ""; // e.g., "boss"
    public int EssenceRequired { get; set; } = 500;
    public int GlobalCooldownSeconds { get; set; } = 60;
    public List<ChaosEvent> Events { get; set; } = new();
}

public class ChaosEvent
{
    public string Name { get; set; } = "";
    public int Weight { get; set; } = 100;
    public string MinimumProgression { get; set; } = "Any"; // Any, Hardmode, PostPlantera
    public string MaximumProgression { get; set; } = "Any";
    public int DurationSeconds { get; set; } = 0;
    public List<string> TShockCommands { get; set; } = new();
    public List<string> RevertCommands { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HavocConfig))]
internal partial class HavocJsonContext : JsonSerializerContext { }
