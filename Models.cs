using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Havoc;

public class HavocConfig
{
    public bool Enabled { get; set; } = true;
    public string TwitchBotToken { get; set; } = ""; 
    public string TwitchChannelName { get; set; } = "";
    public List<ManifestationPool> Manifestations { get; set; } = new();
}

public class ManifestationPool
{
    public string TriggerType { get; set; } = "Chat"; 
    public string TriggerIdentifier { get; set; } = "!trick"; 
    public int MinimumBits { get; set; } = 0;
    
    public int GlobalCooldownSeconds { get; set; } = 30;
    public bool BypassQueue { get; set; } = false; 
    public string Tier { get; set; } = "Minor"; // "Minor" batches execution, "Major" fires solo
    
    public List<ChaosEvent> Events { get; set; } = new();
}

public class ChaosEvent
{
    public string Name { get; set; } = "";
    public int Weight { get; set; } = 100;
    
    public string ConflictGroup { get; set; } = "General"; 
    public int QueueDurationSeconds { get; set; } = 0; 
    public bool RequiresTargetAlive { get; set; } = false; 
    
    // Situational Awareness
    public bool BlockedIfFullHealth { get; set; } = false;
    public bool BlockedIfBossAlive { get; set; } = false;
    public bool BlockedIfEventActive { get; set; } = false;
    public List<int> BlockedByBuffIDs { get; set; } = new(); 
    
    // Progression Locks
    public string MinimumProgression { get; set; } = "Any"; 
    public string MaximumProgression { get; set; } = "Any";

    // Execution Methods (Use SemanticQuery OR fallback to static TShockCommands)
    public SemanticQuery? DynamicQuery { get; set; } = null;
    public List<string> TShockCommands { get; set; } = new(); 
}

public class SemanticQuery
{
    public string Action { get; set; } = "GiveItem"; // GiveItem, SpawnMob, ApplyBuff
    public List<string> Tags { get; set; } = new(); // e.g., ["Weapon", "Melee", "-Yoyo"]
    public string PowerLevel { get; set; } = "Appropriate"; // Trash, Appropriate, Overpowered
    public int Amount { get; set; } = 1;
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HavocConfig))]
internal partial class HavocJsonContext : JsonSerializerContext { }
