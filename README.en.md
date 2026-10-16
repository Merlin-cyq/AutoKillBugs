# AutoKillBugs

[简体中文](README.md) | **English**

A Stardew Valley 1.6 SMAPI mod: **Grubs** and **Flies** in the mines die the instant they appear — while **loot, XP, and Adventurer's Guild kill progress are fully preserved**, because it goes through the game's own kill pipeline instead of simply deleting the monsters.

---

## What this mod does

In the Mines, Skull Cavern, the Quarry Mine, and the Mutant Bug Lair, Grubs and Flies (including the buffed variants found in the Dangerous Mines and the Mutant Bug Lair) die as soon as they are added to the location.

The important part is **how** they die: the mod calls the exact same method the game itself calls when you hit a monster with a weapon. Because of that, every step of the vanilla kill resolution still happens:

- **Loot** — dropped from the vanilla loot tables: Bug Meat, Rice Shoot, White Algae, Ancient Seed, Dwarf Scrolls, plus the Diamond and Prismatic Shard that only become possible after you have reached the bottom of the Mines
- **XP** — awarded normally
- **Adventurer's Guild progress** — the "Initiation" quest (slay 80 cave insects) counts normally
- **Every other vanilla hook** — kill quests, special orders, the "Kill" weapon enchantment, the Burglar's Ring, and so on

## What this mod does NOT do

To be explicit, it does **not**:

