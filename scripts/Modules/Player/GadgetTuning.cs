using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// PLAYER > GADGETS tuning for the explosive gel, freeze blast and Batclaw. The player's gadgets are character
/// subclasses (RGooSprayBm, RBatclawBm...) whose defaults live in the character's package, so the values are scaled on
/// every live object of the kind and on the defaults of each live object's own class: a shot already in the world is
/// tuned within a quarter second, and everything spawned after it starts out tuned. Read from the game's script:
/// - RExplosiveGelBomb.DetonateBomb uses ExplosionRadius; DamageVictim deals GelDamage to the thug the charge is stuck
///   on only when bLethalUpgrade is set. RExplosiveGooMine (vantage point mines) has the same fields.
/// - RFreezeSpray's four bPlayerHas...Upgrade flags come from UpdateFreezeGrenadeUpgrades (the upgrades you own),
///   which is also how they are put back; fFreezeClusterBlastRadius sizes the cluster.
/// - RHarpoonGun.Primed.BeginState takes HarpoonRange from the level volume's OverrideBatclawRange when that is set,
///   else from its class default, every time the claw is aimed.
/// </summary>
internal sealed class GadgetTuning : Module
{
    public static readonly GadgetTuning I = new();

    private const float StockDamage = 0f;

    private Toggle _gelLethal = null!;
    private Slider _gelDamage = null!;
    private Slider _gelRadius = null!;
    private Toggle _freezeUpgrades = null!;
    private Slider _freezeRadius = null!;
    private Slider _clawRange = null!;

    private readonly Tuned<RExplosiveGelBomb> _gelDmg = new("gel damage", g => g.GelDamage, (g, v) => g.GelDamage = (int)MathF.Round(v));
    private readonly Tuned<RExplosiveGooMine> _mineDmg = new("gel mine damage", g => g.GelDamage, (g, v) => g.GelDamage = (int)MathF.Round(v));
    private readonly Tuned<RExplosiveGelBomb> _gelBlast = new("gel radius", g => g.ExplosionRadius, (g, v) => g.ExplosionRadius = v);
    private readonly Tuned<RExplosiveGooMine> _mineBlast = new("gel mine radius", g => g.ExplosionRadius, (g, v) => g.ExplosionRadius = v);
    private readonly Tuned<RFreezeSpray> _freezeBlast = new("freeze cluster radius", f => f.fFreezeClusterBlastRadius, (f, v) => f.fFreezeClusterBlastRadius = v);
    private readonly Tuned<RHarpoonGun> _clawReach = new("batclaw range", g => g.HarpoonRange, (g, v) => g.HarpoonRange = v);
    private readonly Held<RLevelVolume, float> _clawVolume = new("batclaw range override", v => v.OverrideBatclawRange, (v, r) => v.OverrideBatclawRange = r);
    private readonly HashSet<nint> _upgraded = new();

    public override void Build()
    {
        _gelLethal = AddToggle("gel_lethal", "LETHAL EXPLOSIVE GEL", "A thug with an explosive gel charge stuck to him is knocked out when it blows, anywhere and as often as you like. The game only allows that in predator rooms, three times per room, on plain thugs. Brutes and bosses still shrug it off.",
            on => { if (!on) RestoreLethal(); }).Maintain(ApplyLethal);
        _gelDamage = AddSlider("gel_damage", "GEL DAMAGE", "How much health an explosive gel blast takes off every other thug in range, with LETHAL EXPLOSIVE GEL on. A street thug has about 25.",
            StockDamage, StockDamage, 200f, 5f, 100f, DamageLabel, _ => ApplyGel());
        _gelDamage.Maintain(ApplyGel);
        _gelRadius = AddSlider("gel_radius", "GEL BLAST RADIUS", "How far an explosive gel blast reaches, for charges and vantage point mines alike.",
            1f, 0.5f, 4f, 0.25f, 2f, Fmt.Times, _ => ApplyGel());
        _gelRadius.Maintain(ApplyGel);

        _freezeUpgrades = AddToggle("freeze_upgrades", "ALL FREEZE BLAST UPGRADES", "The freeze blast behaves as if every one of its upgrades were bought: cluster blast, longer freeze, proximity triggers. Off goes back to the upgrades you own.",
            on => { if (!on) RestoreFreezeUpgrades(); }).Maintain(GrantFreezeUpgrades);
        _freezeRadius = AddSlider("freeze_radius", "FREEZE CLUSTER RADIUS", "How wide the freeze blast's cluster spreads, when you have that upgrade or ALL FREEZE BLAST UPGRADES is on.",
            1f, 0.5f, 4f, 0.25f, 2f, Fmt.Times, _ => ApplyFreeze());
        _freezeRadius.Maintain(ApplyFreeze);

        _clawRange = AddSlider("batclaw_range", "BATCLAW RANGE", "How far the Batclaw reaches, for thugs, vents and everything else it grabs.",
            1f, 0.5f, 5f, 0.25f, 2f, Fmt.Times, _ => ApplyClaw());
        _clawRange.Maintain(ApplyClaw);
    }

