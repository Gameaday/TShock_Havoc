using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Havoc;

public class HavocConfig
{
    public bool Enabled { get; set; } = true;
    public string TwitchBotToken { get; set; } = ""; // IRC OAuth Token
    public string TwitchChannelName { get; set; } = "";
    
    public List<ManifestationPool> Manifestations { get; set; } = new();
}

public class ManifestationPool
{
    public string TriggerType { get; set; } = "Chat"; // "Chat", "Reward", "Bits"
    public string TriggerIdentifier { get; set; } = ""; // Chat command, Reward Name, or "Bits"
    public int MinimumBits { get; set; } = 0;
    
    public int GlobalCooldownSeconds { get; set; } = 30;
    public bool BypassQueue { get; set; } = false; // Set to true for high-tier Bit pools
    
    public List<ChaosEvent> Events { get; set; } = new();
}

public class ChaosEvent
{
    public string Name { get; set; } = "";
    public int Weight { get; set; } = 100;
    
    // The Smart Queue Parameters
    public string ConflictGroup { get; set; } = "General"; 
    public int QueueDurationSeconds { get; set; } = 0; // How long to lock this ConflictGroup
    
    // Progression Locks
    public string MinimumProgression { get; set; } = "Any"; 
    public string MaximumProgression { get; set; } = "Any";
    
    public List<string> TShockCommands { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HavocConfig))]
internal partial class HavocJsonContext : JsonSerializerContext { }
