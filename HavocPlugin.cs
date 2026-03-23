using System;
using System.Collections.Concurrent;
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
using TwitchLib.Communication.Models;

#nullable enable

namespace Havoc;

public class QueuedManifestation
{
    public ChaosEvent Event { get; set; } = new();
    public ManifestationPool Pool { get; set; } = new(); // Carries the parent pool
    public string Username { get; set; } = "";
}

[ApiVersion(2, 1)]
public class HavocPlugin : TerrariaPlugin
{
    public override string Name => "Havoc Engine";
    public override Version Version => new Version(4, 0, 0);
    public override string Author => "HistoryLabs";

    private string ConfigPath => Path.Combine(TShock.SavePath, "Havoc", "HavocConfig.json");
    private HavocConfig _config = new();
    private TwitchClient? _client;

    // --- SESSION STATE ---
    private string? _targetAccountName;
    private bool _engineAwake = false;

    // --- QUEUE STATE ---
    private readonly ConcurrentDictionary<string, DateTime> _cooldowns = new();
    private readonly ConcurrentDictionary<string, DateTime> _activeConflicts = new();
    private readonly ConcurrentQueue<QueuedManifestation> _actionQueue = new();
    private Timer? _queueProcessorTimer;
    private bool _isProcessingQueue = false;

    public HavocPlugin(Main game) : base(game) { }

    public override void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        LoadConfig();

        Commands.ChatCommands.Add(new Command("havoc.admin", AdminCommand, "havoc"));