    public override void WorldChanged() => Forget();

    public override void LeftGame() => Forget();

    public override void Shutdown()
    {
        _gelDmg.Restore();
        _mineDmg.Restore();
        _gelBlast.Restore();
        _mineBlast.Restore();
        _freezeBlast.Restore();
        _clawReach.Restore();
        _clawVolume.Restore();
        if (_gelLethal.On)
            RestoreLethal();
        if (_freezeUpgrades.On)
            RestoreFreezeUpgrades();
    }

    /// <summary>Every tracked object belonged to the world that just ended: nothing to put back, only to forget.</summary>
    private void Forget()
    {
        _gelDmg.Forget();
        _mineDmg.Forget();
        _gelBlast.Forget();
        _mineBlast.Forget();
        _freezeBlast.Forget();
        _clawReach.Forget();
        _clawVolume.Restore();
        _upgraded.Clear();
        _lethal.Clear();
    }

    private static string DamageLabel(float v) => v <= StockDamage ? "STOCK" : Fmt.Count(v) + " HP";

    private static bool Stock(float scale) => MathF.Abs(scale - 1f) < 0.001f;

    /// <summary>The live objects of a kind plus the defaults of each one's own class, which later spawns copy.</summary>
    private static IEnumerable<T> Kind<T>() where T : GameObject
    {
        var classes = new HashSet<nint>();
        foreach (T obj in World.All<T>())
        {
            yield return obj;
            Class cls = obj.Class;
            if (cls is { IsValid: true } && classes.Add(cls.Ptr) && cls.DefaultObject is T defaults && defaults.IsValid)
                yield return defaults;
        }
    }

    // ---- explosive gel

    private readonly HashSet<nint> _lethal = new();

