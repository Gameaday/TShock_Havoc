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
    public string Username { get; set; } = "";
}

[ApiVersion(2, 1)]
public class HavocPlugin : TerrariaPlugin
{
    public override string Name => "Havoc Engine";
    public override Version Version => new Version(3, 0, 0);
    public override string Author => "HistoryLabs";

    private string ConfigPath => Path.Combine(TShock.SavePath, "Havoc", "HavocConfig.json");
    private HavocConfig _config = new();
    private TwitchClient? _client;
    private bool _isActive = false;

    // State & Queue Management
    private readonly ConcurrentDictionary<string, DateTime> _cooldowns = new();
    private readonly ConcurrentDictionary<string, DateTime> _activeConflicts = new();
    private readonly ConcurrentQueue<QueuedManifestation> _actionQueue = new();
    private Timer? _queueProcessorTimer;

    public HavocPlugin(Main game) : base(game) { }

    public override void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        LoadConfig();

        Commands.ChatCommands.Add(new Command("havoc.admin", AdminCommand, "havoc"));

        // The Smart Queue Processor (Ticks every 1 second)
        _queueProcessorTimer = new Timer(1000);
        _queueProcessorTimer.Elapsed += ProcessQueueTick;
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
            _queueProcessorTimer?.Start();

            player.SendSuccessMessage("[Havoc] The Engine is online and listening to Twitch.");
        }
        catch (Exception ex) { player.SendErrorMessage($"[Havoc] Ignition Failed: {ex.Message}"); }
    }

    // --- TWITCH ROUTER ---
    private void OnTwitchMessage(object? sender, TwitchLib.Client.Events.OnMessageReceivedArgs e)
    {
        string user = e.ChatMessage.Username;
        string msg = e.ChatMessage.Message.Trim();
        int bits = e.ChatMessage.Bits;

        ManifestationPool? pool = null;

        // 1. Route Bits (Highest Priority)
        if (bits > 0)
        {
            pool = _config.Manifestations
                .Where(p => p.TriggerType == "Bits" && bits >= p.MinimumBits)
                .OrderByDescending(p => p.MinimumBits)
                .FirstOrDefault();
        }
        // 2. Route Chat Commands
        else if (msg.StartsWith("!"))
        {
            string cmd = msg.Split(' ')[0];
            pool = _config.Manifestations.FirstOrDefault(p => 
                p.TriggerType == "Chat" && 
                p.TriggerIdentifier.Equals(cmd, StringComparison.OrdinalIgnoreCase));
        }

        if (pool != null) AttemptManifestation(user, pool);
    }

    // --- MANIFESTATION LOGIC ---
    private void AttemptManifestation(string user, ManifestationPool pool)
    {
        // Check Global Cooldown for this specific trigger
        if (_cooldowns.TryGetValue(pool.TriggerIdentifier, out var lastUsed) && (DateTime.UtcNow - lastUsed).TotalSeconds < pool.GlobalCooldownSeconds)
            return;

        // Filter out events that violate server progression
        var validEvents = pool.Events.Where(IsProgressionValid).ToList();
        if (validEvents.Count == 0) return;

        // Lock Cooldown
        _cooldowns[pool.TriggerIdentifier] = DateTime.UtcNow;

        // Roll the Dice for the specific event
        int totalWeight = validEvents.Sum(e => e.Weight);
        int roll = Random.Shared.Next(0, totalWeight);
        ChaosEvent? selected = validEvents.Last();

        foreach (var e in validEvents)
        {
            if (roll < e.Weight) { selected = e; break; }
            roll -= e.Weight;
        }

        var queuedAction = new QueuedManifestation { Event = selected, Username = user };

        // Route to execution or queue
        if (pool.BypassQueue) 
        {
            ExecuteEvent(queuedAction); // VIP Instant Execution
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
        if (_actionQueue.IsEmpty) return;

        // Peek at the next item in line
        if (_actionQueue.TryPeek(out var nextAction))
        {
            string group = nextAction.Event.ConflictGroup;

            // Is this conflict group currently locked?
            if (_activeConflicts.TryGetValue(group, out var lockedUntil) && DateTime.UtcNow < lockedUntil)
            {
                return; // Queue is stalled waiting for the group to clear
            }

            // If clear, dequeue and execute
            if (_actionQueue.TryDequeue(out var actionToRun))
            {
                // Lock the group for the specified duration
                if (actionToRun.Event.QueueDurationSeconds > 0)
                {
                    _activeConflicts[group] = DateTime.UtcNow.AddSeconds(actionToRun.Event.QueueDurationSeconds);
                }
                
                ExecuteEvent(actionToRun);
            }
        }
    }

    private void ExecuteEvent(QueuedManifestation action)
    {
        TShock.Utils.NextTick(() => {
            foreach (var cmd in action.Event.TShockCommands)
                Commands.HandleCommand(TSPlayer.Server, cmd.Replace("{user}", action.Username));
                
            TSPlayer.All.SendMessage($"✨ Twitch chat ({action.Username}) invoked: {action.Event.Name}!", new Microsoft.Xna.Framework.Color(180, 32, 240));
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
        
        if (cmd == "start") StartEngine(args.Player);
        else if (cmd == "stop") { _isActive = false; _queueProcessorTimer?.Stop(); _client?.Disconnect(); }
        else if (cmd == "clearqueue") { _actionQueue.Clear(); args.Player.SendSuccessMessage("Havoc queue cleared."); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _queueProcessorTimer?.Dispose();
            _client?.Disconnect();
        }
        base.Dispose(disposing);
    }
}
