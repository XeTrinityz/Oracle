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
teleport to waypoint.

## What Trinity Edition adds

**A rebuilt mod (3.0).** Oracle was rewritten from the ground up as modules on a small kernel. Every game hook is
guarded, so a failing feature logs an error instead of closing the game. Unloading the mod puts back every value it
changed. Settings moved to their own `settings.toml`, so updating the mod can't overwrite them.

**Favourites radial.** Put any feature on a wheel of your own: press HOME on a row (Y on a controller) and place it in
any slot on any page. In game, hold the radial key (mouse 4 by default, R3 on a controller), point at a slot with
the mouse, a stick or WASD, and let go to use it. Number keys use a slot at once. Q / E (LB / RB) turn the page, up
to 9 pages of 4 to 12 slots. Set the key, HOLD or TAP mode, size and slots per page under SYSTEM > RADIAL, where
ARRANGE RADIAL lets you move favourites around. A used slot does what its hotkey would. Rows that change your save
can't go on it.

**The menu.** A fixed-size panelled menu with an ACTIVE page for everything switched on. Type anywhere in the menu to
search every feature on every page. The key strip underneath changes with the selected row, and the footer shows its
help, hotkey and default value. PREVIEW PAGES shows each page's rows as you browse the page list. SYSTEM > ASSETS browses the game's own loaded textures and fonts, and MENU FONT draws the menu in any of them. Hotkeys are bound from the menu (INSERT on a row; one key can hold several features),
and each page remembers the row you were on.

**Combat.** AUTO FIGHT: Batman fights by himself with his whole moveset, countering and dodging every attack. It is a
learning brain that ships trained (`fight_brain.txt`) and keeps learning from your fights. Also AUTO COUNTER, AUTO
DODGE GUNFIRE, COMBO MULTIPLIER, COMBO NEVER RESETS, ONE HIT KO, ragdoll hits, knocking off helmets, disarm on first
hit and more.

**Teleport, six tabs.** Long teleports fade to black and fly you there above the rooftops, so the city streams in
district by district as it does when you glide, then land you on ground that belongs to a loaded level. Teleport to waypoint or objective, GO BACK through your last 16 teleports, the game's whole world map
by category (landmarks, Riddler trophies, riddles, AR challenges, Most Wanted sites), 8 saved spots, blink and sky
drop, and teleports to the Batmobile, enemies and trophies.

**World: enemies and friendlies.** Freeze (now for friendlies too), resize, heal, disarm, revive, pull and launch NPCs. Pacify enemies, turn
them on each other, arm them with weapons, hack militia tanks to your side, clear street thugs, flip every car, and an
on-screen NPC count.

**World: weather, atmosphere and city.** Rain from none to storm, toxin rain in any colour, puddles, mist and distant
rain. Lightning strikes on demand or at your crosshair, storm frequency and brightness. Wind
strength, direction and gusts. Sky and fog looks plus exposure, bloom, motion blur, film grain, depth of field, and
where the fog starts and how high it hangs. In the city: EMPTY STREETS, spawn an APC, a thug mob on the Batmobile or a
getaway car to chase, Jokerfy every billboard, Joker thugs, how often the Joker hallucinations come, and a
readout of the militia left on your island.

**World editor.** Mark enemies, friendlies, vehicles, turrets, props, movable scenery and pickups through walls,
with boxes, names, health and tracers. Select what you're aiming at, or pick from a list of everything nearby, then
grab it and carry it around (and throw it), place it at your crosshair, nudge, turn and resize it, freeze it, switch
off its collision, or remove it. REMOVE only hides, so missions can't break, and RESTORE puts anything you changed back.

