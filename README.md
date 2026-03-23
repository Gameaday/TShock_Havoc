# Havoc
### Stateless Twitch Interaction & Smart Queue Engine for TShock

Havoc is a rock-solid, completely stateless bridge between Twitch and TShock. Built for performance, it features an intelligent queuing system that manages chaotic redemptions without overwhelming the server or requiring complex databases.

---

## Features

* **Zero-Config Targeting**: Type `/havoc on` in-game. Havoc binds to your TShock Account. If you disconnect, the engine patiently holds the queue until you return.
* **The Smart Queue**: Prevents redemptions from overwriting each other. If an Eclipse is active, a Blood Moon redemption will wait in the queue until the Eclipse finishes.
* **The Corpse Lock**: Prevents viewers from wasting points. If a user buys a heal, but you are currently dead, the queue pauses and executes the moment you respawn.
* **Stateless Economy**: Utilizes native Twitch chat commands and Bits. No external SQLite databases or viewer balances to manage. 
* **VIP Bypass**: Configure massive Bit-drops to bypass the queue entirely and execute instantly for maximum impact.
* **Dynamic Progression**: Automatically filters events based on world progression (e.g., prevents Plantera from spawning on Day 1).

---

## Installation

1. Drop `Havoc.dll` into your TShock `ServerPlugins` folder.
2. Start the server to generate `tshock/Havoc/HavocConfig.json`.
3. Add your Twitch Bot OAuth Token to the config.
4. Type `/havoc on` in-game to ignite the engine.

---

## Configuration Example
Use Thematic Pools to group events by weight and conflict group.

```json
{
  "triggerType": "Chat",
  "triggerIdentifier": "!trick",
  "globalCooldownSeconds": 30,
  "events": [
    {
      "name": "Gravity Flip",
      "weight": 100,
      "conflictGroup": "PlayerBuff",
      "queueDurationSeconds": 15,
      "requiresTargetAlive": true,
      "tShockCommands": ["/buff \"Gravitation\" \"{player}\" 15"]
    }
  ]
}
