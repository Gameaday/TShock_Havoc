using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;

namespace Havoc;

#region STATIC METADATA
public static class HavocLibrary
{
    public static bool IsInInvalidState(TSPlayer player) => 
        player.X <= 0 || player.Y <= 0 || float.IsNaN(player.X) || float.IsNaN(player.Y);

    public static bool IsAnyBossActive() => 
        Main.npc.Any(n => n != null && n.active && (n.boss || NPC.Targeting_NPC_Boss));

    public static bool IsWorldEventActive() => 
        Main.bloodMoon || Main.eclipse || Main.snowMoon || Main.pumpkinMoon || Main.slimeRain;
}
#endregion

#region SEMANTIC ENGINE (REFLECTION)
public static class HavocIndex
{
    public static List<Item> Weapons = new();
    public static List<Item> HealingItems = new();
    public static List<NPC> StandardEnemies = new();

    private static bool _isIndexed = false;

    public static void BuildIndex()
    {
        if (_isIndexed) return;

        // Extract Weapons & Healing
        for (int i = 1; i < ItemID.Count; i++)
        {
            Item item = new Item();
            item.SetDefaults(i);

            if (item.damage > 0 && !item.accessory && item.ammo == 0) Weapons.Add(item);
            if (item.healLife > 0 && !item.potion) HealingItems.Add(item); // Non-cooldown heals
        }

        // Extract Standard Enemies
        for (int i = 1; i < NPCID.Count; i++)
        {
            NPC npc = new NPC();
            npc.SetDefaults(i);

            if (!npc.friendly && !npc.townNPC && !npc.boss && npc.lifeMax > 5 && npc.damage > 0)
                StandardEnemies.Add(npc);
        }

        _isIndexed = true;
        TShock.Log.ConsoleInfo($"[Havoc] Semantic Index Built: {Weapons.Count} Weapons, {StandardEnemies.Count} Enemies loaded.");
    }

    public static int GetCurrentWorldTier()
    {
        if (NPC.downedMoonlord) return 11;
        if (NPC.downedAncientCultist) return 10;
        if (NPC.downedGolemBoss) return 8;
        if (NPC.downedPlantBoss) return 7;
        if (NPC.downedMechBoss1 && NPC.downedMechBoss2 && NPC.downedMechBoss3) return 5;
        if (Main.hardMode) return 4;
        if (NPC.downedBoss3) return 3; // Skeletron
        if (NPC.downedBoss2) return 2; // EoW/BoC
        if (NPC.downedBoss1) return 1; // Eye of Cthulhu
        return 0;
    }

    public static List<string> ResolveQuery(SemanticQuery query, string playerName, float tx, float ty)
    {
        List<string> generatedCommands = new();
        int tier = GetCurrentWorldTier();

        int minRarity = query.PowerLevel == "Overpowered" ? Math.Min(tier + 1, 11) : (query.PowerLevel == "Trash" ? 0 : Math.Max(0, tier - 1));
        int maxRarity = query.PowerLevel == "Trash" ? Math.Max(0, tier - 3) : tier;

        if (query.Action == "GiveWeapon")
        {
            var pool = Weapons.Where(w => w.rare >= minRarity && w.rare <= maxRarity).ToList();
            if (pool.Count > 0) generatedCommands.Add($"/give \"{pool[Random.Shared.Next(pool.Count)].Name}\" \"{playerName}\" {query.Amount}");
        }
        else if (query.Action == "GiveHealing")
        {
            var pool = HealingItems.Where(w => w.rare <= tier).ToList();
            if (pool.Count > 0) generatedCommands.Add($"/give \"{pool[Random.Shared.Next(pool.Count)].Name}\" \"{playerName}\" {query.Amount}");
        }
        else if (query.Action == "SpawnMob")
        {
            int maxLife = (tier + 1) * 150; // Scaling logic for mobs
            var pool = StandardEnemies.Where(n => n.lifeMax <= maxLife && n.lifeMax >= (maxLife/4)).ToList();
            if (pool.Count > 0) generatedCommands.Add($"/spawnmob \"{pool[Random.Shared.Next(pool.Count)].FullName}\" {query.Amount} {tx} {ty}");
        }

        return generatedCommands;
    }
}
#endregion
