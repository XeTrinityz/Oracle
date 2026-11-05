using BmSDK.BmGame;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

internal sealed class Cosmetics : Module
{
    public static readonly Cosmetics I = new();

    public override void Build()
    {
        AddToggle("hide_cape", "HIDE CAPE", "Hides the cape of Batman and other caped characters.", ApplyCape).Maintain(() => ApplyCape(true));
        Slider size = AddSlider("batman_size", "CHARACTER SIZE", "Shrinks or grows your character. Purely visual, the collision stays the same.", 1f, 0.6f, 1.6f, 0.05f, 1.2f, Fmt.Times, ApplySize);
        size.Maintain(() => ApplySize(size.Value));

        string[] keys = Enumerable.Range(0, DamageLevels).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        string[] labels = keys.Select(k => k == "0" ? "CLEAN" : "LEVEL " + k).ToArray();
        _damage = AddChoice("battle_damage", "BATTLE DAMAGE", "Shows how torn up Batman's suit is. Pick a level, then run APPLY BATTLE DAMAGE. The story still adds damage at its usual points.", keys, labels).Pref();
        AddSaveCommand("apply_battle_damage", "APPLY BATTLE DAMAGE", "Puts the chosen battle damage on Batman's suit. Your save keeps it.", ApplyDamage);
    }

    // ---- battle damage
    // The story raises it with RSeqAct_UpdateBatmanDamageLevel: RPawnPlayer.SetDamageLevel(character, level,
    // HigherDamageOnly) on the original Batman pawn, then SetupPlayerMesh(true) to re-mesh him. The level is kept in
    // the save (GRI PlayerCharacters[].DamageLevel). RPlayerController.ReceivedPlayer clears the
    // BatmanDamageLevel0..9 story flags; Oracle leaves those, and the static SetDamageLevelFlags, alone.

    private const int DamageLevels = 7;
    private Choice _damage = null!;

    public override void MenuOpening()
    {
        if (World.Ri is { } game && Showcase.TryRead(() => game.PlayerCharacters[0], out RGameRI.FLoadedPlayerCharacter loaded))
            _damage.Index = Math.Clamp(loaded.DamageLevel, 0, DamageLevels - 1);
    }

    private void ApplyDamage()
    {
        RPlayerController pc = World.Pc ?? throw new InvalidOperationException("the game is still loading");
        RGameRI game = World.Ri ?? throw new InvalidOperationException("the game is still loading");
        RPawnPlayer batman = pc.OriginalBatmanPawn;
        if (Characters.I.PlayingOther || batman is not { IsValid: true } || pc.Pawn?.Ptr != batman.Ptr)
            throw new InvalidOperationException("only for Batman, on foot");
        if (batman.Health <= 0)
            throw new InvalidOperationException("Batman is down");
        if (!Showcase.TryRead(() => game.PlayerCharacters[0], out RGameRI.FLoadedPlayerCharacter loaded))
            throw new InvalidOperationException("no loaded character data available");

        int level = _damage.Index;
        batman.SetDamageLevel(loaded.CharacterName, level, false);
        batman.SetupPlayerMesh(true);
        Log.Info("Battle damage set to " + level + " on " + loaded.CharacterName + ".");
    }

    private static void ApplySize(float v)
    {
        RPawnPlayer? p = World.Player;
        if (p != null && MathF.Abs(p.DrawScale - v) > 0.001f)
            p.SetDrawScale(v);
    }

    private static void ApplyCape(bool hidden)
    {
        if (World.Player is RPawnPlayerBmBase bm)
        {
            RCapeComponent cape = bm.CapeComponent;
            if (cape is { IsValid: true } && cape.IsCapeHiddenGame != hidden)
                cape.CapeSetHiddenGame(hidden);
        }
    }
}
