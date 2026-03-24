using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using TShockAPI;

namespace Havoc;

public static class HavocIndex
{
    // FIX: Using HashSets for extremely fast O(1) intersections
    public static Dictionary<string, HashSet<int>> ItemTags = new(StringComparer.OrdinalIgnoreCase);
    public static Dictionary<string, HashSet<int>> MobTags = new(StringComparer.OrdinalIgnoreCase);

    // FIX: Pre-cache tiers so we don't instantiate new Item/NPC objects mid-game
    private static Dictionary<int, int> ItemTiers = new();
    private static Dictionary<int, int> MobTiers = new();

    public static void BuildIndex()
    {
        ItemTags.Clear(); MobTags.Clear(); ItemTiers.Clear(); MobTiers.Clear();
        ItemTags["All"] = new HashSet<int>(); MobTags["All"] = new HashSet<int>();

        // 1. Deep Item Tagging
        for (int i = 1; i < ItemID.Count; i++)
        {
            Item item = new(); item.SetDefaults(i);
            if (string.IsNullOrEmpty(item.Name)) continue;

            ItemTiers[i] = item.rare; // Pre-cache tier

            List<string> tags = new() { "All" };
            if (item.damage > 0 && !item.accessory && item.ammo == 0) tags.Add("Weapon");
            if (item.accessory) tags.Add("Accessory");
            if (item.healLife > 0) tags.Add("Healing");
            if (item.pick > 0 || item.axe > 0 || item.hammer > 0) tags.Add("Tool");

            if (item.melee) tags.Add("Melee");
            if (item.ranged) tags.Add("Ranged");
            if (item.magic) tags.Add("Magic");
            if (item.summon) tags.Add("Summon");
            
            if (item.Name.Contains("Yoyo") || item.channel) tags.Add("Yoyo");
            if (item.Name.Contains("Sword") || item.Name.Contains("Blade")) tags.Add("Sword");

            foreach (var tag in tags) { if (!ItemTags.ContainsKey(tag)) ItemTags[tag] = new(); ItemTags[tag].Add(i); }
        }

        // 2. Deep Mob Tagging
        for (int i = 1; i < NPCID.Count; i++)
        {
            NPC npc = new(); npc.SetDefaults(i);
            if (string.IsNullOrEmpty(npc.FullName) || npc.friendly || npc.townNPC) continue;

            // Pre-cache tier logic
            MobTiers[i] = npc.boss ? 10 : (npc.value > 5000 ? 6 : (npc.lifeMax > 200 ? 3 : 1));

            List<string> tags = new() { "All", "Enemy" };
            if (npc.boss) tags.Add("Boss");
            if (npc.noGravity) tags.Add("Flying");
            if (npc.value > 1000) tags.Add("Elite");
            
            if (npc.aiStyle == 1 || npc.aiStyle == 15) tags.Add("Slime");
            if (npc.aiStyle == 3) tags.Add("Fighter");
            if (npc.aiStyle == 8 || npc.aiStyle == 17) tags.Add("Caster");

            foreach (var tag in tags) { if (!MobTags.ContainsKey(tag)) MobTags[tag] = new(); MobTags[tag].Add(i); }
        }

        TShock.Log.ConsoleInfo($"[Havoc] Semantic Index Built. Items: {ItemTags["All"].Count}, Mobs: {MobTags["All"].Count}");
    }

    public static int GetCurrentWorldTier()
    {
        if (NPC.downedMoonlord) return 11;
        if (NPC.downedGolemBoss) return 8;
        if (NPC.downedPlantBoss) return 7;
        if (NPC.downedMechBossAny) return 5;
        if (Main.hardMode) return 4;
        if (NPC.downedBoss3) return 3;
        if (NPC.downedBoss2) return 2;
        if (NPC.downedBoss1) return 1;
        return 0;
    }

    public static Item? QueryItem(SemanticQuery query, int worldTier)
    {
        int? id = PerformIntersection(ItemTags, ItemTiers, query, worldTier);
        if (!id.HasValue) return null;
        Item item = new(); item.SetDefaults(id.Value); return item;
    }

    public static NPC? QueryMob(SemanticQuery query, int worldTier)
    {
        int? id = PerformIntersection(MobTags, MobTiers, query, worldTier);
        if (!id.HasValue) return null;
        NPC npc = new(); npc.SetDefaults(id.Value); return npc;
    }

    private static int? PerformIntersection(Dictionary<string, HashSet<int>> map, Dictionary<int, int> tierMap, SemanticQuery query, int worldTier)
    {
        var required = query.Tags.Where(t => !t.StartsWith("-")).ToList();
        var excluded = query.Tags.Where(t => t.StartsWith("-")).Select(t => t.Substring(1)).ToList();

        // Start with the smallest required set for performance
        HashSet<int> results = new HashSet<int>(required.Count > 0 && map.ContainsKey(required[0]) ? map[required[0]] : map["All"]);

        foreach (var tag in required.Skip(1))
            if (map.ContainsKey(tag)) results.IntersectWith(map[tag]); else return null;

        foreach (var tag in excluded)
            if (map.ContainsKey(tag)) results.ExceptWith(map[tag]);

        // Filter by pre-cached tier
        var validIds = results.Where(id => 
        {
            int tier = tierMap.GetValueOrDefault(id, 0);
            return query.PowerLevel switch {
                "Overpowered" => tier > worldTier,
                "Trash" => tier <= Math.Max(0, worldTier - 3),
                _ => tier >= Math.Max(0, worldTier - 1) && tier <= Math.Min(11, worldTier + 1)
            };
        }).ToList();

        return validIds.Count > 0 ? validIds[Random.Shared.Next(validIds.Count)] : null;
    }
}