**Spawner, five tabs.** Spawn characters, vehicles, objects and weapons, built the way the game builds its own from
whatever it has loaded around you. CHARACTERS lists everyone the area can build (militia, gang thugs, police, fire
crews, civilians...) by group, with side (hostile, passive, on your side or BODYGUARD), behaviour (fight, stand, follow
you, wander, frozen), weapon, health and size. Bodyguards follow you everywhere and protect you: they go after whoever
threatens you most with fists, blades or guns (aimed, with line of sight, moving until they have a shot), hits land with
impact sounds, enemies fight them back, and each wears its own marker with a health bar and an off-screen arrow. VEHICLES spawns militia tanks, heavy tanks, drones, cars and vans, hostile or
hacked to your side, attacking, guarding, parked or driving off. OBJECTS makes physics or fixed props from any mesh
nearby or in memory, or copies whatever piece of the world is under your crosshair. WEAPONS lays guns, blades,
shields and crates on the ground for thugs to pick up, or hands one to a character. Every tab sets how many, where
(in front or at the crosshair), spacing and facing; SPAWN COPY OF SELECTED duplicates the world editor's selection.
SPAWNED lists your spawns, hands one to the world editor, and deletes them, and can clear fallen or far-off spawns by
itself. Nothing the game placed is ever deleted.

**Camera, five tabs.** FREE CAMERA flies the view anywhere (Batman holds still, or the whole world freezes), with
smooth moves, tilt and zoom, and puts Batman down under the camera when you're done. TRIPOD leaves the camera on the
spot and gives you Batman back, turning to follow him. FIRST PERSON on foot, camera tilt, camera through walls,
hide the game HUD, cinematic bars, swap shoulder, and a default Batmobile view including the bonnet camera. Field
of view now changes the finished picture, so it can no longer creep wider after aiming.

**Batmobile, five tabs.** Autopilot to a waypoint or objective along the roads, remote control without the gadget,
repair, EMP-proofing and self-righting, engine power and tyre grip, a jump with floaty air gravity, auto combat that
fights for you while you drive, shooting down incoming missiles, every weapon armed, no reload or overheat, a
speedometer with driving stats, battle mode with its weapons and winch on skins that can't transform, Showcase skins, and ENGINE SOUND, which gives any skin the engine and tyre sounds of
another (Arkham Knight, MK2, 1966, 1989, Tumbler, Batman v Superman, Arkham Asylum).

**Player status.** CAN'T DIE (hits still hurt but leave you on 1 health), DAMAGE TAKEN from a tenth to five times,
health regeneration, unbreakable armour, no hit reactions, no stuns, invisible to thugs as well as drones, and a
status readout with health, armour, XP and upgrade points. PROGRESS adds an XP multiplier and +5000 XP, and
CHARACTER sets Batman's suit battle damage.

**Player.** Movement sliders for glide speed, grapple speed and boost power, line launcher speed, fall gravity and
low tunnel speed. Gadget cheats (no cooldown, use anywhere, unlimited disruptor, auto password hacks), Showcase suits applied on the spot,
upgrade points and unlocks, SAVE GAME NOW, and a Riddler page: reveal every trophy on the map, or SKIP TO RIDDLER
FINALE. Several Riddler and GCPD soft-locks are fixed along the way.

**Saves.** SYSTEM > SAVES lists every save on disk, newest first, with date, slot, location, difficulty and completion:
each slot's three rotating files, the backups the game quietly keeps in `Documents\WB Games`, and Oracle's own kept
copies. LOAD THIS SAVE restarts from any of them in game, in the main story or its New Game + run (keeping the save you're
leaving first), KEEP A COPY puts one
where the game will never rotate it away, and SAVE NOW AND KEEP A COPY gives you a save to come back to. START NEW
GAME + NOW starts a fresh New Game + run straight from your game, wiping an NG+ run already under way (your save is
kept first), and the NEW GAME ++ main-menu button lives here too.

Removed from the original: the FUN page (stamina and fall damage).

## Install

1. Install [BmSDK-AK](https://github.com/Team-BmSDK/BmSDK-AK) (Oracle targets v0.21.0).
2. Put this folder in `Batman Arkham Knight/BmGame/Mods/` so you have `BmGame/Mods/Oracle/mod.toml`.
3. Start the game and press **F3**.

The mod writes these itself the first time it needs them, so they are not in the repository: `settings.toml` (your
choices), `saved_spots.txt` (teleport slots), `saves/` (kept save copies), `Oracle.log` (for bug reports). Drop `.mp3`, `.wav` or `.wma` files
in `Music/` for the music player.

**Updating** replaces `fight_brain.txt`, so AUTO FIGHT goes back to the training that ships with that version. Your
settings and saved spots are kept.

## Licence

MIT. See [LICENSE.txt](LICENSE.txt). Original work copyright ZeidCeZeY; Trinity Edition changes copyright XeTrinityz.
