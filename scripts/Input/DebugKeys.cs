using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// The game ships its developers' key bindings (BmInput.ini): B spawns the Batmobile, H speeds the game up, T
/// teleports, K kills every enemy (story ones too), U unlocks every upgrade in the save, F5 reloads the level, CTRL+S
/// saves, ALT+L loads, BACKSPACE starts the debug camera. They call the game's cheat manager, which a normal game
/// doesn't have, so normally they do nothing. Oracle needs that cheat manager, so with Oracle loaded each of them
/// works on any key the player's own controls leave free. This switches off every binding whose commands are all
/// developer commands, and leaves bindings that also carry a real action (the pad's A, for one) alone.
/// </summary>
internal sealed class DebugKeys : Module
{
    public static readonly DebugKeys I = new();

    private const double ScanSeconds = 2.0;

    /// <summary>
    /// Console commands only the cheat manager and the debug cameras define (read from BmGame.upk), plus the
    /// engine cheat manager's. Any command starting with DEBUG counts too.
    /// </summary>
    private static readonly HashSet<string> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        "addscreenshake", "addxp", "advancecurrentthoughthistoryitem", "allammo", "allweapons", "amphibious", "apc",
        "autosavegame", "awarddebugintel", "awardintelpoint", "barkdewedge", "barkwtf", "batmobileaway",
        "batmobilecockpitclose", "batmobilecockpitopen", "batmobileinfinitepower", "batmobilesettirefrictionmod",
        "batmobilesettirelatstiffymod", "bmragdoll", "cachedeleter", "cachefilescanner", "cachefolderscanner",
        "cacheloader", "cachesaver", "challengeadjustscore", "challengeadjusttime", "challengesetmedals",
        "challengesetscore", "changesize", "clearcombattutorials", "combatghost", "copywaypoint", "cycleaiphyswalking",
        "cycleanimnames", "debugcamera2pad", "debugcameradown", "debugcameraforward", "debugcameraleft",
        "debugcameraright", "debugcameraup", "debugcamerazoomin", "debugcamerazoomout", "debugdestroyallweapons",
        "debugextendedviewdist", "debugfocusviewdist", "debugforce3rdpersoncamera", "debugfov", "debuggiveallgadgets",
        "debuggivearmour", "debuggivefreezeblast", "debugglobalflagget", "debugglobalflagset",
        "debuginteractableitems", "debugkillallbutoneenemyinroom", "debugkillallenemies", "debugloadgame",
        "debugplayerobstructions", "debugremoteevent", "debugremovearmour", "debugrespawnallthrowable",
        "debugsharedflagget", "debugsharedflagset", "debugupgradeequipment", "debugvertfov", "debugviewdist",
        "destroyall", "destroyallpawnsexceptplayers", "destroypawns", "dialoguecapture", "dialoguecaptureall",
        "dialogueinc", "disablealternativewalk", "discoverallcurrentsidestories", "displayallfades",
        "displaylookatpawnthoughts", "displaythoughtsonall", "dmthroughwalls_allow", "dmthroughwalls_suppress",
        "doubleghosttogglea", "doubleghosttoggleb", "dpropshape", "drawredhoodsniperdebug", "drawroadlink",
        "dumpallnav", "dumpbarkcoord", "dumpcurrentpawnthoughtstolog", "dumpobjectives", "dumppawnlevelvol",
        "dumppawns", "dumppdat", "dumproomstate", "dumpstorysynopsis", "enablealternativewalk",
        "enablebattlemodetoggleoption", "enablechallengetimer", "eraseallleaderboards", "eraseleaderboard",
        "erasemefromleaderboard", "evaluatebrains", "exitpassengers", "exitpassengerscalm", "explodecar",
        "findbatmobile", "flipbatmobile", "flipcommandertank", "flipgordon", "flipthugcars", "fly", "focusdof",
        "forcebatmanoutofcar", "forcespawnapc", "freezeall", "freezeframe", "gfxsafeframe", "ghost", "giveriddlermap",
        "giveskillpoints", "god", "gotoname", "infinitegadgets", "instantcasualty", "invisible",
        "isdefaultcharacterskintest", "killall", "killapc", "killheavytank", "killheavytanks", "killme", "killpawns",
        "killvehicles", "listdynamicactors", "listnames", "loaded", "lockglidebutton", "makenervous", "maketerrified",
        "massjam", "matineedebugoverlay", "morespeed", "navmeshobstaclesoff", "navmeshobstacleson", "neardeath",
        "normalspeed", "orbitmode", "overridewalkcamera", "pauseandpan", "playersonly", "playerwtf",
        "playtestdronebark", "playtester", "poolallpawns", "ppwtf", "queuedeleter", "queueloader", "queuesaver",
        "realworldbatman", "refreshpaths", "reloadlevel", "reportbug", "rescuebatmobile", "reset360orbitlocation",
        "resetroll", "resetroom", "resetspotted", "restorehealth", "revealandunlockchallengeid", "revealchallengeid",
        "riddlerpickupdebug", "riddlerrevealitems", "riddlerrevealitemsonlyvalid", "sentrygunspawnall",
        "sentryguntriggerplacement", "set360orbitlocation", "setcommandertankbossstage", "setcontroltype",
        "setcustomcontentaccessmodeoverride", "seteasy", "seteasydifficulty", "setgravity", "sethard",
        "setharddifficulty", "setjumpz", "setnormal", "setnormaldifficulty", "setpredgratetakedowndone",
        "setpredledgetakedowndone", "setpredsilenttakedowndone", "setpredwalltakedowndone", "setspeed", "setwaypoint",
        "showdialogueinfo", "showprogress", "showsatnav", "showvpointattackcache", "slambatmobile", "slomo",
        "slowdownspectatorcamera", "spambatarangs", "spawnbatmobilemob", "spawnchasecar", "spawndebugrunawaythug",
        "spawnpassengers", "spawnplayercar", "spawnscreenshotpointlight", "spawnscreenshotspotlight",
        "spawnvehiclescenario", "specialactionxp", "specialmovecheat", "speedupspectatorcamera",
        "splitscreenmoviecapture", "startrollleft", "startrollright", "startzoomin", "startzoomout",
        "steppawndisplayingthoughts", "stopdebugcameramovement", "stoproll", "stopzoom", "summon", "suppressxp",
        "take360orbitscreenshot", "take360orbitxyscreenshot", "take360screenshot", "tankformationreverse", "teleport",
        "teleportto", "testachievement", "testbatmaninc", "testblockbankdoors", "testcombo", "testlevelhidden",
        "testmap3d", "testmap3ddebug", "testnosaving", "testscoresethp", "testscoresetpoints", "testscoresetxp",
        "testscoreshow", "testsetglobalflag", "teststatsstory", "testtakedown", "testtargetcursor", "testzoommode",
        "toggleaipaths", "togglebot", "toggledebugcamera", "toggleenemiescanattack", "toggleerrorthoughts",
        "toggleframeratecheat", "toggleghost", "togglegrapplepointdebug", "togglehighspeed", "togglelookinvert",
        "togglelowspeed", "togglemagicgunstartle", "togglemagicsmasher", "togglenavmeshinspector",
        "toggleonscreenkismetandwarnings", "togglepaths", "togglephysicsgrabber", "togglephysimpact",
        "togglescreenshotcamera", "togglestreamingminimap", "togglesuperhighspeed", "togglethoughts", "trackingmode",
        "triggerheatconcealeffect", "triggerphysimpact", "turnoffenemiescanattack", "ua", "ucm", "unlinkwbid",
        "unlockall", "unlockallachievements", "unlockallupgrades", "unlockbio", "unlockbios", "unlockcatwomanepisodes",
        "unlockchallengeid", "unlockchallengemode", "unlockcitystory", "unlockconceptgalleries",
        "unlockconceptgallery", "unlocknewgameplus", "unlockriddlerpuzzles", "unlockriddlerscreen",
        "unlockshowcaseitems", "unlocktape", "unlocktapes", "unlockwaynetech", "unsuppressxpgive", "unsuppressxptake",
        "upgradeequipmentmenu", "upgrademag", "vehiclerunaway", "viewactor", "viewbot", "viewclass", "viewplayer",
        "viewself", "walk", "warningmessagetest", "weaponconfigwtf",
    };

    private Toggle _allow = null!;
    private Every _scan;
    private bool _reported;

    public override void Build()
    {
        _allow = AddToggle("game_debug_keys", "GAME DEBUG KEYS",
            "The game ships its developers' keys: B spawns the Batmobile, H speeds the game up, T teleports, K kills every enemy, U unlocks every upgrade, F5 reloads the level, and more. They only work because Oracle needs the game's cheat manager, so Oracle keeps them switched off. Turn this on to let them through.",
            _ => Scan()).Setting();
    }

    public static bool IsDebugCommand(string command) =>
        Commands.Contains(command) || command.StartsWith("debug", StringComparison.OrdinalIgnoreCase);

    /// <summary>Runs from the render hook, so the keys stay off even while the game stops ticking mods.</summary>
    public override void Frame()
    {
        // The game rebuilds its bindings when the controls change, so the table is checked again every so often.
        if (_scan.Elapsed(ScanSeconds))
            Scan();
    }

    private void Scan()
    {
        PlayerController? pc = World.Pc;
        if (_allow.On)
        {
            // Not while the menu or radial has the keys muted: H typed into the search would speed the game up.
            if (!GameKeys.IsMuted)
                GameKeys.UnblockDebug(pc);
            return;
        }

        if (pc == null)
            return;

        int blocked = GameKeys.BlockDebug(pc, IsDebugCommand);
        if (blocked > 0 && !_reported)
        {
            _reported = true;
            Log.Info("Game debug keys: switched off " + blocked + " developer bindings.");
        }
    }

    public override void Shutdown() => Safe.Run("debug keys", () => GameKeys.UnblockDebug(World.Pc));
}