        _queueProcessorTimer = new Timer(1000);
        _queueProcessorTimer.Elapsed += ProcessQueueTick;
    }

    // --- LIFECYCLE CONTROLS ---

    private void StartHavoc(TSPlayer player)
    {
        if (!player.IsLoggedIn)
        {
            player.SendErrorMessage("[Havoc] You must be logged into an account to become the target.");
            return;
        }

        _targetAccountName = player.Account.Name;
        
        if (!_engineAwake)
        {
            ConnectTwitch();
            _queueProcessorTimer?.Start();
            _engineAwake = true;
        }

        player.SendSuccessMessage($"[Havoc] Session bound to account '{_targetAccountName}'. The engine is awake and will wait for you if you disconnect.");
    }

    private void StopHavoc()
    {
        _targetAccountName = null;
        _engineAwake = false;
        
        _actionQueue.Clear();
        _activeConflicts.Clear();
        _queueProcessorTimer?.Stop();
        
        _client?.Disconnect();
        _client = null;
    }

    // --- TWITCH ENGINE ---

    private void ConnectTwitch()
    {
        if (string.IsNullOrWhiteSpace(_config.TwitchBotToken)) return;

        var creds = new ConnectionCredentials(_config.TwitchChannelName, _config.TwitchBotToken);
        var clientOptions = new ClientOptions { MessagesAllowedInPeriod = 20, ThrottlingPeriod = TimeSpan.FromSeconds(30) };
        WebSocketClient customClient = new WebSocketClient(clientOptions);
        
        _client = new TwitchClient(customClient);
        _client.Initialize(creds, _config.TwitchChannelName);
        
        // Resiliency Hooks
        _client.OnDisconnected += (s, e) => {
            TShock.Log.ConsoleWarn("[Havoc] Lost connection to Twitch. Attempting to reconnect...");
            Task.Delay(5000).ContinueWith(_ => { if (_engineAwake && !_client.IsConnected) _client.Connect(); });
        };

        _client.OnMessageReceived += OnTwitchMessage;
        _client.Connect();
    }

    private void OnTwitchMessage(object? sender, TwitchLib.Client.Events.OnMessageReceivedArgs e)
    {
        if (!_engineAwake) return;

        string user = e.ChatMessage.Username;
        string msg = e.ChatMessage.Message.Trim();
        int bits = e.ChatMessage.Bits;

        ManifestationPool? pool = null;

        if (bits > 0)
        {
            pool = _config.Manifestations
                .Where(p => p.TriggerType == "Bits" && bits >= p.MinimumBits)
                .OrderByDescending(p => p.MinimumBits)
                .FirstOrDefault();
        }
        else if (msg.StartsWith("!"))
        {
            string cmd = msg.Split(' ')[0];
            pool = _config.Manifestations.FirstOrDefault(p => 
                p.TriggerType == "Chat" && 
                p.TriggerIdentifier.Equals(cmd, StringComparison.OrdinalIgnoreCase));
        }

        if (pool != null) AttemptManifestation(user, pool);
    }

    private void AttemptManifestation(string user, ManifestationPool pool)
    {
        if (_cooldowns.TryGetValue(pool.TriggerIdentifier, out var lastUsed) && (DateTime.UtcNow - lastUsed).TotalSeconds < pool.GlobalCooldownSeconds)
            return;

        var validEvents = pool.Events.Where(IsProgressionValid).ToList();
        if (validEvents.Count == 0) return;

        _cooldowns[pool.TriggerIdentifier] = DateTime.UtcNow;

        int totalWeight = validEvents.Sum(e => e.Weight);
        int roll = Random.Shared.Next(0, totalWeight);
        ChaosEvent? selected = validEvents.Last();

        foreach (var e in validEvents)
        {
            if (roll < e.Weight) { selected = e; break; }
            roll -= e.Weight;
        }

        var queuedAction = new QueuedManifestation { Event = selected, Username = user };

        if (pool.BypassQueue) 
        {
            // Execute immediately, skipping conflict checks (For massive Bit bombs)
            ExecuteEvent(queuedAction, _targetAccountName ?? "Server"); 
        }
        else 
        {
            _actionQueue.Enqueue(queuedAction);
            SendMessageToTwitch($"@{user}, your manifestation '{selected.Name}' has been added to the queue!");
        }
    }

    // --- THE SMART QUEUE PROCESSOR ---

    private void ProcessQueueTick(object? sender, ElapsedEventArgs e)
    {
        if (_actionQueue.IsEmpty || _isProcessingQueue || !_engineAwake || _targetAccountName == null) return;
        _isProcessingQueue = true;

        try
        {
            // 1. Identity Resolution
            var target = TShock.Players.FirstOrDefault(p => 
                p != null && p.Active && p.IsLoggedIn && 
                p.Account.Name.Equals(_targetAccountName, StringComparison.OrdinalIgnoreCase));

            if (target == null) return; // Streamer offline/disconnected. Wait infinitely.

            if (_actionQueue.TryPeek(out var nextAction))
            {
                string group = nextAction.Event.ConflictGroup;

                // 2. Conflict Group Check
                if (_activeConflicts.TryGetValue(group, out var lockedUntil) && DateTime.UtcNow < lockedUntil)
                    return; // Stalled waiting for active group

                // 3. Game State Check (The Corpse Lock)
                if (nextAction.Event.RequiresTargetAlive && target.TPlayer.dead)
                    return; // Stalled waiting for respawn

                // 4. Execute
                if (_actionQueue.TryDequeue(out var actionToRun))
                {
                    if (actionToRun.Event.QueueDurationSeconds > 0)
                        _activeConflicts[group] = DateTime.UtcNow.AddSeconds(actionToRun.Event.QueueDurationSeconds);
                    
                    ExecuteEvent(actionToRun, target.Name);
                }
            }
        }
        catch (Exception ex) { TShock.Log.ConsoleError($"[Havoc] Queue Error: {ex.Message}"); }
        finally { _isProcessingQueue = false; }
    }

    private void ExecuteEvent(QueuedManifestation action, string targetName)
    {
        TShock.Utils.NextTick(() => {
            foreach (var cmd in action.Event.TShockCommands)
            {
                string finalCmd = cmd.Replace("{user}", action.Username).Replace("{player}", targetName);
                Commands.HandleCommand(TSPlayer.Server, finalCmd.TrimStart('/'));
            }
                
            TSPlayer.All.SendMessage($"✨ {action.Username} invoked: {action.Event.Name}!", new Microsoft.Xna.Framework.Color(180, 32, 240));
        });
    }

    private bool IsProgressionValid(ChaosEvent e)
    {
        if (e.MinimumProgression.Equals("Hardmode", StringComparison.OrdinalIgnoreCase) && !Main.hardMode) return false;
        if (e.MinimumProgression.Equals("PostPlantera", StringComparison.OrdinalIgnoreCase) && !NPC.downedPlantBoss) return false;
        if (e.MaximumProgression.Equals("PreHardmode", StringComparison.OrdinalIgnoreCase) && Main.hardMode) return false;
        return true;
    }

    // --- UTILITIES ---

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
        
        if (cmd == "on") StartHavoc(args.Player);
        else if (cmd == "off") { StopHavoc(); args.Player.SendSuccessMessage("[Havoc] Engine deactivated."); }
        else if (cmd == "clearqueue") { _actionQueue.Clear(); args.Player.SendSuccessMessage("[Havoc] Queue cleared."); }
        else if (cmd == "reload") { LoadConfig(); args.Player.SendSuccessMessage("[Havoc] Config reloaded."); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) StopHavoc();
        base.Dispose(disposing);
    }
}
