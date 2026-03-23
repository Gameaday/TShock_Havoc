# TShock_Havoc
Act as the bridge between live streaming platforms and the Terraria game engine, allowing external viewers to interact with the server in real-time.


# Module: Havoc (Live Interaction & Crowd Control)
Objective: Act as the bridge between live streaming platforms and the Terraria game engine, allowing external viewers to interact with the server in real-time.

# Core Requirements
Twitch PubSub/IRC Connection: Must maintain a persistent WebSocket connection to Twitch chat and Channel Point redemptions.

Action Mapping: Must map specific Twitch redeems or commands (e.g., "Spawn Slime King", "Midnight") to native TShock server commands.

Cooldowns & Rate Limiting: Must implement strict cooldowns per command and per user to prevent viewers from crashing the server via entity spam.

Identity Resolution (Optional/Advanced): If a viewer triggers a targeted event (e.g., "Heal Streamer"), Havoc must query the centralized Database to find the streamer's current in-game entity ID.

Graceful Degradation: Must silently fail and attempt reconnections without halting the Terraria server if the Twitch API goes down.

# Technical Specifications
Dependencies: TwitchLib (or equivalent lightweight IRC/PubSub client), System.Text.Json.

TShock Hooks: Minimal to none. Primary operations will be dispatching Commands.HandleCommand() to the server engine.

Commands: /havoc reload, /havoc toggle.

State Management: Thread-safe timers for cooldown tracking.
