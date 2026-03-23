using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Havoc;

public class HavocConfig
{
    public bool Enabled { get; set; } = true;
    public string TwitchBotToken { get; set; } = ""; 
    public string TwitchChannelName { get; set; } = "";
    public string ArchiveDbPath { get; set; } = "tshock/Metatron/Archive.sqlite";
    public ulong StreamerDiscordId { get; set; } = 0;
    
    // Instead of raw actions, viewers trigger "Pools"
    public List<ChaosPool> Pools { get; set; } = new();
}

public class ChaosPool
{
    public string TriggerCommand { get; set; } = ""; // e.g., "!annoy" or "Annoy Streamer" reward
    public bool IsRewardRedemption { get; set; } = false;
    public int CooldownSeconds { get; set; } = 60;
    
    public List<ChaosEvent> Events { get; set; } = new();
}

public class ChaosEvent
{
    public string Name { get; set; } = ""; // Internal name for logging
    public List<string> TShockCommands { get; set; } = new(); 
    
    // Progression Locks
    public string MinimumProgression { get; set; } = "Any"; // Any, Hardmode, PostPlantera
    public string MaximumProgression { get; set; } = "Any"; // Useful for preventing pre-hardmode enemies in endgame
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HavocConfig))]
internal partial class HavocJsonContext : JsonSerializerContext { }
