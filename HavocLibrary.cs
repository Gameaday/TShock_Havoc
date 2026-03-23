using TerrariaApi.Server;
using TShockAPI;

namespace Havoc;

public static class HavocLibrary
{
    /// <summary>
    /// Checks if the player is in an invalid state for commands (e.g., teleporting, world loading).
    /// </summary>
    public static bool IsInInvalidState(TSPlayer player)
    {
        // If a player's coordinates are 0, or NaN, they are mid-transition.
        return player.X <= 0 || player.Y <= 0 || float.IsNaN(player.X) || float.IsNaN(player.Y);
    }

    // Common Buff IDs for easy reference when building your JSON
    public static class Buffs
    {
        public const int Poisoned = 20;
        public const int OnFire = 24;
        public const int Bleeding = 30;
        public const int Confused = 31;
        public const int Slow = 32;
        public const int Weak = 33;
        public const int Silenced = 37;
        public const int Chilled = 46;
        public const int Frozen = 47;
        public const int Webbed = 149;
        public const int Stoned = 156;
    }
}
