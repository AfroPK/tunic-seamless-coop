# TUNIC Seamless Co-op

Play TUNIC together: 2–8 players in one shared world. Everyone sees each other move and fight, enemies are
shared and fight whoever is nearest, and progress, items and money are shared by the whole party.

> Unofficial fan mod. Not affiliated with Andrew Shouldice, Finji or the TUNIC team. Every player needs their
> own copy of TUNIC.

## What is synced

| | |
|---|---|
| Players | Position, facing, animations (rolls, attacks, items), equipment and effects, smooth 100 ms playback |
| Areas | Players in different areas don't see each other; walking through a door brings you together again |
| World | Doors, bells, switches and other progress flags; opened chests (state persists in every save) |
| Inventory | **Everything shared** — items, upgrades, consumables and money are one party inventory |
| Coins | Coins that drop (pots, chests, enemies) appear for everyone; each coin can only be collected once |
| Pots & grass | Breaking a pot breaks it for everyone (coins inside are not duplicated) |
| Enemies | Each enemy is simulated by the nearest player and mirrored for everyone else; anyone can hit any enemy; enemies chase and hurt whoever is closest |
| Death | When a teammate is still standing you go **down** instead of dying: spectate your team until someone revives you (see *Down, not out*). If everyone is down — or nobody else is in the session — you die and respawn at your campfire as usual |

## Works with any PC store

The mod doesn't use Steam, Epic or GOG services at all: players connect directly over the network, so a Steam
player can play with an Epic or GOG player. The game itself doesn't even need its launcher running. What
matters is that everyone has the **same TUNIC version** (shown in the co-op menu) and the **same mod
version** — the mod refuses mismatched mod versions and warns about mismatched game versions.

| Store | Where TUNIC is usually installed |
|---|---|
| Steam | `…\steamapps\common\TUNIC` (Steam → right-click TUNIC → Manage → Browse local files) |
| Epic | `C:\Program Files\Epic Games\TUNIC` (Library → ⋯ → Manage → folder icon) |
| GOG Galaxy | `C:\Program Files (x86)\GOG Galaxy\Games\TUNIC` (game page → Manage installation → Show folder) |
| Xbox / Game Pass | Only if the install folder is accessible (Xbox app → Manage → Files → Browse); protected installs can't load BepInEx |

## Install

1. Find your TUNIC folder (the one with `Tunic.exe`, see the table above).
2. Install **BepInEx 6.0.0-pre.1 (Unity IL2CPP, x64)** there — the same version the TUNIC Randomizer uses:
   <https://github.com/BepInEx/BepInEx/releases/download/v6.0.0-pre.1/BepInEx_UnityIL2CPP_x64_6.0.0-pre.1.zip>
   Extract it next to `Tunic.exe`, start the game once, wait for the title screen (the first start takes a
   minute while BepInEx prepares), then quit.
3. Extract this mod's zip into the same folder so you get `BepInEx/plugins/TunicCoop/TunicCoop.dll` and
   `LiteNetLib.dll`.
4. Start the game. You'll see a small **CO-OP · Offline** panel in the top-left corner.

## Play

Press **F1** in game to open the co-op menu:

- **Your name** — what the other players see above your fox and in their panel. The first time you open
  the menu it starts here so you can pick one; you can change it any time, even mid-session, and everyone
  sees the new name immediately.
- **Join address / Port** — the host's address (they'll see it in their panel and menu as "Share: …").
- **Max players** — 2 to 8 (host only).
- **Streamer mode** — hides every IP address on screen (the panel, messages and this menu, where the address
  you type shows as `****`). Turn it on before you go live; share your address with friends privately.
- **Host a session**, **Join**, **Leave session**.

Use **Up/Down** to pick a row, type to edit, **Left/Right** to change numbers, **Enter** to confirm, **F1** to
close. Your fox doesn't move while the menu is open. Settings are saved for next time.

Quick keys: **F7** host · **F8** join the saved address · **F9** leave.

While playing you'll see:

- **The co-op panel** (top-left): connection status, the address to share or the host you're connected to,
  and every player with their colour, current area, ping and health.
- **Nameplates** above other players with their name and health.
- **Messages** (top-centre) when people join or leave, and plain-language explanations when something goes
  wrong (wrong address, host not reachable, version mismatch, session full…).

