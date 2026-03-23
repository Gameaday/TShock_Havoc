using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Havoc;

public class HavocConfig
{
    public bool Enabled { get; set; } = true;
    public string TwitchBotToken { get; set; } = ""; // IRC Token
    public string TwitchChannelName { get; set; } = "";
    
    // NEW: Needed for Channel Points (PubSub)
    public string TwitchChannelId { get; set; } = ""; 
    public string TwitchPubSubToken { get; set; } = ""; 
    
    public string ArchiveDbPath { get; set; } = "tshock/Metatron/Archive.sqlite";
    public ulong StreamerDiscordId { get; set; } = 0;
    
    public List<ChaosPool> Pools { get; set; } = new();
}

public class ChaosPool
{
    // "Chat", "Reward", or "Bits"
    public string TriggerType { get; set; } = "Chat"; 
    
    // The chat command (e.g., "!annoy") OR the exact name of the Twitch Reward
    public string TriggerIdentifier { get; set; } = ""; 
    
    // NEW: The bit threshold required to trigger this pool
    public int MinimumBits { get; set; } = 0; 
    
    public int CooldownSeconds { get; set; } = 60;
    public List<ChaosEvent> Events { get; set; } = new();
}

public class ChaosEvent
{
    public string Name { get; set; } = "";
    public int Weight { get; set; } = 100; 
    public List<string> TShockCommands { get; set; } = new(); 
    public int DurationSeconds { get; set; } = 0;
    public List<string> RevertCommands { get; set; } = new();
    public string MinimumProgression { get; set; } = "Any"; 
    public string MaximumProgression { get; set; } = "Any"; 
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HavocConfig))]
internal partial class HavocJsonContext : JsonSerializerContext { }