    /// <summary>
    /// The game's gate on gel hurting thugs: only while the room's RBMRoomAIState.AreThugsKnockedOutByGooMines (fewer
    /// than three gel takedowns so far) and only plain thugs (ECPT_Thug). With LETHAL EXPLOSIVE GEL on, every fighter
    /// counts, every time; the multi-stage bosses, robots and the Riddler's mech keep their immunity.
    /// </summary>
    [Redirect(typeof(RExplosiveGelBomb), nameof(RExplosiveGelBomb.ShouldDoDamageToThug))]
    private static bool ShouldDoDamageToThugRedirect(RExplosiveGelBomb self, RPawnVillain thug)
    {
        if (!Safe.Try("gel damage gate", () => I._gelLethal.On && thug is { IsValid: true }))
            return self.ShouldDoDamageToThug(thug);

        switch (thug.CombatPawnType)
        {
            case RPawnCombat.ECombatPawnType.ECPT_Thug:
            case RPawnCombat.ECombatPawnType.ECPT_ToughThug:
            case RPawnCombat.ECombatPawnType.ECPT_Lieutenant:
            case RPawnCombat.ECombatPawnType.ECPT_Venom:
            case RPawnCombat.ECombatPawnType.ECPT_Ninja:
            case RPawnCombat.ECombatPawnType.ECPT_Outcast:
            case RPawnCombat.ECombatPawnType.ECPT_Tonfa:
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The game's lethal branch hands the stuck thug a hit worth his whole health (DamagedBy, RDmgType_ExplosiveGelStickyBomb,
    /// 10 s stun), but in game it took nothing off (19 -> 19 HP in a street fight, 2026-11-07). So after it, a thug the
    /// charge was stuck to who is still up is knocked out with TakeDamage, the path turncoat hits use (confirmed in game).
    /// </summary>
    [Redirect(typeof(RExplosiveGelBomb), nameof(RExplosiveGelBomb.DamageVictim))]
    private static void DamageVictimRedirect(RExplosiveGelBomb self, RPawnVillain victim)
    {
        self.DamageVictim(victim);
        Safe.Run("lethal gel", () => I.Finish(self, victim));
    }

    private void Finish(RExplosiveGelBomb gel, RPawnVillain victim)
    {
        if (!_gelLethal.On || !victim.IsValid || gel.StuckOnPawn is not { IsValid: true } stuck || stuck.Ptr != victim.Ptr)
            return;
        if (victim.Health <= 0 || victim.bGoodAsDead || !ShouldDoDamageToThugRedirect(gel, victim))
            return;

        Vector3 push = Vector3.Normalize(victim.Location - gel.Location + new Vector3(0f, 0f, 1f)) * 2000f;
        victim.TakeDamage(victim.Health + 100, gel.GooSprayer?.Controller!, victim.Location, push, RDmgType_ExplosiveGel.StaticClass(), default, gel);
    }

    private void ApplyLethal()
    {
        foreach (RExplosiveGelBomb gel in Kind<RExplosiveGelBomb>())
        {
            _lethal.Add(gel.Ptr);
            if (!gel.bLethalUpgrade)
                gel.bLethalUpgrade = true;
        }

        foreach (RExplosiveGooMine mine in Kind<RExplosiveGooMine>())
        {
            _lethal.Add(mine.Ptr);
            if (!mine.bLethalUpgrade)
                mine.bLethalUpgrade = true;
        }
    }

    private void RestoreLethal()
    {
        foreach (RExplosiveGelBomb gel in Kind<RExplosiveGelBomb>())
        {
            if (_lethal.Contains(gel.Ptr))
                gel.bLethalUpgrade = false;
        }

        foreach (RExplosiveGooMine mine in Kind<RExplosiveGooMine>())
        {
            if (_lethal.Contains(mine.Ptr))
                mine.bLethalUpgrade = false;
        }

        _lethal.Clear();
    }

    private void ApplyGel()
    {
        float damage = _gelDamage.Value;
        if (damage <= StockDamage)
        {
            _gelDmg.Restore();
            _mineDmg.Restore();
        }
        else
        {
            _gelDmg.Apply(Kind<RExplosiveGelBomb>(), _ => damage);
            _mineDmg.Apply(Kind<RExplosiveGooMine>(), _ => damage);
        }

        float radius = _gelRadius.Value;
        if (Stock(radius))
        {
            _gelBlast.Restore();
            _mineBlast.Restore();
        }
        else
        {
            _gelBlast.Apply(Kind<RExplosiveGelBomb>(), v => v * radius);
            _mineBlast.Apply(Kind<RExplosiveGooMine>(), v => v * radius);
        }
    }

    // ---- freeze blast

    private void GrantFreezeUpgrades()
    {
        foreach (RFreezeSpray spray in World.All<RFreezeSpray>())
        {
            if (spray.bDeleteMe)
                continue;
            _upgraded.Add(spray.Ptr);
            if (!spray.bPlayerHasFreezeClusterUpgrade) spray.bPlayerHasFreezeClusterUpgrade = true;
            if (!spray.bPlayerHasFreezeClusterProximityUpgrade) spray.bPlayerHasFreezeClusterProximityUpgrade = true;
            if (!spray.bPlayerHasFreezeDurationUpgrade) spray.bPlayerHasFreezeDurationUpgrade = true;
            if (!spray.bPlayerHasFreezeGrenadeProximityUpgrade) spray.bPlayerHasFreezeGrenadeProximityUpgrade = true;
        }
    }

    /// <summary>The game's own recalculation from the upgrades you own, on every spray this module touched.</summary>
    private void RestoreFreezeUpgrades()
    {
        foreach (RFreezeSpray spray in World.All<RFreezeSpray>())
        {
            if (_upgraded.Contains(spray.Ptr))
                Safe.Run("freeze upgrades restore", spray.UpdateFreezeGrenadeUpgrades);
        }

        _upgraded.Clear();
    }

    private void ApplyFreeze()
    {
        float scale = _freezeRadius.Value;
        if (Stock(scale))
            _freezeBlast.Restore();
        else
            _freezeBlast.Apply(Kind<RFreezeSpray>(), v => v * scale);
    }

    // ---- batclaw

    private void ApplyClaw()
    {
        float scale = _clawRange.Value;
        if (Stock(scale))
        {
            _clawReach.Restore();
            _clawVolume.Restore();
            return;
        }

        _clawReach.Apply(Kind<RHarpoonGun>(), v => v * scale);

        RLevelVolume? volume = World.Ri?.CurrentLevelVolume;
        if (volume is { IsValid: true } && (volume.OverrideBatclawRange > 0f || _clawVolume.Holds(volume)))
            _clawVolume.Set(volume, v => v > 0f ? v * scale : v);
        else
            _clawVolume.Restore();
    }
}

/// <summary>
/// One game value scaled on however many objects currently carry it. Each object's stock value is remembered the
/// first time it is seen, so the scale is always applied to the game's own number, and <see cref="Restore"/> puts
/// every object still alive back. Objects the game has destroyed are forgotten.
/// </summary>
internal sealed class Tuned<T> where T : GameObject
{
    private readonly string _what;
    private readonly Func<T, float> _get;
    private readonly Action<T, float> _set;
    private readonly Dictionary<nint, (T Obj, float Stock)> _held = new();

    public Tuned(string what, Func<T, float> get, Action<T, float> set)
    {
        _what = what;
        _get = get;
        _set = set;
    }

    public void Apply(IEnumerable<T> objects, Func<float, float> fromStock)
    {
        foreach (T obj in objects)
        {
            if (!obj.IsValid)
                continue;

            if (!_held.TryGetValue(obj.Ptr, out (T Obj, float Stock) entry) || !entry.Obj.IsValid)
            {
                entry = (obj, _get(obj));
                _held[obj.Ptr] = entry;
            }

            float want = fromStock(entry.Stock);
            if (MathF.Abs(_get(obj) - want) > 0.0001f)
                _set(obj, want);
        }
    }

    public void Restore()
    {
        foreach ((T obj, float stock) in _held.Values)
        {
            if (obj.IsValid)
                Safe.Run(_what + " restore", () => _set(obj, stock));
        }

        _held.Clear();
    }

    public void Forget() => _held.Clear();
}