### Down, not out

When a hit would kill you and at least one teammate is still up, you're **downed** instead: your fox stays
where it fell (shown to everyone as *DOWNED* with a red nameplate) and your camera follows a teammate.

- **Left / Right** — switch which teammate you're watching.
- A teammate revives you by **standing next to you for 3 seconds** (they see a *Reviving …* bar). You get
  back up with half your health.
- **Hold F2** — give up and respawn at your campfire instead.
- If every player is down at the same time, everyone dies for real and respawns as usual.

**Use a dedicated co-op save.** When you join, your save takes on the host's world and inventory.

### Connecting

The host must be reachable on **UDP 27015** (configurable):

- Same network: use the host PC's LAN IP (e.g. `192.168.1.20`).
- Over the internet: forward UDP 27015 on the host's router, **or** use a virtual LAN such as Tailscale,
  ZeroTier or Radmin VPN and use that IP. Allow TUNIC through Windows Firewall when asked.

### Config (`BepInEx/config/com.freddy.tuniccoop.cfg`)

| Setting | Default | |
|---|---|---|
| `Player.Name` | `Fox` | Your name in the overlay |
| `Network.JoinAddress` | `127.0.0.1` | Host to join with F8 |
| `Network.Port` | `27015` | UDP port |
| `Network.MaxPlayers` | `4` | 2–8, including the host |
| `Network.Password` | *(empty)* | Must match the host |
| `Privacy.StreamerMode` | `false` | Hide IP addresses on screen (also in the F1 menu) |
| `Gameplay.Revive` | `true` | Down-and-revive instead of instant death while a teammate is alive |
| `Gameplay.ReviveSeconds` | `3` | How long a teammate must stand next to you |
| `Gameplay.ReviveHealthPercent` | `50` | Health you get back when revived |
| `World.SyncWorldFlags` | `true` | Share world progress and inventory |
| `Debug.*` | `false` | Developer test tools; leave off |

## Troubleshooting

- **"Couldn't reach …"** — check the address and port, that the host pressed *Host a session*, and that the
  host's firewall allowed TUNIC (and the router forwards UDP 27015, or you're on the same VPN).
- **"The host refused the connection"** — the password differs, or you have a different mod version.
- **Game version warning** — update TUNIC in your launcher so everyone has the same version.
- **A feature was "turned off after errors"** — your game build differs from the tested one; co-op keeps
  working without that part. Please report your game version and store.
- Logs: `BepInEx/LogOutput.log`.

## Known limitations

- Bosses and cutscenes have not been tested yet.
- Tested on the Steam build; Epic/GOG builds of the same game version should behave identically (nothing
  store-specific is used), and anything that differs switches itself off instead of breaking co-op.
- Connecting is by address (LAN, port forwarding or a VPN such as Tailscale/ZeroTier/Radmin). There's no
  store-specific invite system by design, so every store can play together.
- Other players' tether-wand wind-up flourish is not shown (the attack itself is).
- Shared money means a real death costs the whole party until the bloodstain is recovered.
- Some of TUNIC's own ghost-fox NPCs depend on each player's save, so one player may see a ghost another
  doesn't.

## How it was tested

Every feature above was verified on two real PCs (plus a third game instance) by an automated test
autopilot built into the mod (`Debug.EnableAutopilot`): it loads saves, hosts/joins, drives the fox with a
virtual gamepad and checks results from state files, logs and screenshots. See `tools/suite.ps1`.

## Building from source

Requires the .NET SDK and a TUNIC install with BepInEx already run once (for the interop assemblies):

```
dotnet build src/TunicCoop -c Release -p:TunicDir="D:\SteamLibrary\steamapps\common\TUNIC" -p:Deploy=true
```

## Credits and disclosure

- [BepInEx](https://github.com/BepInEx/BepInEx) (LGPL-2.1) and its IL2CPP interop / Harmony / MonoMod stack.
- [LiteNetLib](https://github.com/RevenantX/LiteNetLib) (MIT), bundled.
- Thanks to the TUNIC modding community (TUNIC Randomizer) for establishing the BepInEx route.
- **Built with AI:** this mod was developed and tested by Claude (Anthropic) working with the author.

No game files or decompiled game code are included. License: MIT (see `LICENSE`).
