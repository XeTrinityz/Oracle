using System.Globalization;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// PLAYER > STATUS beyond god mode: how much damage you take, not dying, health regeneration, armour, hit
/// reactions and stuns, and an on-screen status readout.
/// </summary>
internal sealed class Status : Module
{
    public static readonly Status I = new();

    private const float RegenDelay = 3f;

    // The redirects can run before Build, so these may still be null.
    private Slider? _damageTaken;
    private Toggle? _cantDie;
    private Toggle? _noStagger;
    private Toggle? _noStuns;

    private Slider _regen = null!;
    private Toggle _fullArmour = null!;
    private Toggle _readout = null!;

    private readonly bool[] _grantedArmour = new bool[2];
    private nint _regenPawn;
    private int _regenLastHealth;
    private float _regenCalm;
    private float _regenPool;

    public override bool HasHud => _readout.On && World.Pawn is RPawnPlayer;

    public override void Build()
    {
        _cantDie = AddToggle("cant_die", "CAN'T DIE", "You get hurt as normal, but a blow that would finish you leaves you on 1 health instead. Falling out of the world still counts.");
        _damageTaken = AddSlider("damage_taken", "DAMAGE TAKEN", "Scales every hit you take, from a tenth up to five times as much.", 1f, 0.1f, 5f, 0.05f, 0.5f, Fmt.Times);
        _regen = AddSlider("health_regen", "HEALTH REGEN", "Heals you by this share of your health every second, once you have gone 3 seconds without being hurt.", 0f, 0f, 0.25f, 0.01f, 0.05f, PerSecond);

        _fullArmour = AddToggle("full_armour", "UNBREAKABLE ARMOUR", "Keeps your melee and ballistic armour full. Without armour upgrades you get 100 of each while this is on.", ApplyArmour);
        _fullArmour.Maintain(() => ApplyArmour(true));
        AddCommand("restore_armour", "RESTORE ARMOUR", "Refills your melee and ballistic armour right now.", RestoreArmour);

        _noStagger = AddToggle("no_stagger", "NO HIT REACTIONS", "Hits still hurt, but they no longer knock you back or interrupt what you are doing.");
        _noStuns = AddToggle("no_stuns", "NO STUNS", "Stun batons, electric attacks and laser grids no longer leave you dazed, shocked or slowed.");

        _readout = AddToggle("status_readout", "STATUS READOUT", "Shows your health, armour, experience and upgrade points on screen while you are on foot.");
    }

    public override void WorldChanged()
    {
        Array.Clear(_grantedArmour);
        _regenPawn = 0;
    }

    public override void Shutdown()
    {
        if (_fullArmour.On)
            Safe.Run("armour restore", () => ApplyArmour(false));
    }

    private static string PerSecond(float v) => v <= 0f ? "OFF" : Fmt.Percent(v) + " / S";

    private static bool IsYou(RPawnPlayer pawn) => World.Player is { } p && p.Ptr == pawn.Ptr;

    // ---- damage

    // RPawnPlayer.TakeDamage is where every hit on the player lands: combat (RPawnPlayerCombat.ApplyDamage), guns,
    // explosions and hazards. It takes resistance and armour off, then the Pawn base lowers Health. Scaling and
    // capping the damage before any of that leaves the rest of the game's rules in place. Kill volumes
    // (RDmgType_KillZone, out of the world) are left alone so Batman can't get stuck below the map.
    private int Adjust(RPawnPlayer self, int damage, Class? type)
    {
        if (damage <= 0 || !IsYou(self) || type is { IsValid: true } && (type.Name.ToString() ?? "").Contains("KillZone", StringComparison.OrdinalIgnoreCase))
            return damage;

        float scale = _damageTaken?.Value ?? 1f;
        if (MathF.Abs(scale - 1f) > 0.001f)
            damage = Math.Max(1, (int)MathF.Round(damage * scale));

        if (_cantDie is { On: true } && damage >= self.Health)
            damage = Math.Max(0, self.Health - 1);

        return damage;
    }

    [Redirect(typeof(RPawnPlayer), nameof(RPawnPlayer.TakeDamage))]
    private static void TakeDamageRedirect(RPawnPlayer self, int damage, Controller instigatedBy, Vector3 hitLocation, Vector3 momentum, Class damageType, Actor.FTraceHitInfo hitInfo, Actor damageCauser)
    {
        int adjusted = damage;
        Safe.Run("damage taken", () => adjusted = I.Adjust(self, damage, damageType));
        self.TakeDamage(adjusted, instigatedBy, hitLocation, momentum, damageType, hitInfo, damageCauser);
    }

