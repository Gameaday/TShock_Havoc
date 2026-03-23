using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TwitchLib.Client;
using TwitchLib.Client.Models;
using TwitchLib.Communication.Clients;
using TwitchLib.Communication.Models;

#nullable enable

namespace Havoc;

[ApiVersion(2, 1)]
public class HavocPlugin : TerrariaPlugin
{
    public override string Name => "Havoc Crowd Control";
    public override Version Version => new Version(1, 0, 0);
    public override string Author => "HistoryLabs";

    private string ConfigPath => Path.Combine(TShock.SavePath, "Havoc", "HavocConfig.json");
    private HavocConfig _config = new();
    private TwitchClient? _client;
    private readonly ConcurrentDictionary<string, DateTime> _cooldowns = new();

    public HavocPlugin(Main game) : base(game) { }

    public override void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        LoadConfig();

        if (_config.Enabled) _ = ConnectToTwitchAsync();

        Commands.ChatCommands.Add(new Command("havoc.admin", AdminCommand, "havoc"));
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                _config = JsonSerializer.Deserialize(File.ReadAllText(ConfigPath), HavocJsonContext.Default.HavocConfig) ?? new();
            }
            else File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_config, HavocJsonContext.Default.HavocConfig));
        }
        catch (Exception ex) { TShock.Log.ConsoleError($"[Havoc] Config Error: {ex.Message}"); }
    }

    private async Task ConnectToTwitchAsync()
    {
        if (string.IsNullOrWhiteSpace(_config.TwitchBotToken)) return;

        try
        {
            var credentials = new ConnectionCredentials(_config.TwitchChannelName, _config.TwitchBotToken);
            var clientOptions = new ClientOptions { MessagesAllowedInPeriod = 750, ThrottlingPeriod = TimeSpan.FromSeconds(30) };
            WebSocketClient customClient = new WebSocketClient(clientOptions);
            
            _client = new TwitchClient(customClient);
            _client.Initialize(credentials, _config.TwitchChannelName);

            _client.OnMessageReceived += (s, e) => ProcessTwitchCommand(e.ChatMessage.Username, e.ChatMessage.Message, false);
            // Note: PubSub would be needed for Reward Redemptions; keeping IRC simple for commands first
            
            _client.Connect();
            TShock.Log.ConsoleInfo($"[Havoc] Connected to Twitch channel: {_config.TwitchChannelName}");
        }
        catch (Exception ex) { TShock.Log.ConsoleError($"[Havoc] Twitch Connection Failed: {ex.Message}"); }
    }

    private void ProcessTwitchCommand(string username, string message, bool isReward)
    {
        var action = _config.Actions.FirstOrDefault(a => 
            a.Command.Equals(message.Split(' ')[0], StringComparison.OrdinalIgnoreCase) && 
            a.IsRewardRedemption == isReward);

        if (action == null) return;

        // Check Cooldown
        string cooldownKey = $"{username}_{action.Command}";
        if (_cooldowns.TryGetValue(cooldownKey, out var lastUsed) && (DateTime.UtcNow - lastUsed).TotalSeconds < action.CooldownSeconds)
            return;

        _cooldowns[cooldownKey] = DateTime.UtcNow;

        // Resolve Target (Identity Resolution)
        string targetPlayer = GetInGameNameForStreamer() ?? "Server";
        
        string finalCmd = action.TShockCommand.Replace("{user}", username).Replace("{player}", targetPlayer);
        
        // Dispatch to TShock as Server
        TShock.Utils.NextTick(() => {
            Commands.HandleCommand(TSPlayer.Server, finalCmd.TrimStart('/'));
        });
    }

    private string? GetInGameNameForStreamer()
    {
        if (_config.StreamerDiscordId == 0 || !File.Exists(_config.ArchiveDbPath)) return null;

        try
        {
            using var conn = new SqliteConnection($"Data Source={_config.ArchiveDbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT AccountName FROM Ledger WHERE DiscordId = @did LIMIT 1";
            cmd.Parameters.AddWithValue("@did", _config.StreamerDiscordId.ToString());
            return cmd.ExecuteScalar()?.ToString();
        }
        catch { return null; }
    }

    private void AdminCommand(CommandArgs args)
    {
        if (args.Parameters.Count == 0) return;
        if (args.Parameters[0].ToLower() == "reload")
        {
            LoadConfig();
            args.Player.SendSuccessMessage("[Havoc] Config reloaded.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _client?.Disconnect();
        base.Dispose(disposing);
    }
}
