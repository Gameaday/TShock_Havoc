# Havoc
### Stateless Twitch Interaction & Semantic Query Engine for TShock

Havoc is a rock-solid, completely stateless bridge between Twitch and TShock. Built for performance, it features an intelligent Semantic Query Engine that allows viewers to interact with the game world using traits and tags rather than hardcoded IDs.

---

## Features

* **Semantic Query Engine**: Viewers can request "Appropriate Melee Weapons" or "Overpowered Flying Enemies." Havoc indexes Terraria's internal memory to find the perfect match for the current world tier.
* **Zero-Config Session Persistence**: Type `/havoc on` to bind the engine to your account. If you disconnect, the engine stalls and waits for your specific account to return.
* **Smart Queue & JIT Re-Roll**: Prevents "Redundant Redemptions." If a viewer tries to heal you while you are at full health, the engine silently re-rolls the event at the front of the line for maximum impact.
* **Tiered Batching**: Minor events (like slimes or tricks) are batched together to keep the "spam fun" alive, while Major events (Bit donations) fire solo for high-impact presence.

---

## 🛠 Project Roadmap

- [ ] **Inventory Hijacking**: Implement "The Great Swap." Temporarily store a player's inventory and replace it with thematic "trash" (or "gold") for a set duration.
- [ ] **Multi-Target Support**: Allow the engine to track multiple streamers on the same server simultaneously with independent queues.
- [ ] **Aetheric Vault API**: A read-only web-endpoint to allow OBS overlays to display the current queue and "Active Conflicts" in real-time.
- [ ] **Dynamic Pricing**: (Exploration) Adjusting "Essence" or Bit-requirements based on the number of people currently in chat to keep the economy balanced.

---

## Installation

1. Drop `Havoc.dll` into your TShock `ServerPlugins` folder.
2. Start the server to generate `tshock/Havoc/HavocConfig.json`.
3. Add your Twitch Bot OAuth Token and Channel Name.
4. Type `/havoc on` in-game to ignite the engine.

---

## Commands
* `/havoc on` - Binds the engine to your TShock account.
* `/havoc off` - Disconnects from Twitch and wipes the queue.
* `/havoc clearqueue` - Manually deletes all pending events.
* `/havoc reload` - Hot-reloads the JSON configuration.

---
## License
MIT
