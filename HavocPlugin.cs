private void ProcessTwitchCommand(string username, string message, bool isReward)
    {
        if (!_isActive) return;

        string[] args = message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (args.Length == 0) return;

        // 1. Find the requested Pool (Help, Harm, Annoy, etc.)
        var pool = _config.Pools.FirstOrDefault(p => 
            p.TriggerCommand.Equals(args[0], StringComparison.OrdinalIgnoreCase) && 
            p.IsRewardRedemption == isReward);

        if (pool == null || pool.Events.Count == 0) return;

        // 2. Cooldown Enforcement
        string cooldownKey = $"{username}_{pool.TriggerCommand}";
        if (_cooldowns.TryGetValue(cooldownKey, out var lastUsed) && (DateTime.UtcNow - lastUsed).TotalSeconds < pool.CooldownSeconds)
            return;

        _cooldowns[cooldownKey] = DateTime.UtcNow;

        // 3. Progression Filtering (The Safety Net)
        var validEvents = pool.Events.Where(e => IsEventValidForCurrentProgression(e)).ToList();
        
        if (validEvents.Count == 0) 
        {
            TShock.Log.ConsoleWarn($"[Havoc] Pool '{pool.TriggerCommand}' triggered, but no events matched current server progression.");
            return; 
        }

        // 4. Roll the Dice
        var selectedEvent = validEvents[Random.Shared.Next(validEvents.Count)];
        string targetPlayer = GetInGameNameForStreamer() ?? "Server";

        // 5. Execute the Commands
        TShock.Utils.NextTick(() => {
            foreach (var baseCmd in selectedEvent.TShockCommands)
            {
                string finalCmd = baseCmd.Replace("{user}", username)
                                         .Replace("{player}", targetPlayer);
                
                Commands.HandleCommand(TSPlayer.Server, finalCmd.TrimStart('/'));
            }
            
            // Optional: Broadcast what happened so chat knows who won the roll
            TSPlayer.All.SendMessage($"🎲 Twitch chat ({username}) triggered: {selectedEvent.Name}!", new Microsoft.Xna.Framework.Color(180, 32, 240));
        });
    }

    // --- THE PROGRESSION LOGIC ---
    private bool IsEventValidForCurrentProgression(ChaosEvent chaosEvent)
    {
        // Check Minimums
        if (chaosEvent.MinimumProgression.Equals("Hardmode", StringComparison.OrdinalIgnoreCase) && !Main.hardMode) return false;
        if (chaosEvent.MinimumProgression.Equals("PostPlantera", StringComparison.OrdinalIgnoreCase) && !NPC.downedPlantBoss) return false;
        if (chaosEvent.MinimumProgression.Equals("PostMoonLord", StringComparison.OrdinalIgnoreCase) && !NPC.downedMoonlord) return false;

        // Check Maximums (Prevents baby slimes from spawning when you are fighting Moon Lord)
        if (chaosEvent.MaximumProgression.Equals("PreHardmode", StringComparison.OrdinalIgnoreCase) && Main.hardMode) return false;
        if (chaosEvent.MaximumProgression.Equals("PrePlantera", StringComparison.OrdinalIgnoreCase) && NPC.downedPlantBoss) return false;

        return true; 
    }
