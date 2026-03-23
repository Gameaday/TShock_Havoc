using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using TShockAPI;

namespace Havoc;

public static class HavocIndex
{
    public static Dictionary<string, List<int>> ItemTags = new(StringComparer.OrdinalIgnoreCase);
    public static Dictionary<string, List<int>> MobTags = new(StringComparer.OrdinalIgnoreCase);

    public static void BuildIndex()
    {
        ItemTags.Clear(); MobTags.Clear();
        ItemTags["All"] = new List<int>(); MobTags["All"] = new List<int>();

        // 1. Deep Item Tagging
        for (int i = 1; i < ItemID.Count; i++)
        {
            Item item = new(); item.SetDefaults(i);
            if (string.IsNullOrEmpty(item.Name)) continue;

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
        int? id = PerformIntersection(ItemTags, query, worldTier, true);
        if (!id.HasValue) return null;
        Item item = new(); item.SetDefaults(id.Value); return item;
    }

    public static NPC? QueryMob(SemanticQuery query, int worldTier)
    {
        int? id = PerformIntersection(MobTags, query, worldTier, false);
        if (!id.HasValue) return null;
        NPC npc = new(); npc.SetDefaults(id.Value); return npc;
    }

    private static int? PerformIntersection(Dictionary<string, List<int>> map, SemanticQuery query, int worldTier, bool isItem)
    {
        var required = query.Tags.Where(t => !t.StartsWith("-")).ToList();
        var excluded = query.Tags.Where(t => t.StartsWith("-")).Select(t => t.Substring(1)).ToList();

        IEnumerable<int> results = required.Count > 0 && map.ContainsKey(required[0]) ? map[required[0]] : map["All"];

        foreach (var tag in required.Skip(1))
            if (map.ContainsKey(tag)) results = results.Intersect(map[tag]); else return null;

        foreach (var tag in excluded)
            if (map.ContainsKey(tag)) results = results.Except(map[tag]);

        results = results.Where(id => 
        {
            int tier = 0;
            if (isItem) { Item t = new(); t.SetDefaults(id); tier = t.rare; }
            else { NPC t = new(); t.SetDefaults(id); tier = t.boss ? 10 : (t.value > 5000 ? 6 : (t.lifeMax > 200 ? 3 : 1)); }
            
            return query.PowerLevel switch {
                "Overpowered" => tier > worldTier,
                "Trash" => tier <= Math.Max(0, worldTier - 3),
                _ => tier >= Math.Max(0, worldTier - 1) && tier <= Math.Min(11, worldTier + 1)
            };
        });

        var final = results.ToList();
        return final.Count > 0 ? final[Random.Shared.Next(final.Count)] : null;
    }
}
