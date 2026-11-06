using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// SPAWNER/WEAPONS. A weapon on the floor is the RBMCombatThrownObject a disarm leaves: the weapon is spawned, asked
/// for its dropped form (RBMWeapon.CreateDroppedWeapon, what the game calls when a thug loses it), then removed, so
/// only the pickup remains. Thugs pick these up and fight with them. A weapon can also go straight into a character's
/// hands with RBMPawnAI.SpawnWeapon, the call the game's lockers and RSeqAct_GiveWeapon use.
/// </summary>
internal sealed partial class Spawner
{
    private const float WeaponFootprint = 40f;
    private const float WeaponLift = 40f;

    private Choice _wpnWho = null!;

    private List<WeaponRecipe> _weapons = new();

    private void BuildWeapons()
    {
        _wpnWho = AddChoice("wpn_who", "WEAPON", "Hold to scroll. Every weapon class the game has loaded here, guns, blades, shields and throwable crates included. A weapon whose model isn't loaded here is refused when spawned.",
            new[] { "" }, new[] { "OPEN THE MENU IN GAME" }).Pref();
        _wpnWho.HoldToScroll = true;
        AddCommand("wpn_refresh", "REFRESH LIST", "Reads again which weapons are loaded here.", () => RefreshWeapons(true)).Setting();
        AddCommand("wpn_spawn", "DROP ON THE GROUND", "Lays the weapon above on the ground, HOW MANY times, the way PLACE says. Thugs nearby will pick them up and use them.", () => SpawnWeapons(PickedWeapon()));
        AddCommand("wpn_give", "GIVE TO SELECTED", "Puts the weapon above in the hands of the character the world editor has selected (or the newest character you spawned).", GiveWeapon);
    }

    private void ListWeapons()
    {
        if (_weapons.Count == 0)
            _wpnWho.SetOptions(new[] { "" }, new[] { World.InGame ? "NO WEAPONS LOADED HERE" : "OPEN THE MENU IN GAME" });
        else
            _wpnWho.SetOptions(_weapons.Select(w => w.Key).ToArray(), _weapons.Select(w => w.Label).ToArray());

        // The CHARACTERS tab arms spawns from the same list.
        _chrWeapon.SetOptions(new[] { "own", "none" }.Concat(_weapons.Select(w => w.Key)).ToArray(),
            new[] { "THEIR OWN", "UNARMED" }.Concat(_weapons.Select(w => w.Label)).ToArray());
    }

    private WeaponRecipe PickedWeapon()
    {
        if (_weapons.Count == 0)
            RefreshWeapons(false, force: true);
        string key = _wpnWho.CurrentKey;
        WeaponRecipe r = _weapons.Find(w => w.Key == key) ?? throw new InvalidOperationException("no weapon picked: REFRESH LIST");
        if (!r.Class.IsValid)
        {
            RefreshWeapons(false, force: true);
            throw new InvalidOperationException("that weapon has unloaded: pick again");
        }

        return r;
    }

    private static bool HasModel(SkeletalMeshComponent? c) => c is { IsValid: true } && c.SkeletalMesh is { IsValid: true };

    private void SpawnWeapons(WeaponRecipe r)
    {
        Pawn me = RequireReady();
        int n = Room(Wanted);
        var made = new List<Spawn>();
        int failed = 0;
        string why = "it has no form that lies on the ground";
        List<Spot> spots = Spots(me, n, WeaponFootprint);
        Starting(n, r.Label, r.Key);
        foreach (Spot spot in spots)
        {
            Vector3 at = spot.Floor + Vector3.UnitZ * WeaponLift;
            if (Game.SpawnActor(r.Class, default, at, spot.Facing) is not RBMWeapon w || !w.IsValid)
            {
                failed++;
                continue;
            }

            RBMCombatThrownObject? dropped = null;
            try
            {
                if (HasModel(w.Mesh))
                    dropped = w.CreateDroppedWeapon(true, Vector3.Zero);
                else
                    why = "its model isn't loaded here";
            }
            finally
            {
                // The bare weapon was only the source of its dropped form.
                if (w.IsValid && !w.bDeleteMe)
                    Safe.Run("spawn weapon source", () => w.Destroy());
            }

            if (dropped is not { IsValid: true } || dropped.bDeleteMe)
            {
                failed++;
                continue;
            }

            if (!HasModel(dropped.SkeletalMeshComponent))
            {
                Safe.Run("spawn weapon discard", () => dropped.Destroy());
                why = "its model isn't loaded here";
                failed++;
                continue;
            }

            // Never tidied away by the game while off screen, the way a riot's dropped weapons are.
            dropped.bCanBeDestroyedWhenOffScreenAndNotInCombat = false;
            dropped.bDestroyWhenOffscreen = false;
            made.Add(Track(dropped, SpawnKind.Weapon, r.Label, at));
        }

        Finish(made, r.Label, failed, why);
    }

    private void GiveWeapon()
    {
        WeaponRecipe r = PickedWeapon();
        RBMPawnAI p = WorldEditor.I.Selected?.Actor as RBMPawnAI
            ?? _spawns.LastOrDefault(s => s.Alive && s.Actor is RBMPawnAI)?.Actor as RBMPawnAI
            ?? throw new InvalidOperationException("select a character in the world editor first");
        if (p.Health <= 0)
            throw new InvalidOperationException("they're knocked out");

        if (p.CurrWeapon is { IsValid: true } held)
        {
            if (held.Class.Ptr == r.Class.Ptr)
                throw new InvalidOperationException("they already hold one");
            p.DisappearDisarm();
        }

        RBMWeapon? got = p.SpawnWeapon(r.Class, true, false);
        if (got is not { IsValid: true })
            throw new InvalidOperationException("they can't hold that here");
        if (!HasModel(got.Mesh))
            Overlay.Notice(NoticeTitle, r.Label + " GIVEN, BUT ITS MODEL ISN'T LOADED", false);
        else
            Overlay.Notice(NoticeTitle, r.Label + " GIVEN", true);
    }

    private static WeaponRecipe WeaponRecipeOf(Class cls) => SpawnCatalog.RecipeOf(cls);
}
