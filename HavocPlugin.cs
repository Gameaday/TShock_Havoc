using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Timers;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TwitchLib.Client;
using TwitchLib.Client.Models;
using TwitchLib.Communication.Clients;

#nullable enable

namespace Havoc;

public class QueuedAction
{
    public ChaosEvent Event { get; set; } = new();
    public ManifestationPool ParentPool { get; set; } = new();
    public string Username { get; set; } = "";
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
}

[ApiVersion(2, 1)]
public class HavocPlugin : TerrariaPlugin
{
    public override string Name => "Havoc Engine Pro";
    public override Version Version => new Version(5, 0, 0);
    public override string Author => "HistoryLabs";

    private string ConfigPath => Path.Combine(TShock.SavePath, "Havoc", "HavocConfig.json");
    private HavocConfig _config = new();

    private string? _targetAccountName;
    private bool _engineAwake = false;
    private TwitchClient? _client;

    private readonly ConcurrentDictionary<string, DateTime> _cooldowns = new();
    private readonly ConcurrentDictionary<string, DateTime> _activeConflicts = new();
    private readonly ConcurrentQueue<QueuedAction> _actionQueue = new();
    private Timer? _queueTick;
    private bool _isProcessingQueue = false;

    public HavocPlugin(Main game) : base(game) { }

    #region LIFECYCLE & COMMANDS

    public override void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        LoadConfig();

        // Build the Semantic Index after Terraria data is fully loaded
        ServerApi.Hooks.GamePostInitialize.Register(this, (args) => HavocIndex.BuildIndex());
        
        Commands.ChatCommands.Add(new Command("havoc.admin", AdminCommand, "havoc"));