- ❌ Remove the monster object directly
- ❌ Just set the monster's health to 0
- ❌ Hide the monster sprite
- ❌ Modify spawn tables (it doesn't stop monsters from spawning — it just kills them immediately)
- ❌ Touch your save file (not a single byte)

The first four approaches would also make the bugs disappear, but all of them bypass the vanilla kill resolution — which means **nothing drops and nothing gets counted**. That is exactly the trap this mod avoids.

---

## Installation

1. Install [SMAPI](https://smapi.io/) (**4.5.2 or later**)
2. Download `AutoKillBugs-1.2.0.zip` from this repository's Releases page
3. Unzip it and put the whole `AutoKillBugs` folder into your game's `Mods` directory
4. **Launch the game through `StardewModdingAPI.exe`** (do not launch the game executable directly)

Afterwards your files should look like this:

```
Stardew Valley/Mods/AutoKillBugs/AutoKillBugs.dll
Stardew Valley/Mods/AutoKillBugs/manifest.json
Stardew Valley/Mods/AutoKillBugs/config.json
Stardew Valley/Mods/AutoKillBugs/README.md
```

**Uninstalling**: just delete the `Mods/AutoKillBugs` folder. The mod never writes to your save file, so it leaves nothing behind.

---

## Configuration

Three switches, all enabled by default:

| Option | Default | Description |
|---|---|---|
| `Enabled` | `true` | Master switch. When off, the mod does nothing at all (equivalent to not having it installed) |
| `OnlyInMines` | `true` | Only active in the Mines / Skull Cavern / Quarry Mine / Mutant Bug Lair. Turn it off to apply everywhere (e.g. dungeons added by other mods) |
| `DelayOneFrame` | `true` | See the dedicated section below |

### Three ways to change the settings

1. **Press `F9` in-game** (recommended) — opens a vanilla-styled settings menu. Toggling a switch takes effect **immediately** and is written back to the config file. Close it with `Esc`, right-click, the ✕ in the top-right corner, or `F9` again. It opens both in-game and from the title screen.
2. **Edit** `Mods/AutoKillBugs/config.json` directly — takes effect after a restart.
3. **Generic Mod Config Menu** (optional) — if you have [GMCM](https://www.nexusmods.com/stardewvalley/mods/5098) installed, the same three options show up under `Mod Options` on the title screen. Without GMCM this path is silently skipped — no errors, no warnings.

`F9` is the only key this mod binds; the three switches themselves occupy no keys.

### What `DelayOneFrame` actually means

It is a trade-off — pick whichever side you care about more:

|  | `true` (default) | `false` |
|---|---|---|
| Will you see the bug? | It flashes for about 1 frame (1/60 s) | Never, not even one frame |
| Loot | **Complete** (including the Diamond / Prismatic Shard added after reaching the bottom of the Mines) | That extra loot may be missing |

The reason: some of the extra loot is only rolled when a monster **runs its own update for the first time**, based on the difficulty of the mine level it is on. With `true`, the mod registers the monster first and only acts on the next game frame, guaranteeing every bug runs at least one update — so that loot is always granted. The cost is that it appears on screen for a moment. With `false`, the mod kills the bug on the very same frame it enters the location, which is visually perfectly clean — but bugs that are added to the location *after* the regular update has already run (the classic case being the Fly that hatches out of a pupating Grub) never ran an update, so they miss that extra loot.

**Base loot, XP, and kill counts are identical in both modes.** Only the extra loot above differs. If "not a single frame" matters more to you, set it to `false`.

---

## How it works

For anyone who wants to read or modify the code.

### It only uses the game's own kill entry point

The single method this mod uses to kill anything is:

```
GameLocation.damageMonster(Rectangle, minDamage, maxDamage, isBomb, knockBackModifier,
                           addedPrecision, critChance, critMultiplier,
                           triggerMonsterInvincibleTimer, who, isProjectile)
```

— the very method the game calls when you swing a weapon or set off a bomb near a monster. Internally, when a monster's health reaches zero it runs `onMonsterKilled`, and loot, XP, guild counters, quest notifications, and removal from `characters` all live on that chain.

Calling `characters.Remove(...)` directly, or merely setting `Health` to 0, does *not* work: the game's own `updateCharacters` silently removes any monster with health ≤ 0 on the next frame, **with no resolution at all**.

### Why it hooks `UpdateTicked`

SMAPI's `GameLoop.UpdateTicked` fires right after the real `Game1.Update` (where monsters are spawned and added to the location) and still before `Draw` in the same frame. Killing at that moment means the monster is already out of the location on the frame it appeared — you never see it.

(SMAPI's `World.NpcListChanged` event is inherently one frame later; using it would let the bug be drawn for at least one frame, so it is not used.)

### Sound and visual effects

The death sounds Grubs and Flies play carry **no position argument**, and the game plays those at full volume regardless of where they are — so without handling it, you would hear a string of "something invisible is dying" noises.

The mod installs a Harmony prefix patch on `GameLocation.playSound` that **skips playback only during the instant of the automatic kill**. If the patch fails to install, killing and loot are unaffected — you just get one warning in the SMAPI console.

The floating damage numbers and animation sprites from the death are cleaned up inside that same window.

### What about pupating?

A Grub that takes enough damage enters "pupating" — during which it is immune to all damage — and then hatches into a Fly. Because this mod handles a Grub the moment it appears, a Grub can never normally reach half health. Should one already be pupating, the mod clears its pupating flag before acting; and even if it manages to hatch first, the resulting Fly is just as much a target, so nothing slips through.

### Multiplayer

Monsters are owned by the host in multiplayer, and a farmhand mutating them would desync the two sides. The mod therefore only acts on the host; on a farmhand it logs one warning and stays out of the way.

---

## Known limitations

- **Only the location the player is currently in is processed.** Bugs left behind on a mine level you have already left are not cleaned up remotely; that is left to the game.
- **Host only** (see above).
- **With `DelayOneFrame = true` the bug flashes for one frame.** Set it to `false` to never see it at all — see the comparison table for the cost.
- **The extra loot has prerequisites.** Diamond / Prismatic Shard are only possible once you have reached the bottom of the Mines at least once; Qi Beans and the egg additionally require a specific special order and the Desert Festival respectively. When those conditions are not met, both modes drop exactly the same things.
- **This mod has never been run through a full in-game verification.** Its verification is **static**: a clean build (0 warnings, 0 errors), assembly metadata checks, an independent recompilation of the delivered DLL compared line by line against both the IL (0 differences) and the decompiled C# (0 differences), and a check of every game API call site. How the menu actually looks and feels is something only you can see.
- **Three known low-severity quirks** (deliberately left unfixed; none affect usability):
  1. When the settings menu cannot open because another menu is already open, that `F9` press is still suppressed — it is not passed to other mods and does not trigger any game function (Stardew Valley binds nothing to F9 by default).
  2. The menu does not synchronise the mouse Y coordinate when drawing, so on some resolutions the "is the mouse over the menu" check may take a default branch (purely visual).
  3. Closing the menu does not re-validate that the submenu pointer still points at itself (static analysis found that path unreachable).

---

## Building from source

Requires the **.NET 6 SDK**.

```bash
git clone <this repository>
cd AutoKillBugs
```

Then edit `AutoKillBugs/AutoKillBugs.csproj` and point it at your own game installation:

```xml
<GamePath>your Stardew Valley install directory</GamePath>
```

And build:

```bash
dotnet build AutoKillBugs/AutoKillBugs.csproj -c Release
```

The project uses [Pathoschild.Stardew.ModBuildConfig](https://www.nuget.org/packages/Pathoschild.Stardew.ModBuildConfig), which handles referencing the game assemblies. The output is a `net6.0` class library — the game ships its own self-contained .NET 6 runtime, so the target framework has to stay `net6.0`.

---

## Compatibility & requirements

| | |
|---|---|
| Stardew Valley | **1.6.15** (any 1.6.x should work) |
| SMAPI | **4.5.2** or later |
| Optional | [Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098) |

The mod modifies no game data files and adds no sprites or fonts — the panel, checkboxes, and fonts in the settings menu all come from the game's own assets.

---

## Changelog

**1.2.0**
- Added an in-game settings menu (default hotkey `F9`); each switch takes effect immediately and is written back to the config file
- Added Generic Mod Config Menu integration (optional dependency)
- Kill logic is identical to 1.1.0, confirmed by a line-by-line IL comparison

**1.1.0**
- Added the `DelayOneFrame` option (enabled by default), letting bugs run one update before dying so the extra loot added after reaching the bottom of the Mines is preserved
- Added staleness guards to the pending-kill list: no cross-location kills, no double resolution, no unbounded growth

**1.0.0**
- First release: Grubs and Flies die through the game's own kill pipeline the moment they appear, preserving loot, XP, and guild progress
