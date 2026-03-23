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
    public ManifestationPool Pool { get; set; } = new();
    public string Username { get; set; } = "";
}

[ApiVersion(2, 1)]
public class HavocPlugin : TerrariaPlugin
{
    public override string Name => "Havoc Engine";
    public override Version Version => new Version(4, 1, 0);
    public override string Author => "HistoryLabs";

    private string ConfigPath => Path.Combine(TShock.SavePath, "Havoc", "HavocConfig.json");
    private HavocConfig _config = new();

    // Session State
    private string? _targetAccountName;
    private bool _engineAwake = false;

    // Twitch State
    private TwitchClient? _client;

    // Queue State
    private readonly ConcurrentDictionary<string, DateTime> _cooldowns = new();
    private readonly ConcurrentDictionary<string, DateTime> _activeConflicts = new();
    private readonly ConcurrentQueue<QueuedManifestation> _actionQueue = new();
    private Timer? _queueTick;
    private bool _isProcessingQueue = false;

    public HavocPlugin(Main game) : base(game) { }

    #region LIFECYCLE & COMMANDS

    public override void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        LoadConfig();

        Commands.ChatCommands.Add(new Command("havoc.admin", AdminCommand, "havoc"));

        _queueTick = new Timer(1000);
        _queueTick.Elapsed += ProcessQueueTick;
    }

    private void StartHavoc(TSPlayer player)
    {
        if (!player.IsLoggedIn)
        {
            player.SendErrorMessage("[Havoc] You must be logged into a TShock account to become the target.");
            return;
        }

        _targetAccountName = player.Account.Name;
        
        if (!_engineAwake)
        {
            ConnectTwitch();
            _queueTick?.Start();
            _engineAwake = true;
        }

        player.SendSuccessMessage($"[Havoc] Session bound to account '{_targetAccountName}'.");
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
        var clientOptions = new ClientOptions { MessagesAllowedInPeriod = 20, ThrottlingPeriod = TimeSpan.FromSeconds(30) };
        WebSocketClient customClient = new WebSocketClient(clientOptions);
        
        _client = new TwitchClient(customClient);
        _client.Initialize(creds, _config.TwitchChannelName);
        
        _client.OnDisconnected += (s, e) => {
            TShock.Log.ConsoleWarn("[Havoc] Lost connection to Twitch. Reconnecting...");
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
                p.TriggerType == "Chat" && p.TriggerIdentifier.Equals(cmd, StringComparison.OrdinalIgnoreCase));
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

        var queuedAction = new QueuedManifestation { Event = selected, Pool = pool, Username = user };

        if (pool.BypassQueue) ExecuteEvent(queuedAction, _targetAccountName ?? "Server"); 
        else _actionQueue.Enqueue(queuedAction);
    }

    private void SendMessageToTwitch(string msg)
    {
        if (_client != null && _client.IsConnected)
            _client.SendMessage(_config.TwitchChannelName, msg);
    }

    #endregion

    #region THE SMART QUEUE & GAME LOGIC

    private void ProcessQueueTick(object? sender, ElapsedEventArgs e)
    {
        if (_actionQueue.IsEmpty || _isProcessingQueue || !_engineAwake || _targetAccountName == null) return;
        _isProcessingQueue = true;

        try
        {
            var target = TShock.Players.FirstOrDefault(p => 
                p != null && p.Active && p.IsLoggedIn && 
                p.Account.Name.Equals(_targetAccountName, StringComparison.OrdinalIgnoreCase));

            if (target == null) return; // Target offline: Stalls the queue infinitely until return

            if (_actionQueue.TryPeek(out var nextAction))
            {
                // 1. THE RE-ROLL ENGINE
                if (!IsSituationallyValid(nextAction.Event, target))
                {
                    var validEvents = nextAction.Pool.Events
                        .Where(ev => IsProgressionValid(ev) && IsSituationallyValid(ev, target)).ToList();

                    if (validEvents.Count > 0)
                    {
                        int totalWeight = validEvents.Sum(ev => ev.Weight);
                        int roll = Random.Shared.Next(0, totalWeight);
                        ChaosEvent? selected = validEvents.Last();

                        foreach (var ev in validEvents)
                        {
                            if (roll < ev.Weight) { selected = ev; break; }
                            roll -= ev.Weight;
                        }

                        nextAction.Event = selected; // Mutate for next tick
                        SendMessageToTwitch($"♻️ @{nextAction.Username}, the world resisted. Your manifestation shifted into '{selected.Name}'!");
                        return; 
                    }
                    else
                    {
                        _actionQueue.TryDequeue(out _); // Discard entirely
                        SendMessageToTwitch($"❌ @{nextAction.Username}, you tried to help, but they are already at maximum power. (Redemption dropped)");
                        return; 
                    }
                }

                // 2. CONFLICT CHECK
                string group = nextAction.Event.ConflictGroup;
                if (_activeConflicts.TryGetValue(group, out var expiry) && DateTime.UtcNow < expiry)
                    return; 

                // 3. CORPSE LOCK
                if (nextAction.Event.RequiresTargetAlive && target.TPlayer.dead)
                    return; 

                // 4. EXECUTION
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
            TSPlayer.All.SendMessage($"✨ Twitch Chat ({action.Username}) invoked: {action.Event.Name}!", new Microsoft.Xna.Framework.Color(180, 32, 24
