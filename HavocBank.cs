using System;
using System.Collections.Concurrent;
using System.IO;
using Microsoft.Data.Sqlite;
using TShockAPI;

namespace Havoc;

public static class HavocBank
{
    private static string _dbString = "";
    private static readonly ConcurrentDictionary<string, int> _auraCache = new(StringComparer.OrdinalIgnoreCase);
    private static bool _isDirty = false; // Tracks if RAM differs from DB

    public static void Initialize(string dbPath)
    {
        _dbString = $"Data Source={dbPath}";
        
        using var conn = new SqliteConnection(_dbString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS HavocBank (
                TwitchUsername TEXT PRIMARY KEY,
                Essence INTEGER DEFAULT 0
            );";
        cmd.ExecuteNonQuery();
    }

    public static int GetAura(string username)
    {
        if (_auraCache.TryGetValue(username, out int cachedAura)) return cachedAura;

        try
        {
            using var conn = new SqliteConnection(_dbString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Essence FROM HavocBank WHERE TwitchUsername = @u";
            cmd.Parameters.AddWithValue("@u", username.ToLower());
            var result = cmd.ExecuteScalar();
            
            int essence = result != null ? Convert.ToInt32(result) : 0;
            _auraCache[username] = essence;
            return essence;
        }
        catch { return 0; }
    }

    public static void ModifyAura(string username, int amount)
    {
        int current = GetAura(username);
        _auraCache[username] = Math.Max(0, current + amount);
        _isDirty = true;
    }

    public static bool TryConsumeAura(string username, int cost)
    {
        int current = GetAura(username);
        if (current >= cost)
        {
            _auraCache[username] = current - cost;
            _isDirty = true;
            return true;
        }
        return false;
    }

    public static void FlushToDatabase()
    {
        if (!_isDirty) return;

        try
        {
            using var conn = new SqliteConnection(_dbString);
            conn.Open();
            using var transaction = conn.BeginTransaction();
            
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO HavocBank (TwitchUsername, Essence) VALUES (@u, @e) ON CONFLICT(TwitchUsername) DO UPDATE SET Essence = @e";
            
            foreach (var kvp in _auraCache)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@u", kvp.Key.ToLower());
                cmd.Parameters.AddWithValue("@e", kvp.Value);
                cmd.ExecuteNonQuery();
            }
            
            transaction.Commit();
            _isDirty = false;
        }
        catch (Exception ex) { TShock.Log.ConsoleError($"[HavocBank] Flush failed: {ex.Message}"); }
    }
}