    // ---- hit reactions and stuns

    // RPawnPlayerCombat.DamagedBy skips the RCombatMove_BatmanHit stagger (and TakeDamage the flinch overlay) when
    // RPlayerController.IgnoreHitReaction says so, which the game does itself while you are crouched in a vent.
    // Damage types marked bCantIgnoreHitReaction still play theirs.
    [Redirect(typeof(RPlayerController), nameof(RPlayerController.IgnoreHitReaction))]
    private static bool IgnoreHitReactionRedirect(RPlayerController self) =>
        (I._noStagger is { On: true } && IsYours(self)) || self.IgnoreHitReaction();

    private static bool NoStuns(RPlayerControllerCombat self) => I._noStuns is { On: true } && IsYours(self);

    private static bool IsYours(RPlayerController pc) => World.Pc is { } you && you.Ptr == pc.Ptr;

    // Damage types with bStunOnHit, bZapOnHit or bLaserOnHit call these on the controller, which turn on the dazed
    // post process and timers. Skipping them leaves the hit itself alone.
    [Redirect(typeof(RPlayerControllerCombat), nameof(RPlayerControllerCombat.Stunned))]
    private static void StunnedRedirect(RPlayerControllerCombat self, bool dontPlaySfx)
    {
        if (!NoStuns(self))
            self.Stunned(dontPlaySfx);
    }

    [Redirect(typeof(RPlayerControllerCombat), nameof(RPlayerControllerCombat.Zapped))]
    private static void ZappedRedirect(RPlayerControllerCombat self)
    {
        if (!NoStuns(self))
            self.Zapped();
    }

    [Redirect(typeof(RPlayerControllerCombat), nameof(RPlayerControllerCombat.Lasered))]
    private static void LaseredRedirect(RPlayerControllerCombat self)
    {
        if (!NoStuns(self))
            self.Lasered();
    }

    // ---- regeneration

    // The game's own replenish (RPawnPlayer.UpdateReplenishingHealth behind bReplenishHealth) is challenge-map only
    // and adds one point at a time, so Oracle heals on its own clock. Writing Health then calling HealthUpdated is
    // what the game's healing does: it updates the bar and the saved health.
    public override void Tick(float dt)
    {
        if (_regen.Value <= 0f || World.Player is not { } p)
            return;

        int health = p.Health;
        int max = p.MaxHealth;
        if (p.Ptr != _regenPawn || health < _regenLastHealth)
        {
            _regenPawn = p.Ptr;
            _regenCalm = 0f;
            _regenPool = 0f;
        }

        _regenLastHealth = health;
        if (health <= 0 || max <= 0 || health >= max)
            return;

        _regenCalm += dt;
        if (_regenCalm < RegenDelay)
            return;

        _regenPool += dt * _regen.Value * max;
        int add = (int)_regenPool;
        if (add <= 0)
            return;

        _regenPool -= add;
        p.Health = Math.Min(max, health + add);
        _regenLastHealth = p.Health;
        p.HealthUpdated();
    }

    // ---- armour

    // Armour is two pools on RPawnPlayer (melee, ballistic) that ApplyArmour drains before health. Maxima come from
    // the armour upgrades and are -1 without any. SetArmourCurrent clamps to the maximum, updates the HUD bar and
    // keeps the saved armour in step. UpdateMaximumHealthAndArmour is the game's own recount from the upgrades, so
    // it puts back maxima Oracle granted.
    private static readonly RPawnPlayer.EArmourType[] ArmourTypes = { RPawnPlayer.EArmourType.EA_ArmourMelee, RPawnPlayer.EArmourType.EA_ArmourBallistic };

    private void ApplyArmour(bool on)
    {
        if (World.Player is not { } p || p.Health <= 0)
            return;

        if (!on)
        {
            if (_grantedArmour[0] || _grantedArmour[1])
                p.UpdateMaximumHealthAndArmour(false, false);
            Array.Clear(_grantedArmour);
            return;
        }

        for (int i = 0; i < ArmourTypes.Length; i++)
        {
            if (p.GetArmourMaximum(ArmourTypes[i]) <= 0)
            {
                p.SetArmourMaximum(ArmourTypes[i], 100);
                _grantedArmour[i] = true;
            }
        }

        if (!p.IsFullArmour())
            FillArmour(p);
    }

