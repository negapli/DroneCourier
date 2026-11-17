# DroneCourier

A Schedule I mod: buy delivery drones from **Milo Byte**, a tech nerd who hangs out in the warehouse, and let them move goods between your properties. No driving, no workers: everything is set up from a **Drones** app on your phone.

## Features
- **Milo Byte**, a new NPC in the warehouse (the dark market). He sells 5 drone sizes; bigger drones carry more stacks per trip and unlock with your rank.

  | Drone | Stacks per trip | Rank | Price |
  |---|---|---|---|
  | Small | 4 | Peddler | $6,500 |
  | Medium | 6 | Hustler | $10,000 |
  | Large | 8 | Bagman | $15,000 |
  | Extra large | 12 | Enforcer | $25,000 |
  | Huge | 20 | Shot Caller | $45,000 |

- **Drones app**: pick a rack or storage closet to load from (FROM) and one in another property to drop at (TO), the same way the management clipboard picks things.
- **When to leave**: when the sender is full, when X stacks are ready, or every X minutes. Start a loop, do one trip, or stop.
- **Item filters**: choose which items a drone carries, with the game's item icons. Several drones can share one rack if their items don't overlap; a drone with no filter takes the rest.
- Drones are invisible: a trip takes 10 s to load, distance / 10 m/s each way, and 10 s to unload. Goods in flight are saved with your game. A full receiver makes the drone wait; nothing is lost.
- **Rename** your drones.
- **Give a drone back**: Milo leaves half its price as cash in a dead drop and texts you where.

## Requirements
- Schedule I **0.4.7** (IL2CPP, the default Steam branch)
- [MelonLoader](https://github.com/LavaGang/MelonLoader) 0.7.x
- [S1API](https://thunderstore.io/c/schedule-i/p/ifBars/S1API_Forked/) 3.2.3 (IL2CPP)
- [SteamNetworkLib (Il2Cpp)](https://thunderstore.io/c/schedule-i/p/ifBars/SteamNetworkLib_Il2Cpp/) 1.6.0

## Install
1. Install MelonLoader, S1API and SteamNetworkLib (a mod manager like r2modman does this for you).
2. Put `DroneCourier.dll` in `<Schedule I>/Mods/`.
3. Start the game. Milo stands in the warehouse; it opens in the evening once you've unlocked it the normal way.

Uninstall: delete `Mods/DroneCourier.dll`. Your drones are stored in the save's `Modded` folder and are ignored without the mod.

## Multiplayer (experimental)
Every player needs DroneCourier, S1API and SteamNetworkLib. The host owns the drones and runs the trips; clients see them in their app and can buy, set up and give back drones. Client changes take about 1-2 seconds to show. Co-op has not been fully tested yet: please report what you find.

## Troubleshooting
The log is `<Schedule I>/MelonLoader/Latest.log`; DroneCourier lines start with `[DroneCourier]`. Please attach it to bug reports.

## Building from source
.NET 6 SDK, a Schedule I install with MelonLoader (run the game once so `MelonLoader/Il2CppAssemblies` exists), S1API and SteamNetworkLib installed.
```
dotnet build -c Release -p:GameDir="/path/to/Schedule I"
```
The build copies `DroneCourier.dll` into `<game>/Mods/`.

## Credits
- [MelonLoader](https://github.com/LavaGang/MelonLoader) (LavaGang)
- [S1API](https://github.com/ifBars/S1API) and [SteamNetworkLib](https://github.com/ifBars/SteamNetworkLib) by ifBars
- App icon art by [ReyShikufu](https://github.com/ReyShikufu)
- Built with Claude Code (Anthropic's Claude Opus) as the coding agent, directed and play-tested by pligane.
- No game files or game assets are included; Milo is built from the game's own avatar parts at runtime.

## License
MIT, see [LICENSE](LICENSE).
