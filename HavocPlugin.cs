using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TwitchLib.Client;
using TwitchLib.Client.Models;
using TwitchLib.Communication.Clients;
using TwitchLib.Communication.Models;

#nullable enable

namespace Havoc;

public class QueuedAction
{
    public ChaosEvent Event { get; set; } = new();
    public ManifestationPool ParentPool { get; set; } = new();
    public string Username { get; set; } = "";
}

[ApiVersion(2, 1)]
public class HavocPlugin : TerrariaPlugin
{
    public override string Name => "Havoc Engine Pro";
    public override Version Version => new Version(5, 0, 2);
    public override string Author => "HistoryLabs";

    private string ConfigPath => Path.Combine(TShock.SavePath, "Havoc", "HavocConfig.json");
    private HavocConfig _config = new();

    private string? _targetAccountName;
    private bool _engineAwake = false;
    private TwitchClient? _client;

    private readonly ConcurrentDictionary<string, DateTime> _cooldowns = new();
    private readonly ConcurrentDictionary<string, DateTime> _activeConflicts = new();
    private readonly ConcurrentQueue<QueuedAction> _actionQueue = new();
    
    // NATIVE ENGINE SYNC: Replaces the Timer completely
    private int _tickCounter = 0;
    private bool _isProcessingQueue = false;

    public HavocPlugin(Main game) : base(game) { }

    #region LIFECYCLE
    public override void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        LoadConfig();

        ServerApi.Hooks.GamePostInitialize.Register(this, (args) => HavocIndex.BuildIndex());
        
        // Hook natively into Terraria's 60 FPS update loop
        ServerApi.Hooks.GameUpdate.Register(this, OnGameUpdate);
        