        _queueTick = new Timer(1000);
        _queueTick.Elapsed += ProcessSmartQueue;
    }

    private void StartHavoc(TSPlayer player)
    {
        if (!player.IsLoggedIn) { player.SendErrorMessage("[Havoc] You must be logged in to be the target."); return; }
        
        _targetAccountName = player.Account.Name;
        if (!_engineAwake)
        {
            ConnectTwitch();
            _queueTick?.Start();
            _engineAwake = true;
        }
        player.SendSuccessMessage($"[Havoc] Session bound to '{_targetAccountName}'.");
    }

    private void StopHavoc()
    {
        _targetAccountName = null;
        _engineAwake = false;
        _actionQueue.Clear();
        _activeConflicts.Clear();
        _queueTick?.Stop();
        _client?.Disconnect();
        _client = null;
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
        else if (cmd == "off") { StopHavoc(); args.Player.SendSuccessMessage("[Havoc] Engine deactivated."); }
        else if (cmd == "clearqueue") { _actionQueue.Clear(); args.Player.SendSuccessMessage("[Havoc] Queue wiped."); }
        else if (cmd == "reload") { LoadConfig(); args.Player.SendSuccessMessage("[Havoc] Config reloaded."); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) StopHavoc();
        base.Dispose(disposing);
    }

    #endregion

    #region TWITCH ENGINE

    private void ConnectTwitch()
    {
        if (string.IsNullOrWhiteSpace(_config.TwitchBotToken)) return;

        var creds = new ConnectionCredentials(_config.TwitchChannelName, _config.TwitchBotToken);
        _client = new TwitchClient(new WebSocketClient(new ClientOptions { MessagesAllowedInPeriod = 20, ThrottlingPeriod = TimeSpan.FromSeconds(30) }));
        _client.Initialize(creds, _config.TwitchChannelName);
        
        _client.OnDisconnected += (s, e) => {
            Task.Delay(5000).ContinueWith(_ => { if (_engineAwake && !_client.IsConnected) _client.Connect(); });
        };

        _client.OnMessageReceived += OnTwitchMessage;
        _client.Connect();
    }

    private void OnTwitchMessage(object? sender, TwitchLib.Client.Events.OnMessageReceivedArgs e)
    {
        if (!_engineAwake) return;

        ManifestationPool? pool = null;
        if (e.ChatMessage.Bits > 0)
            pool = _config.Manifestations.Where(p => p.TriggerType == "Bits" && e.ChatMessage.Bits >= p.MinimumBits).OrderByDescending(p => p.MinimumBits).FirstOrDefault();
        else if (e.ChatMessage.Message.Trim().StartsWith("!"))
            pool = _config.Manifestations.FirstOrDefault(p => p.TriggerType == "Chat" && p.TriggerIdentifier.Equals(e.ChatMessage.Message.Trim().Split(' ')[0], StringComparison.OrdinalIgnoreCase));

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

        foreach (var e in validEvents)
        {
            if (roll < e.Weight) { selected = e; break; }
            roll -= e.Weight;
        }

        var queuedAction = new QueuedAction { Event = selected, ParentPool = pool, Username = user };

        if (pool.BypassQueue) ExecuteCommands(queuedAction, TShock.Players.FirstOrDefault(p => p?.Account?.Name == _targetAccountName)); 
        else _actionQueue.Enqueue(queuedAction);
    }

    private void SendMessageToTwitch(string msg) { if (_client?.IsConnected == true) _client.SendMessage(_config.TwitchChannelName, msg); }

    #endregion

    #region THE SMART QUEUE & GAME LOGIC

    private void ProcessSmartQueue(object? sender, ElapsedEventArgs e)
    {
        if (_actionQueue.IsEmpty || _isProcessingQueue || !_engineAwake || _targetAccountName == null) return;
        _isProcessingQueue = true;

        try
        {
            var target = TShock.Players.FirstOrDefault(p => p != null && p.Active && p.IsLoggedIn && p.Account.Name.Equals(_targetAccountName, StringComparison.OrdinalIgnoreCase));
            if (target == null || HavocLibrary.IsInInvalidState(target)) return; // Stalled: Offline or transitioning

            List<QueuedAction> toExecute = new();

            if (_actionQueue.TryPeek(out var nextAction))
            {
                // JIT Re-Roll Engine
                if (!IsSituationallyValid(nextAction.Event, target))
                {
                    PerformJitReroll(nextAction, target);
                    return; // Yield tick to re-evaluate
                }

                if (_activeConflicts.TryGetValue(nextAction.Event.ConflictGroup, out var expiry) && DateTime.UtcNow < expiry) return; 
                if (nextAction.Event.RequiresTargetAlive && target.TPlayer.dead) return; 

                // Batching Logic
                if (nextAction.ParentPool.Tier.Equals("Minor", StringComparison.OrdinalIgnoreCase))
                {
                    while (toExecute.Count < 5 && _actionQueue.TryDequeue(out var batchItem))
                    {
                        toExecute.Add(batchItem);
                        if (_actionQueue.TryPeek(out var upcoming) && !upcoming.ParentPool.Tier.Equals("Minor", StringComparison.OrdinalIgnoreCase)) break;
                    }
                }
                else if (_actionQueue.TryDequeue(out var soloItem)) toExecute.Add(soloItem);
            }

            if (toExecute.Count > 0)
            {
                TShock.Utils.NextTick(() => {
                    foreach (var action in toExecute)
                    {
                        ExecuteCommands(action, target);
                        if (action.Event.QueueDurationSeconds > 0)
                            _activeConflicts[action.Event.ConflictGroup] = DateTime.UtcNow.AddSeconds(action.Event.QueueDurationSeconds);
                    }
                });
            }
        }
        catch (Exception ex) { TShock.Log.ConsoleError($"[Havoc] Queue Error: {ex.Message}"); }
        finally { _isProcessingQueue = false; }
    }

    private void PerformJitReroll(QueuedAction action, TSPlayer target)
    {
        var validEvents = action.ParentPool.Events.Where(ev => IsProgressionValid(ev) && IsSituationallyValid(ev, target)).ToList();
        if (validEvents.Count > 0)
        {
            int roll = Random.Shared.Next(0, validEvents.Sum(ev => ev.Weight));
            foreach (var ev in validEvents) { if (roll < ev.Weight) { action.Event = ev; break; } roll -= ev.Weight; }
            SendMessageToTwitch($"♻️ @{action.Username}, the world resisted. Manifestation shifted to: '{action.Event.Name}'!");
        }
        else
        {
            _actionQueue.TryDequeue(out _);
        }
    }

    private void ExecuteCommands(QueuedAction action, TSPlayer? target)
    {
        if (target == null) return;

        List<string> commandsToRun = new();

        // SEMANTIC RESOLUTION
        if (action.Event.DynamicQuery != null)
            commandsToRun.AddRange(HavocIndex.ResolveQuery(action.Event.DynamicQuery, target.Name, target.TileX, target.TileY));
        else // FALLBACK
            commandsToRun.AddRange(action.Event.TShockCommands);

        foreach (var cmd in commandsToRun)
        {
            string finalCmd = cmd.Replace("{user}", action.Username)
                                 .Replace("{player}", target.Name)
                                 .Replace("{tx}", target.TileX.ToString())
                                 .Replace("{ty}", target.TileY.ToString());
            Commands.HandleCommand(TSPlayer.Server, finalCmd.TrimStart('/'));
        }
        
        TSPlayer.All.SendMessage($"✨ {action.Username} invoked: {action.Event.Name}!", new Microsoft.Xna.Framework.Color(180, 32, 240));
    }

    private bool IsProgressionValid(ChaosEvent e)
    {
        if (e.MinimumProgression.Equals("Hardmode", StringComparison.OrdinalIgnoreCase) && !Main.hardMode) return false;
        if (e.MinimumProgression.Equals("PostPlantera", StringComparison.OrdinalIgnoreCase) && !NPC.downedPlantBoss) return false;
        if (e.MaximumProgression.Equals("PreHardmode", StringComparison.OrdinalIgnoreCase) && Main.hardMode) return false;
        return true;
    }

    private bool IsSituationallyValid(ChaosEvent e, TSPlayer target)
    {
        if (e.BlockedIfFullHealth && target.TPlayer.statLife >= target.TPlayer.statLifeMax2) return false;
        if (e.BlockedIfBossAlive && HavocLibrary.IsAnyBossActive()) return false;
        if (e.BlockedIfEventActive && HavocLibrary.IsWorldEventActive()) return false;
        if (e.BlockedByBuffIDs.Any(id => target.TPlayer.buffType.Contains(id))) return false;
        return true;
    }

    #endregion
}
