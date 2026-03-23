# Havoc
### Twitch Interaction & Crowd Control Engine for TShock

Havoc is a high-performance bridge between live streaming platforms and the Terraria game engine. It allows Twitch viewers to interact directly with your server in real-time through chat commands and channel point redemptions.

---

## Features

* **Persistent Twitch Integration**: Maintains a stable WebSocket connection to Twitch IRC for real-time command parsing.
* **Action Mapping**: Map Twitch events directly to native TShock server commands (e.g., spawning bosses, changing time, or granting items).
* **Identity Resolution**: Queries the Metatron central database to automatically find and target the streamer or specific linked players in-game.
* **Flood Protection**: Thread-safe cooldown timers prevent viewers from overwhelming the server with entity spam.
* **Graceful Degradation**: Built to silently attempt reconnections without interrupting the Terraria server heartbeat.

---

## Installation

1. Download the bundled `Havoc.dll` from the repository releases.
2. Place the `.dll` into your TShock `ServerPlugins` folder.
3. Restart your server to generate the directory structure at `tshock/Havoc/`.
4. Configure your Twitch OAuth credentials in `HavocConfig.json`.

---

## Configuration

### HavocConfig.json
| Property | Description |
| :--- | :--- |
| `twitchBotToken` | Your Twitch OAuth token (keep this private). |
| `twitchChannelName` | The channel for the bot to monitor. |
| `archiveDbPath` | Path to Metatron's `Archive.sqlite` for identity lookups. |
| `streamerDiscordId` | Your Discord ID to enable auto-targeting of your character. |

### Actions
Define your crowd control triggers in the `actions` array:

```json
{
  "command": "!slime",
  "tShockCommand": "/spawnmob slime 5",
  "cooldownSeconds": 60,
  "isRewardRedemption": false
}
