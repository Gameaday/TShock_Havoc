using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Timers;
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
    public override string Name => "Essence of Havoc";
    public override Version Version => new Version(2, 0, 0);
    public override string Author => "HistoryLabs";

    private string ConfigPath => Path.Combine(TShock.SavePath, "Havoc", "HavocConfig.json");
    private HavocConfig _config = new();
    private TwitchClient? _client;
    private bool _isActive = false;

    // Concurrency & Anti-Spam
    private readonly ConcurrentDictionary<string, DateTime> _cooldowns = new();
    private readonly ConcurrentQueue<string> _resonanceBatcher = new();
    
    // Timers
    private Timer? _batchTimer;
    private Timer? _dbFlushTimer;
    private Timer? _riftSpawnerTimer;
    private Timer? _riftExpiryTimer;

    // Rift State
    private string _currentRiftCode = "";
    private bool _riftActive = false;

    public HavocPlugin(Main game) : base(game) { }

    public override void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        LoadConfig();
        HavocBank.Initialize(_config.ArchiveDbPath);

        Commands.ChatCommands.Add(new Command("havoc.admin", AdminCommand, "havoc"));

        // Setup Background Flush (Every 5 mins)
        _dbFlushTimer = new Timer(300000); 
        _dbFlushTimer.Elapsed += (s, e) => HavocBank.FlushToDatabase();
        _dbFlushTimer.Start();
    }

    private void StartEngine(TSPlayer player)
    {
        if (_isActive) return;
        
        try
        {
            var credentials = new ConnectionCredentials(_config.TwitchChannelName, _config.TwitchBotToken);
            var clientOptions = new ClientOptions { MessagesAllowedInPeriod = 20, ThrottlingPeriod = TimeSpan.FromSeconds(30) };
            WebSocketClient customClient = new WebSocketClient(clientOptions);
            
            _client = new TwitchClient(customClient);
            _client.Initialize(credentials, _config.TwitchChannelName);
            _client.OnMessageReceived += OnTwitchMessage;
            _client.Connect();

            _isActive = true;

            // Start Batcher
            _batchTimer = new Timer(_config.EssenceSystem.BatchingIntervalSeconds * 1000);
            _batchTimer.Elapsed += FlushBatcher;
            _batchTimer.Start();

            // Start First Rift Timer
            ScheduleNextRift();

            player.SendSuccessMessage("[Havoc] The Aetheric Forge is ignited. Listening to Twitch.");
        }
        catch (Exception ex) { player.SendErrorMessage($"[Havoc] Ignition Failed: {ex.Message}"); }
    }

    private void OnTwitchMessage(object? sender, TwitchLib.Client.Events.OnMessageReceivedArgs e)
    {
        string user = e.ChatMessage.Username;
        string msg = e.ChatMessage.Message.ToLower().Trim();

        // 1. Aetheric Infusion (Bits)
        if (e.ChatMessage.Bits > 0)
        {
            int gained = e.ChatMessage.Bits * _config.EssenceSystem.BitsToEssenceMultiplier;
            HavocBank.ModifyAura(user, gained);
            _resonanceBatcher.Enqueue($"{user} (+{gained})");
        }

        // 2. Rift Stabilization
        if (_riftActive && msg == _currentRiftCode)
        {
            _riftActive = false;
            _riftExpiryTimer?.Stop();
            int amount = Random.Shared.Next(_config.EssenceSystem.RiftMinAbsorb, _config.EssenceSystem.RiftMaxAbsorb + 1);
            HavocBank.ModifyAura(user, amount);
            SendMessageToTwitch(_config.Wording.SuccessfulAbsorb.Replace("{user}", user).Replace("{amount}", amount.ToString()));
            ScheduleNextRift();
            return;
        }

        // 3. Check Aura
        if (msg == "!aura")
        {
            SendMessageToTwitch($"@{user}, your inherent Aura contains {HavocBank.GetAura(user)} Essence.");
            return;
        }

        // 4. Test the Flux (Double or Nothing)
        if (msg.StartsWith("!flux "))
        {
            if (int.TryParse(msg.Substring(6), out int wager) && wager > 0)
            {
                if (HavocBank.TryConsumeAura(user, wager))
                {
                    if (Random.Shared.Next(100) > 55) // 45% win chance
                    {
                        HavocBank.ModifyAura(user, wager * 2);
                        SendMessageToTwitch($"@{user} fed {wager} to the Flux... it RESONATED! (+{wager * 2} Essence)");
                    }
                    else SendMessageToTwitch($"@{user} fed {wager} to the Flux... it collapsed into the void.");
                }
                else SendMessageToTwitch(_config.Wording.InsufficientEssence.Replace("{user}", user));
            }
            return;
        }

        // 5. Manifestations
        if (msg.StartsWith("!invoke "))
        {
            string target = msg.Substring(8);
            var pool = _config.Manifestations.FirstOrDefault(p => p.Trigger.Equals(target, StringComparison.OrdinalIgnoreCase));
            
            if (pool != null) AttemptManifestation(user, pool);
        }
    }

    private void AttemptManifestation(string user, ManifestationPool pool)
    {
        // Global Cooldown Check
        if (_cooldowns.TryGetValue(pool.Trigger, out var lastUsed) && (DateTime.UtcNow - lastUsed).TotalSeconds < pool.GlobalCooldownSeconds)
            return;

        // Check Essence
        if (!HavocBank.TryConsumeAura(user, pool.EssenceRequired))
        {
            SendMessageToTwitch(_config.Wording.InsufficientEssence.Replace("{user}", user));
            return;
        }

        // Filter Progression
        var validEvents = pool.Events.Where(IsProgressionValid).ToList();
        if (validEvents.Count == 0)
        {
            HavocBank.ModifyAura(user, pool.EssenceRequired); // The Refund
            SendMessageToTwitch($"@{user}, the world resists this manifestation right now. Your Essence was returned.");
            return;
        }

        // Lock Cooldown
        _cooldowns[pool.Trigger] = DateTime.UtcNow;

        // Roll the Dice
        int totalWeight = validEvents.Sum(e => e.Weight);
        int roll = Random.Shared.Next(0, totalWeight);
        ChaosEvent? selected = validEvents.Last(); // Fallback

        foreach (var e in validEvents)
        {
            if (roll < e.Weight) { selected = e; break; }
            roll -= e.Weight;
        }

        // Execute natively on the Game Thread
        TShock.Utils.NextTick(() => {
            foreach (var cmd in selected.TShockCommands)
                Commands.HandleCommand(TSPlayer.Server, cmd.Replace("{user}", user));
                
            TSPlayer.All.SendMessage($"✨ {user} has manifested: {selected.Name}!", new Microsoft.Xna.Framework.Color(180, 32, 240));
        });

        // The Reversion Engine
        if (selected.DurationSeconds > 0 && selected.RevertCommands.Count > 0)
        {
            Task.Delay(selected.DurationSeconds * 1000).ContinueWith(_ => {
                TShock.Utils.NextTick(() => {
                    foreach (var rev in selected.RevertCommands)
                        Commands.HandleCommand(TSPlayer.Server, rev.Replace("{user}", user));
                });
            });
        }
    }

    // --- GAME LOGIC ---
    private bool IsProgressionValid(ChaosEvent e)
    {
        if (e.MinimumProgression.Equals("Hardmode", StringComparison.OrdinalIgnoreCase) && !Main.hardMode) return false;
        if (e.MinimumProgression.Equals("PostPlantera", StringComparison.OrdinalIgnoreCase) && !NPC.downedPlantBoss) return false;
        if (e.MaximumProgression.Equals("PreHardmode", StringComparison.OrdinalIgnoreCase) && Main.hardMode) return false;
        return true;
    }

    // --- RIFT SYSTEM ---
    private void ScheduleNextRift()
    {
        if (_riftSpawnerTimer != null) { _riftSpawnerTimer.Stop(); _riftSpawnerTimer.Dispose(); }
        
        int minutes = Random.Shared.Next(_config.EssenceSystem.RiftIntervalMinMinutes, _config.EssenceSystem.RiftIntervalMaxMinutes + 1);
        _riftSpawnerTimer = new Timer(minutes * 60000);
        _riftSpawnerTimer.Elapsed += (s, e) => SpawnRift();
        _riftSpawnerTimer.Start();
    }

    private void SpawnRift()
    {
        _currentRiftCode = "!" + Guid.NewGuid().ToString().Substring(0, 4);
        _riftActive = true;

        string announcement = _config.Wording.RiftAnnouncements[Random.Shared.Next(_config.Wording.RiftAnnouncements.Count)];
        SendMessageToTwitch(announcement.Replace("{code}", _currentRiftCode));

        _riftExpiryTimer = new Timer(30000); // 30 seconds to claim
        _riftExpiryTimer.Elapsed += (s, e) => { _riftActive = false; _riftExpiryTimer.Stop(); };
        _riftExpiryTimer.Start();
    }

    // --- UTILITIES ---
    private void FlushBatcher(object? sender, ElapsedEventArgs e)
    {
        if (_resonanceBatcher.IsEmpty || _client == null || !_client.IsConnected) return;

        var sb = new StringBuilder(_config.Wording.ResonanceHeader);
        bool hasItems = false;
        while (_resonanceBatcher.TryDequeue(out var msg))
        {
            sb.Append($" {msg},");
            hasItems = true;
        }

        if (hasItems) SendMessageToTwitch(sb.ToString().TrimEnd(','));
    }

    private void SendMessageToTwitch(string msg)
    {
        if (_client != null && _client.IsConnected)
            _client.SendMessage(_config.TwitchChannelName, msg);
    }

    private void LoadConfig()
    {
        if (File.Exists(ConfigPath)) _config = JsonSerializer.Deserialize(File.ReadAllText(ConfigPath), HavocJsonContext.Default.HavocConfig) ?? new();
        else File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_config, HavocJsonContext.Default.HavocConfig));
    }

    private void AdminCommand(CommandArgs args)
    {
        if (args.Parameters.Count == 0) return;
        string cmd = args.Parameters[0].ToLower();
        
        if (cmd == "start") StartEngine(args.Player);
        else if (cmd == "stop") { _isActive = false; _client?.Disconnect(); HavocBank.FlushToDatabase(); }
        else if (cmd == "forcerift") SpawnRift();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            HavocBank.FlushToDatabase();
            _client?.Disconnect();
            _batchTimer?.Dispose();
            _dbFlushTimer?.Dispose();
        }
        base.Dispose(disposing);
    }
}