        Commands.ChatCommands.Add(new Command("havoc.admin", AdminCommand, "havoc"));
    }

    private void StartHavoc(TSPlayer player)
    {
        if (!player.IsLoggedIn) { player.SendErrorMessage("[Havoc] You must be logged in to be the target."); return; }
        _targetAccountName = player.Account.Name;
        if (!_engineAwake) { ConnectTwitch(); _engineAwake = true; }
        player.SendSuccessMessage($"[Havoc] Session bound to '{_targetAccountName}'.");
    }

    private void StopHavoc()
    {
        _targetAccountName = null; _engineAwake = false;
        _actionQueue.Clear(); _activeConflicts.Clear(); _tickCounter = 0;
        _client?.Disconnect(); _client = null;
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
        if (cmd == "on") StartHavoc(args.Player);
        else if (cmd == "off") { StopHavoc(); args.Player.SendSuccessMessage("[Havoc] Deactivated."); }
        else if (cmd == "reload") { LoadConfig(); args.Player.SendSuccessMessage("[Havoc] Reloaded."); }
    }

    protected override void Dispose(bool disposing) 
    { 
        if (disposing) 
        {
            ServerApi.Hooks.GameUpdate.Deregister(this, OnGameUpdate);
            StopHavoc(); 
        }
        base.Dispose(disposing); 
    }
    #endregion

    #region TWITCH
    private void ConnectTwitch()
    {
        if (string.IsNullOrWhiteSpace(_config.TwitchBotToken)) return;
        _client = new TwitchClient(new WebSocketClient(new ClientOptions { MessagesAllowedInPeriod = 20, ThrottlingPeriod = TimeSpan.FromSeconds(30) }));
        _client.Initialize(new ConnectionCredentials(_config.TwitchChannelName, _config.TwitchBotToken), _config.TwitchChannelName);
        _client.OnDisconnected += (s, e) => Task.Delay(5000).ContinueWith(_ => { if (_engineAwake && !_client.IsConnected) _client.Connect(); });
        _client.OnMessageReceived += OnTwitchMessage;
        _client.Connect();
    }

    private void OnTwitchMessage(object? sender, TwitchLib.Client.Events.OnMessageReceivedArgs e)
    {
        if (!_engineAwake) return;
        ManifestationPool? pool = e.ChatMessage.Bits > 0 
            ? _config.Manifestations.Where(p => p.TriggerType == "Bits" && e.ChatMessage.Bits >= p.MinimumBits).OrderByDescending(p => p.MinimumBits).FirstOrDefault()
            : _config.Manifestations.FirstOrDefault(p => p.TriggerType == "Chat" && p.TriggerIdentifier.Equals(e.ChatMessage.Message.Trim().Split(' ')[0], StringComparison.OrdinalIgnoreCase));

        if (pool != null) AttemptManifestation(e.ChatMessage.Username, pool);
    }

    private void AttemptManifestation(string user, ManifestationPool pool)
    {
        if (_cooldowns.TryGetValue(pool.TriggerIdentifier, out var lastUsed) && (DateTime.UtcNow - lastUsed).TotalSeconds < pool.GlobalCooldownSeconds) return;
        var validEvents = pool.Events.Where(IsProgressionValid).ToList();
        if (validEvents.Count == 0) return;

        _cooldowns[pool.TriggerIdentifier] = DateTime.UtcNow;
        int roll = Random.Shared.Next(0, validEvents.Sum(e => e.Weight));
        ChaosEvent? selected = validEvents.Last();
        foreach (var e in validEvents) { if (roll < e.Weight) { selected = e; break; } roll -= e.Weight; }

        var action = new QueuedAction { Event = selected, ParentPool = pool, Username = user };
        if (pool.BypassQueue) ExecuteAction(action, TShock.Players.FirstOrDefault(p => p?.Account?.Name == _targetAccountName)); 
        else _actionQueue.Enqueue(action);
    }
    #endregion

    #region SMART QUEUE & EXECUTION
    
    // Hooks directly into Terraria's 60 Frames-Per-Second engine loop
    private void OnGameUpdate(EventArgs args)
    {
        if (!_engineAwake) return;

        _tickCounter++;
        if (_tickCounter >= 60) // 60 Ticks = exactly 1 real-world second
        {
            _tickCounter = 0;
            ProcessSmartQueue();
        }
    }

    private void ProcessSmartQueue()
    {
        if (_actionQueue.IsEmpty || _isProcessingQueue || _targetAccountName == null) return;
        _isProcessingQueue = true;

        try
        {
            var target = TShock.Players.FirstOrDefault(p => p != null && p.Active && p.IsLoggedIn && p.Account.Name.Equals(_targetAccountName, StringComparison.OrdinalIgnoreCase));
            if (target == null || target.X <= 0 || target.Y <= 0 || float.IsNaN(target.X)) return; // Offline or Transitioning

            List<QueuedAction> toExecute = new();

            if (_actionQueue.TryPeek(out var nextAction))
            {
                if (!IsSituationallyValid(nextAction.Event, target)) { PerformJitReroll(nextAction, target); return; }
                if (_activeConflicts.TryGetValue(nextAction.Event.ConflictGroup, out var expiry) && DateTime.UtcNow < expiry) return; 
                if (nextAction.Event.RequiresTargetAlive && target.TPlayer.dead) return; 

                if (nextAction.ParentPool.Tier.Equals("Minor", StringComparison.OrdinalIgnoreCase))
                {
                    while (toExecute.Count < 5 && _actionQueue.TryDequeue(out var batchItem)) {
                        toExecute.Add(batchItem);
                        if (_actionQueue.TryPeek(out var upcoming) && !upcoming.ParentPool.Tier.Equals("Minor", StringComparison.OrdinalIgnoreCase)) break;
                    }
                }
                else if (_actionQueue.TryDequeue(out var soloItem)) toExecute.Add(soloItem);
            }

            // Because this is running inside OnGameUpdate, we are safely on the Main Thread. No NextTick required!
            if (toExecute.Count > 0)
            {
                foreach (var action in toExecute) {
                    ExecuteAction(action, target);
                    if (action.Event.QueueDurationSeconds > 0) _activeConflicts[action.Event.ConflictGroup] = DateTime.UtcNow.AddSeconds(action.Event.QueueDurationSeconds);
                }
            }
        }
        catch (Exception ex) { TShock.Log.ConsoleError($"[Havoc] Queue Error: {ex.Message}"); }
        finally { _isProcessingQueue = false; }
    }

    private void ExecuteAction(QueuedAction action, TSPlayer? target)
    {
        if (target == null) return;
        List<string> commands = new();
        string executionName = action.Event.Name;

        if (action.Event.DynamicQuery != null)
        {
            var query = action.Event.DynamicQuery;
            int tier = HavocIndex.GetCurrentWorldTier();

            if (query.Action == "GiveItem")
            {
                Item? i = HavocIndex.QueryItem(query, tier);
                if (i != null) { commands.Add($"/give {i.type} \"{{player}}\" {query.Amount}"); executionName = i.Name; }
            }
            else if (query.Action == "SpawnMob")
            {
                NPC? n = HavocIndex.QueryMob(query, tier);
                if (n != null) { commands.Add($"/spawnmob {n.type} {query.Amount} {{tx}} {{ty}}"); executionName = n.FullName; }
            }
        }
        else commands.AddRange(action.Event.TShockCommands);

        if (commands.Count == 0) return;

        foreach (var cmd in commands)
        {
            string finalCmd = cmd.Replace("{user}", action.Username).Replace("{player}", target.Name)
                                 .Replace("{tx}", target.TileX.ToString()).Replace("{ty}", target.TileY.ToString());
            Commands.HandleCommand(TSPlayer.Server, finalCmd.TrimStart('/'));
        }
        TSPlayer.All.SendMessage($"✨ {action.Username} materialized: {executionName}!", 180, 32, 240);
    }

    private void PerformJitReroll(QueuedAction action, TSPlayer target)
    {
        var validEvents = action.ParentPool.Events.Where(ev => IsProgressionValid(ev) && IsSituationallyValid(ev, target)).ToList();
        if (validEvents.Count > 0) {
            int roll = Random.Shared.Next(0, validEvents.Sum(ev => ev.Weight));
            foreach (var ev in validEvents) { if (roll < ev.Weight) { action.Event = ev; break; } roll -= ev.Weight; }
            if (_client?.IsConnected == true) _client.SendMessage(_config.TwitchChannelName, $"♻️ @{action.Username}, the world resisted. Manifestation shifted!");
        } else _actionQueue.TryDequeue(out _);
    }

    private bool IsProgressionValid(ChaosEvent e)
    {
        if (e.MinimumProgression == "Hardmode" && !Main.hardMode) return false;
        if (e.MinimumProgression == "PostPlantera" && !NPC.downedPlantBoss) return false;
        if (e.MaximumProgression == "PreHardmode" && Main.hardMode) return false;
        return true;
    }

    private bool IsSituationallyValid(ChaosEvent e, TSPlayer target)
    {
        if (e.BlockedIfFullHealth && target.TPlayer.statLife >= target.TPlayer.statLifeMax2) return false;
        if (e.BlockedIfBossAlive && Main.npc.Any(n => n?.active == true && n.boss)) return false;
        if (e.BlockedIfEventActive && (Main.bloodMoon || Main.eclipse || (Main.slimeRain && Main.netMode != 0))) return false;
        if (e.BlockedByBuffIDs.Any(id => target.TPlayer.buffType.Contains(id))) return false;
        return true;
    }
    #endregion
}
