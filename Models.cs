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
    public string TriggerType { get; set; } = "Chat"; // "Chat" or "Bits"
    public string TriggerIdentifier { get; set; } = "!trick"; 
    public int MinimumBits { get; set; } = 0;
    
    public int GlobalCooldownSeconds { get; set; } = 30;
    public bool BypassQueue { get; set; } = false; 
    public string Tier { get; set; } = "Minor"; // "Minor" batches up to 5. "Major" executes solo.
    
    public List<ChaosEvent> Events { get; set; } = new();
}

public class ChaosEvent
{
    public string Name { get; set; } = "";
    public int Weight { get; set; } = 100;
    
    // Smart Queue
    public string ConflictGroup { get; set; } = "General"; 
    public int QueueDurationSeconds { get; set; } = 0; 
    public bool RequiresTargetAlive { get; set; } = false; 
    
    // Situational Awareness (JIT Re-roll)
    public bool BlockedIfFullHealth { get; set; } = false;
    public List<int> BlockedByBuffIDs { get; set; } = new(); 
    
    // Progression Locks
    public string MinimumProgression { get; set; } = "Any"; 
    public string MaximumProgression { get; set; } = "Any";
    
    public List<string> TShockCommands { get; set; } = new();
}

// Required for TShock/.NET JSON trimming compatibility
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HavocConfig))]
internal partial class HavocJsonContext : JsonSerializerContext { }
