# Oracle: Trinity Edition

An in-game cheat and tweak menu for **Batman: Arkham Knight** (PC), built on [BmSDK-AK](https://github.com/Team-BmSDK/BmSDK-AK).
Press **F3** in game to open it, or hold **BACK (View)** on an Xbox controller.

This is a fork of **Oracle by ZeidCeZeY**. It started from his mod and has since been rebuilt and expanded a lot.

## Credits

| | |
|---|---|
| **ZeidCeZeY** | Created Oracle: the original idea, the menu and the first set of features (versions 1.0 to 2.0). Original release on Nexus Mods: <https://www.nexusmods.com/batmanarkhamknight/mods/3597> |
| **XeTrinityz** | Trinity Edition: the ground-up rebuild (3.0) and everything added since. Details below. |

Both are credited in game too, under **SYSTEM > CREDITS**.

ZeidCeZeY released Oracle under the MIT licence and did not put it on GitHub. This repository keeps his copyright notice
in [LICENSE.txt](LICENSE.txt), as the licence requires. If you only want the original, get it from his Nexus page.

## What came from the original

Oracle 1.0 to 2.0 by ZeidCeZeY gave the mod its name, the F3 menu, and the first features: player, combat and
takedown cheats, freeze enemies, sky and fog looks, weather (including real NO RAIN and wet streets), the camera page
(FOV, distance, height and shoulder for walking, gliding and the Batmobile, plus presets), infinite glide, playing as
the other characters (who can all drive the Batmobile), live Batmobile cheats, a music player for your own songs,
NEW GAME ++, counter icons in NG+, menu colours, hotkeys, cheats remembered across loads, controller support, and
teleport to waypoint with the city district map (`cells.cache`).

## What Trinity Edition adds

**A rebuilt mod (3.0).** Oracle was rewritten from the ground up as modules on a small kernel. Every game hook is
guarded, so a failing feature logs an error instead of closing the game. Unloading the mod puts back every value it
changed. Settings moved to their own `settings.toml`, so updating the mod can't overwrite them. New floating-column
menu with an ACTIVE page and a size setting, and hotkeys you bind from the menu (INSERT on a row; one key can hold
several features).

**Combat.** AUTO FIGHT: Batman fights by himself with his whole moveset, countering and dodging every attack. It is a
learning brain that ships trained (`fight_brain.txt`) and keeps learning from your fights. Also AUTO COUNTER, AUTO
DODGE GUNFIRE, COMBO MULTIPLIER, COMBO NEVER RESETS, ONE HIT KO, ragdoll hits, knocking off helmets, disarm on first
hit and more.

**Teleport, six tabs.** Long teleports that fade and stream the city in district by district, then land on carefully
checked ground. Teleport to waypoint or objective, GO BACK through your last 16 teleports, the game's whole world map
by category (landmarks, Riddler trophies, riddles, AR challenges, Most Wanted sites), 8 saved spots, blink and sky
drop, and teleports to the Batmobile, enemies and trophies.

**World: enemies and friendlies.** Freeze (now for friendlies too), resize, heal, disarm, revive, pull and launch NPCs. Pacify enemies, turn
them on each other, arm them with weapons, hack militia tanks to your side, clear street thugs, flip every car, and an
on-screen NPC count.

**Batmobile, five tabs.** Autopilot to a waypoint or objective along the roads, remote control without the gadget,
repair, EMP-proofing and self-righting, engine power and tyre grip, a jump with floaty air gravity, auto combat that
fights for you while you drive, shooting down incoming missiles, every weapon armed, no reload or overheat, a
speedometer with driving stats, and Showcase skins.

**Player.** Gadget cheats (no cooldown, use anywhere, unlimited disruptor), Showcase suits applied on the spot,
upgrade points and unlocks, SAVE GAME NOW, and a Riddler page: reveal every trophy on the map, or SKIP TO RIDDLER
FINALE. Several Riddler and GCPD soft-locks are fixed along the way.

Removed from the original: the FUN page (stamina and fall damage).

## Install

1. Install [BmSDK-AK](https://github.com/Team-BmSDK/BmSDK-AK) (Oracle targets v0.21.0).
2. Put this folder in `Batman Arkham Knight/BmGame/Mods/` so you have `BmGame/Mods/Oracle/mod.toml`.
3. Start the game and press **F3**.

The mod writes these itself the first time it needs them, so they are not in the repository: `settings.toml` (your
choices), `saved_spots.txt` (teleport slots), `Oracle.log` (for bug reports). Drop `.mp3`, `.wav` or `.wma` files
in `Music/` for the music player.

**Updating** replaces `fight_brain.txt`, so AUTO FIGHT goes back to the training that ships with that version. Your
settings and saved spots are kept.

## Licence

MIT. See [LICENSE.txt](LICENSE.txt). Original work copyright ZeidCeZeY; Trinity Edition changes copyright XeTrinityz.
