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
    public override Version Version => new Version(5, 2, 0);
    public override string Author => "HistoryLabs";

    private string ConfigPath => Path.Combine(TShock.SavePath, "Havoc", "HavocConfig.json");
    private HavocConfig _config = new();

    private string? _targetAccountName;
    private bool _engineAwake = false;
    private TwitchClient? _client;

    private readonly ConcurrentDictionary<string, DateTime> _cooldowns = new();
    private readonly ConcurrentDictionary<string, DateTime> _activeConflicts = new();
    private readonly ConcurrentQueue<QueuedAction> _actionQueue = new();
    
    private int _tickCounter = 0;
    private bool _isProcessingQueue = false;

    public HavocPlugin(Main game) : base(game) 
    {
        // FIX: Attach resolver to the instance so it can be safely removed on Dispose to prevent memory leaks.
        AppDomain.CurrentDomain.AssemblyResolve += ResolveTwitchDependencies;
    }

    private System.Reflection.Assembly? ResolveTwitchDependencies(object? sender, ResolveEventArgs args)
    {
        string name = new System.Reflection.AssemblyName(args.Name).Name ?? "";
        if (!name.StartsWith("TwitchLib") && !name.Contains("ZstdSharp") && !name.Contains("Microsoft.Extensions.Logging"))
            return null;

        string resourceName = $"Havoc.Resources.{name}.dll";
        using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream == null) return null;

        byte[] data = new byte[stream.Length];
        stream.Read(data, 0, data.Length);
        return System.Reflection.Assembly.Load(data);
    }

    public override void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        LoadConfig();

        ServerApi.Hooks.GamePostInitialize.Register(this, (args) => HavocIndex.BuildIndex());
        ServerApi.Hooks.GameUpdate.Register(this, OnGameUpdate);
        
        Commands.ChatCommands.Add(new Command("havoc.admin", AdminCommand, "havoc"));
    }

    private void StartHavoc(TSPlayer player)
    {
        if (!player.IsLoggedIn) { player.SendErrorMessage("[Havoc] You must be logged in."); return; }
        _targetAccountName = player.Account.Name;
        
        if (!_engineAwake) 
        { 
            _engineAwake = true; 
            // FIX: Run connection asynchronously so it doesn't freeze the TShock server thread
            _ = Task.Run(() => ConnectTwitchAsync()); 
        }
        player.SendSuccessMessage($"[Havoc] Bound to '{_targetAccountName}'. Waiting for Twitch Sync...");
    }

    private void StopHavoc()
    {
        _targetAccountName = null; _engineAwake = false;
        _actionQueue.Clear(); _activeConflicts.Clear(); _tickCounter = 0;
        _client?.Disconnect(); _client = null;
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var text = File.ReadAllText(ConfigPath);
                _config = JsonSerializer.Deserialize<HavocConfig>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            }
            else
            {
                var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_config, options));
            }
        }
        catch (Exception ex) { TShock.Log.ConsoleError($"[Havoc] Config load failed: {ex.Message}"); }
    }

    private void AdminCommand(CommandArgs args)
    {
        if (args.Parameters.Count == 0) return;
        string cmd = args.Parameters[0].ToLower();
        if (cmd == "on") StartHavoc(args.Player);
        else if (cmd == "off") { StopHavoc(); args.Player.SendSuccessMessage("[Havoc] Deactivated."); }
        else if (cmd == "reload") { LoadConfig(); args.Player.SendSuccessMessage("[Havoc] Reloaded."); }
    }

    private void ConnectTwitchAsync()
    {
        if (string.IsNullOrWhiteSpace(_config.TwitchBotToken)) return;
        try
        {
            _client = new TwitchClient(new WebSocketClient(new ClientOptions { MessagesAllowedInPeriod = 20, ThrottlingPeriod = TimeSpan.FromSeconds(30) }));
            _client.Initialize(new ConnectionCredentials(_config.TwitchChannelName, _config.TwitchBotToken), _config.TwitchChannelName);
            
            _client.OnMessageReceived += (s, e) => {
                if (!_engineAwake) return;
                var pool = e.ChatMessage.Bits > 0 
                    ? _config.Manifestations.Where(p => p.TriggerType == "Bits" && e.ChatMessage.Bits >= p.MinimumBits).OrderByDescending(p => p.MinimumBits).FirstOrDefault()
                    : _config.Manifestations.FirstOrDefault(p => p.TriggerType == "Chat" && p.TriggerIdentifier.Equals(e.ChatMessage.Message.Trim().Split(' ')[0], StringComparison.OrdinalIgnoreCase));
                if (pool != null) AttemptManifestation(e.ChatMessage.Username, pool);
            };
            
            _client.OnDisconnected += (s, e) => Task.Delay(5000).ContinueWith(_ => { if (_engineAwake && !_client.IsConnected) _client.Connect(); });
            _client.Connect();
            
            TShock.Log.ConsoleInfo($"[Havoc] Twitch engine successfully synced to channel: {_config.TwitchChannelName}");
        }
        catch (Exception ex) { TShock.Log.ConsoleError($"[Havoc] Failed to connect to Twitch: {ex.Message}"); }
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

    private void OnGameUpdate(EventArgs args)
    {
        if (!_engineAwake) return;
        if (++_tickCounter >= 60) { _tickCounter = 0; ProcessSmartQueue(); }
    }

    private void ProcessSmartQueue()
    {
        if (_actionQueue.IsEmpty || _isProcessingQueue || _targetAccountName == null) return;
        _isProcessingQueue = true;

        try
        {
            var target = TShock.Players.FirstOrDefault(p => p != null && p.Active && p.IsLoggedIn && p.Account.Name == _targetAccountName);
            if (target == null || target.X <= 0 || float.IsNaN(target.X)) return;

            if (_actionQueue.TryPeek(out var next))
            {
                if (!IsSituationallyValid(next.Event, target)) { PerformJitReroll(next, target); return; }
                if (_activeConflicts.TryGetValue(next.Event.ConflictGroup, out var expiry) && DateTime.UtcNow < expiry) return;
                if (next.Event.RequiresTargetAlive && target.TPlayer.dead) return;

                if (next.ParentPool.Tier.Equals("Minor", StringComparison.OrdinalIgnoreCase))
                {
                    int count = 0;
                    while (count++ < 5 && _actionQueue.TryDequeue(out var item)) {
                        ExecuteAction(item, target);
                        if (_actionQueue.TryPeek(out var up) && !up.ParentPool.Tier.Equals("Minor")) break;
                    }
                }
                else if (_actionQueue.TryDequeue(out var solo)) ExecuteAction(solo, target);
            }
        }
        finally { _isProcessingQueue = false; }
    }

    private void ExecuteAction(QueuedAction action, TSPlayer? target)
    {
        if (target == null) return;
        
        List<string> commands = new();
        string name = action.Event.Name;

        if (action.Event.DynamicQuery != null) {
            var q = action.Event.DynamicQuery;
            if (q.Action == "GiveItem") {
                var i = HavocIndex.QueryItem(q, HavocIndex.GetCurrentWorldTier());
                if (i != null) { commands.Add($"/give {i.type} \"{{player}}\" {q.Amount}"); name = i.Name; }
            } else if (q.Action == "SpawnMob") {
                var n = HavocIndex.QueryMob(q, HavocIndex.GetCurrentWorldTier());
                if (n != null) { commands.Add($"/spawnmob {n.type} {q.Amount} {{tx}} {{ty}}"); name = n.FullName; }
            }
        } else commands.AddRange(action.Event.TShockCommands);

        foreach (var cmd in commands) {
            string final = cmd.Replace("{user}", action.Username).Replace("{player}", target.Name)
                              .Replace("{tx}", target.TileX.ToString()).Replace("{ty}", target.TileY.ToString());
            Commands.HandleCommand(TSPlayer.Server, final.TrimStart('/'));
        }
        
        if (action.Event.QueueDurationSeconds > 0)
            _activeConflicts[action.Event.ConflictGroup] = DateTime.UtcNow.AddSeconds(action.Event.QueueDurationSeconds);

        TSPlayer.All.SendMessage($"✨ {action.Username} materialized: {name}!", 180, 32, 240);
    }

    private void PerformJitReroll(QueuedAction action, TSPlayer target)
    {
        var valid = action.ParentPool.Events.Where(ev => IsProgressionValid(ev) && IsSituationallyValid(ev, target)).ToList();
        if (valid.Count > 0) {
            action.Event = valid[Random.Shared.Next(valid.Count)];
            if (_client?.IsConnected == true) _client.SendMessage(_config.TwitchChannelName, $"♻️ @{action.Username}, the world resisted. Manifestation shifted!");
        } else _actionQueue.TryDequeue(out _);
    }

    private bool IsProgressionValid(ChaosEvent e) {
        if (e.MinimumProgression == "Hardmode" && !Main.hardMode) return false;
        if (e.MaximumProgression == "PreHardmode" && Main.hardMode) return false;
        return true;
    }

    private bool IsSituationallyValid(ChaosEvent e, TSPlayer target) {
        if (e.BlockedIfFullHealth && target.TPlayer.statLife >= target.TPlayer.statLifeMax2) return false;
        if (e.BlockedIfBossAlive && Main.npc.Any(n => n?.active == true && n.boss)) return false;
        if (e.BlockedByBuffIDs.Any(id => target.TPlayer.buffType.Contains(id))) return false;
        return true;
    }

    protected override void Dispose(bool disposing) 
    { 
        if (disposing) 
        { 
            ServerApi.Hooks.GameUpdate.Deregister(this, OnGameUpdate); 
            
            // FIX: Deregister the Assembly Resolver to seal the memory leak
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveTwitchDependencies;
            
            StopHavoc(); 
        } 
        base.Dispose(disposing); 
    }
}