    private static void RestoreArmour()
    {
        RPawnPlayer p = World.Player ?? throw new InvalidOperationException("the game is still loading");
        if (p.GetArmourMaximum(ArmourTypes[0]) <= 0 && p.GetArmourMaximum(ArmourTypes[1]) <= 0)
            throw new InvalidOperationException("you have no armour upgrades");
        FillArmour(p);
    }

    private static void FillArmour(RPawnPlayer p)
    {
        foreach (RPawnPlayer.EArmourType t in ArmourTypes)
        {
            int max = p.GetArmourMaximum(t);
            if (max > 0)
                p.SetArmourCurrent(t, max);
        }
    }

    // ---- readout

    public override void Draw(Canvas c)
    {
        if (World.Pawn is not RPawnPlayer p)
            return;

        int melee = p.GetArmourMaximum(ArmourTypes[0]);
        int ballistic = p.GetArmourMaximum(ArmourTypes[1]);
        bool armour = melee > 0 || ballistic > 0;
        RPersistentShared? shared = World.Info?.PersistentShared is { IsValid: true } ps ? ps : null;

        const float width = 220f;
        float height = 76f + (armour ? 28f : 0f) + (shared != null ? 28f : 0f);
        float x = Gfx.W - Gfx.S(width) - Gfx.S(28f);
        float y = Gfx.H - Gfx.S(height) - Gfx.S(48f);
        Gfx.Panel(x, y, Gfx.S(width), Gfx.S(height));

        float left = x + Gfx.S(14f);
        float right = x + Gfx.S(width - 14f);
        float barW = Gfx.S(width - 28f);
        Gfx.Text(left, y + Gfx.S(9f), Flags(), Theme.Mid, 0.6f);

        float row = y + Gfx.S(30f);
        Meter(left, right, row, barW, "HEALTH", p.Health, p.MaxHealth);
        row += Gfx.S(28f);

        if (armour)
        {
            float half = (barW - Gfx.S(10f)) * 0.5f;
            Meter(left, left + half, row, half, "MELEE", Armour(p, 0), melee);
            Meter(left + half + Gfx.S(10f), right, row, half, "BALLISTIC", Armour(p, 1), ballistic);
            row += Gfx.S(28f);
        }

        if (shared != null)
        {
            float span = shared.XpForNextUpgrade - shared.XpForPrevUpgrade;
            float into = shared.Xp - shared.XpForPrevUpgrade;
            Gfx.Text(left, row, "XP", Theme.Mid, 0.56f);
            Gfx.TextRight(right, row + Gfx.S(5f), Whole(into) + " / " + Whole(span) + "   POINTS " + shared.UpgradePointsAvailable, Theme.Hi, 0.56f);
            Bar(left, row + Gfx.S(14f), barW, span > 0f ? into / span : 0f);
        }
    }

    /// <summary>The status cheats that are on, as a header line.</summary>
    private string Flags()
    {
        var on = new List<string>(4);
        if (Player.I.God.On)
            on.Add("GOD");
        if (_cantDie is { On: true })
            on.Add("CAN'T DIE");
        if (_damageTaken is { } d && !d.IsDefault)
            on.Add("DMG " + d.State);
        if (_regen.Value > 0f)
            on.Add("REGEN");
        return on.Count == 0 ? "STATUS" : string.Join("  ", on);
    }

    // The game has no getter for the current armour, only the CurrentArmourLevels array it indexes by type.
    private static int Armour(RPawnPlayer p, int slot)
    {
        try
        {
            TArray<int> levels = p.CurrentArmourLevels;
            return slot < levels.Count ? levels[slot] : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void Meter(float left, float right, float y, float w, string label, int value, int max)
    {
        Gfx.Text(left, y, label, Theme.Mid, 0.56f);
        Gfx.TextRight(right, y + Gfx.S(5f), max > 0 ? Math.Max(0, value) + " / " + max : "-", Theme.Hi, 0.56f);
        Bar(left, y + Gfx.S(14f), w, max > 0 ? (float)value / max : 0f);
    }

    private static void Bar(float x, float y, float w, float fraction)
    {
        float h = Gfx.S(4f);
        Gfx.Rect(x, y, w, h, Theme.Mid, 0.25f);
        fraction = float.IsNaN(fraction) ? 0f : Math.Clamp(fraction, 0f, 1f);
        Gfx.Rect(x, y, w * fraction, h, Theme.Accent, 0.95f);
    }

    private static string Whole(float v) => MathF.Round(v).ToString(CultureInfo.InvariantCulture);
}
