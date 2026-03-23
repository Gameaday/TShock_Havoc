using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Havoc;

public class HavocConfig
{
    public bool Enabled { get; set; } = true;
    public string TwitchBotToken { get; set; } = ""; // OAuth token
    public string TwitchChannelName { get; set; } = "";
    public string TwitchClientId { get; set; } = "";
    public string ArchiveDbPath { get; set; } = "tshock/Metatron/Archive.sqlite";
    public ulong StreamerDiscordId { get; set; } = 0;
    
    public List<HavocAction> Actions { get; set; } = new();
}

public class HavocAction
{
    public string Command { get; set; } = ""; // e.g., "!spawn" or Reward Name
    public string TShockCommand { get; set; } = ""; // e.g., "/spawnmob slime 10"
    public int CooldownSeconds { get; set; } = 30;
    public bool IsRewardRedemption { get; set; } = false;
    public List<string> AllowedUsers { get; set; } = new(); // Empty = Everyone
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HavocConfig))]
internal partial class HavocJsonContext : JsonSerializerContext { }
