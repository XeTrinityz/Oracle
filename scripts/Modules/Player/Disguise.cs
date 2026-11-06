using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// Dresses the player as any character the game has loaded, keeping everything else theirs: moves, fights, gadgets.
/// Looks come from every RCharacterDefine in memory (the recipe the game builds each NPC from) or from a character
/// in the crosshair, copied exactly.
/// It copies the game's own Joker hallucination (RRiotBrain.BecomeJoker): each piece of the borrowed look is a new
/// SkeletalMeshComponent on the player whose bones follow the player's mesh (SetParentAnimComponent, PACM_RelativeToReferencePose, so each body keeps its own proportions),
/// while the player's own mesh, extra pieces and cape are hidden. The player's mesh keeps animating underneath, so
/// gadget sockets and every animation still work. Only what is loaded can be worn, and nothing reaches the save.
/// </summary>
internal sealed class Disguise : Module
{
    public static readonly Disguise I = new();

    private const string NoticeTitle = "DISGUISE";
    private const float Metre = 100f;
    private const float Reach = 80f * Metre;
    private const float PickCone = 6f;
    private const RSkeletalMeshComponent_Export.EParentAnimComponentMode FollowParentMode = RSkeletalMeshComponent_Export.EParentAnimComponentMode.PACM_RelativeToReferencePose;
    private const int MaxMaterials = 16;
    private const float MaxVariants = 999f;
    private const string ActorKey = "actor:";
    private const string SuitKey = "suit:";
    private const string AroundGroup = "AROUND YOU";
    private const string SuitGroup = "SUITS";

    /// <summary>One piece of a borrowed look, copied out of the character so it outlives them.</summary>
    private sealed class Piece
    {
        public SkeletalMesh? Skeletal;
        public StaticMesh? Static;
        public PhysicsAsset? Physics;
        public MaterialInterface?[] Materials = Array.Empty<MaterialInterface?>();
        public bool FollowsBones;
        public RSkeletalMeshComponent_Export.EParentAnimComponentMode Mode;
        public FName Bone;
        public FName Socket;
        public Vector3 Location;
        public Rotator Rotation;
        public Vector3 Scale = Vector3.One;
    }

    private sealed record Look(string Name, List<Piece> Pieces);

    private Toggle _on = null!;
    private Choice _pick = null!;
    private Slider _variant = null!;
    private Look? _look;
    private RPawnPlayer? _wearer;
    private SkeletalMeshComponent? _body;
    private readonly List<SkeletalMeshComponent> _followers = new();
    private readonly List<PrimitiveComponent> _props = new();
    private readonly HashSet<nint> _worn = new();
    private readonly Dictionary<nint, PrimitiveComponent> _hidden = new();
    private bool _capeWasHidden;
    private bool _oldUpdateSkel;

    // ---- fitting the game's things to the worn body
    private Toggle _fitCamera = null!, _fitGadgets = null!;
    private FName _head = FName.None;
    private readonly Dictionary<nint, Fit> _fits = new();

    /// <summary>
    /// One thing the game attached to the player's (hidden) mesh, moved onto the worn body. Orig is what the game
    /// asked for; Last is what Oracle wrote, so a fresh attach by the game (different values) is told apart.
    /// </summary>
    private sealed class Fit
    {
        public int Bone;
        public FName Anchor;
        public Vector3 OrigLoc, LastLoc;
        public Rotator OrigRot, LastRot;
        public bool Seen;
    }

    public override void Build()
    {
        _pick = AddChoice("disguise_look", "LOOK",
            "Every character the game has loaded right now: militia, gangs, police, civilians, and any story character nearby. The list changes with where you are. Pick one, then turn DISGUISE on; while it's on, picking another swaps straight to it.",
            new[] { "" }, new[] { "OPEN MENU IN GAME" }, _ => OnLook()).Pref();
        _pick.HoldToScroll = true;
        _variant = AddSlider("disguise_variant", "VARIANT",
            "Every version of the LOOK the game can make: hats and masks on or off, other colours, another head or jacket. It runs from STANDARD to however many that character has; each number is always the same version. While DISGUISE is on, stepping swaps straight to it.",
            0f, 0f, MaxVariants, 1f, 1f, VariantLabel, _ => OnPick()).Pref();
        _on = AddToggle("disguise", "DISGUISE",
            "Wears the LOOK above. Everything else stays yours: moves, fights, gadgets. Switch it off to be yourself again. A reload takes it off.",
            OnToggle).Unsaved();
        AddCommand("disguise_wear", "WEAR AIMED CHARACTER", "Copies the exact look of the character in your crosshair, their random hat or colours included, and wears it.", () =>
        {
            Put(Aimed() ?? throw new InvalidOperationException("aim at a character first"));
            _on.On = true;
        });
        _fitCamera = AddToggle("disguise_fit_camera", "FIT CAMERA",
            "Moves the camera to suit the worn body: lower and closer for a smaller character, higher and further for a bigger one, and into its eyes in first person. Not in the Batmobile or cutscenes.",
            on: true).Pref();
        _fitGadgets = AddToggle("disguise_fit_gadgets", "FIT GADGETS",
            "Puts what you hold and carry on the worn body: gadgets in its hands and on its belt, the call hologram on its arm, and anything else the game fixes to you. Off, they sit where Batman's own hands would be.",
            on: true).Pref();
    }

    public override void MenuOpening()
    {
        Safe.Run("disguise looks", RefreshLooks);
        Safe.Run("disguise variants", RefreshVariants);
    }

    private void OnLook()
    {
        Safe.Run("disguise variants", RefreshVariants);
        OnPick();
    }

    private string VariantLabel(float v)
    {
        int max = (int)_variant.Max;
        if (max <= 0)
            return "STANDARD ONLY";
        return v < 0.5f ? "STANDARD (" + max + " MORE)" : "#" + (int)v + " OF " + max;
    }

    /// <summary>Fits VARIANT to the picked character: STANDARD up to its last combination, at most MaxVariants.</summary>
    private void RefreshVariants()
    {
        long total = 1;
        if (PickedDefine() is { } d)
        {
            try
            {
                FromDefine(d, "", 0, out total);
            }
            catch (InvalidOperationException)
            {
                total = 1;
            }
        }

        _variant.Max = (float)Math.Min(Math.Max(0, total - 1), MaxVariants);
        _variant.Value = Math.Clamp(_variant.Value, 0f, _variant.Max);
    }

    private void OnToggle(bool on)
    {
        if (!on)
        {
            TakeOff();
            return;
        }

        Try(() => Put(Picked()));
    }

    private void OnPick()
    {
        if (_on.On)
            Try(() => Put(Picked()));
    }

    private void Try(Action wear)
    {
        try
        {
            wear();
        }
        catch (Exception ex)
        {
            _on.On = _look != null;
            Log.Info("DISGUISE failed: " + ex.Message);
            Overlay.Notice(NoticeTitle, ex.Message.ToUpperInvariant(), false);
        }
    }

    public override void Tick(float dt)
    {
        if (_look == null)
            return;

        if (Characters.I.Busy)
            return;

        RPawnPlayer? player = World.Player;
        if (player is not { IsValid: true, bDeleteMe: false })
            return;

        // A new body (character switch, respawn): move the look over to it.
        if (_wearer == null || !_wearer.IsValid || _wearer.Ptr != player.Ptr)
        {
            try
            {
                Strip();
                Dress(player, _look);
            }
            catch (Exception ex)
            {
                Safe.Run("disguise off", TakeOff);
                Log.Info("DISGUISE came off: " + ex.Message);
                Overlay.Notice(NoticeTitle, "TAKEN OFF", false);
            }

            return;
        }

        Keep(player);
    }

    public override void WorldChanged()
    {
        _person.Clear();
        // Every component and mesh is about to go stale; drop them without touching the old world.
        Forget();
        _look = null;
        _on.On = false;
    }

    public override void LeftGame() => WorldChanged();

    public override void Shutdown() => Safe.Run("disguise", TakeOff);

    // ---- picking

    /// <summary>Puts a look on the player, replacing any worn one.</summary>
    private void Put(Look look)
    {
        Transition.RequireIdle();
        if (Characters.I.Busy)
            throw new InvalidOperationException("wait for the character switch to finish");

        RPawnPlayer player = World.Player ?? throw new InvalidOperationException("the game is still loading");
        Strip();
        _look = look;
        Dress(player, look);
        int flapping = _followers.Count(f => f.IsValid && f.FlapsAssetInstance is { IsValid: true });
        Log.Info("disguise: wearing " + look.Name + " (" + Fmt.Plural(look.Pieces.Count, "piece") + ", " + flapping + " with flaps physics).");
        Overlay.Notice(NoticeTitle, look.Name, true);
    }

    /// <summary>Every loaded character recipe with something to wear, by group then name.</summary>
    /// <summary>
    /// The "in Batmobile" passenger recipes (MOST WANTED IN BAT MOBILE: Penguin, Pyg, Man-Bat...) all wear a broken
    /// Joker model when put on (user, 2026-11-06), so they're left out.
    /// </summary>
    private static bool InBatmobile(string path) =>
        path.Replace("_", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).Contains("batmobile", StringComparison.OrdinalIgnoreCase);

    private void RefreshLooks()
    {
        if (!World.InGame)
            return;

        var rows = new List<(string Key, string Group, string Name)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RCharacterDefine d in World.All<RCharacterDefine>())
        {
            if (!HasBody(d))
                continue;

            string path = SpawnCatalog.PathOf(d);
            if (InBatmobile(path) || !seen.Add(path))
                continue;

            rows.Add((path, SpawnCatalog.GroupOf(path, false), SpawnCatalog.Pretty(d.Name.ToString() ?? "CHARACTER")));
        }

        Safe.Run("disguise other looks", () => AddOtherLooks(rows));

        if (rows.Count == 0)
        {
            _pick.SetOptions(new[] { "" }, new[] { "NO CHARACTERS LOADED" });
            return;
        }

        rows.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.Group, b.Group);
            return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
        });
        _pick.SetOptions(rows.Select(r => r.Key).ToArray(), rows.Select(r => r.Group + ": " + r.Name).ToArray());
    }

    // ---- other looks in memory

    /// <summary>Whether a mesh is a person, remembered per mesh: many actors share one, and the test is several calls.</summary>
    private readonly Dictionary<nint, bool> _person = new();

    /// <summary>
    /// Looks in memory that no character recipe covers: people around you built some other way, characters placed in
    /// the level (story characters, cutscene stand-ins), and loaded playable suits. Bodies a recipe already offers are
    /// skipped, and only meshes with a person's skeleton are kept.
    /// </summary>
    private void AddOtherLooks(List<(string Key, string Group, string Name)> rows)
    {
        FName[] bones = PersonBones();
        if (bones.Length == 0)
            return;

        var covered = new HashSet<nint>();
        foreach (RCharacterDefine d in World.All<RCharacterDefine>())
        {
            foreach (RCharacterDefine.FMainMeshArray slot in d.CharacterMeshes)
            {
                foreach (SkeletalMesh? m in slot.Meshes)
                {
                    if (m is { IsValid: true })
                        covered.Add(m.Ptr);
                }
            }
        }

        nint player = World.Player?.Ptr ?? 0;
        nint batman = World.Pc?.OriginalBatmanPawn?.Ptr ?? 0;
        var bodies = new HashSet<nint>();

        void Offer(Actor a, string group)
        {
            if (a.bDeleteMe || a.Ptr == player || a.Ptr == batman || BodyOf(a) is not { } body || body.SkeletalMesh is not { IsValid: true } mesh)
                return;
            if (covered.Contains(mesh.Ptr) || !bodies.Add(mesh.Ptr) || !IsPerson(body, mesh, bones))
                return;

            rows.Add((ActorKey + SpawnCatalog.PathOf(a), group, SpawnCatalog.Pretty(mesh.Name.ToString() ?? "CHARACTER")));
        }

        foreach (RPawn p in World.All<RPawn>())
            Offer(p, AroundGroup);
        foreach (SkeletalMeshActor a in World.All<SkeletalMeshActor>())
            Offer(a, AroundGroup);

        foreach (RAddContentPlayerCharacterMesh suit in World.All<RAddContentPlayerCharacterMesh>())
        {
            if (SuitMeshes(suit).Count > 0)
                rows.Add((SuitKey + SpawnCatalog.PathOf(suit), SuitGroup, SpawnCatalog.Pretty(suit.Name.ToString() ?? "SUIT")));
        }
    }

    private static SkeletalMeshComponent? BodyOf(Actor a) => a switch
    {
        RPawn p => p.Mesh is { IsValid: true } m ? m : null,
        SkeletalMeshActor s => s.SkeletalMeshComponent is { IsValid: true } m ? m : null,
        _ => null,
    };

    /// <summary>
    /// The bones the player's mesh hangs its sockets on (hands, head, spine: where gadgets and effects attach). A
    /// person's skeleton has most of them by name; doors, flags and cranes have none.
    /// </summary>
    private static FName[] PersonBones()
    {
        if (World.Player?.Mesh?.SkeletalMesh is not { IsValid: true } own)
            return Array.Empty<FName>();

        var bones = new Dictionary<int, FName>();
        foreach (SkeletalMeshSocket? socket in own.Sockets)
        {
            if (socket is { IsValid: true } && socket.BoneName.Index != FName.None.Index)
                bones.TryAdd(socket.BoneName.Index, socket.BoneName);
        }

        return bones.Count >= 3 ? bones.Values.ToArray() : Array.Empty<FName>();
    }

    private bool IsPerson(SkeletalMeshComponent c, SkeletalMesh mesh, FName[] bones)
    {
        if (_person.TryGetValue(mesh.Ptr, out bool yes))
            return yes;

        // GetParentBone returns None for a bone the mesh doesn't have (and for its root, which sockets rarely use).
        int found = 0;
        foreach (FName bone in bones)
        {
            if (c.GetParentBone(bone).Index != FName.None.Index)
                found++;
        }

        yes = found * 4 >= bones.Length * 3;
        _person[mesh.Ptr] = yes;
        return yes;
    }

    private static Actor? FindActor(string path)
    {
        foreach (Actor a in World.All<RPawn>())
        {
            if (!a.bDeleteMe && SpawnCatalog.PathOf(a).Equals(path, StringComparison.OrdinalIgnoreCase))
                return a;
        }

        foreach (Actor a in World.All<SkeletalMeshActor>())
        {
            if (!a.bDeleteMe && SpawnCatalog.PathOf(a).Equals(path, StringComparison.OrdinalIgnoreCase))
                return a;
        }

        return null;
    }

    private static RAddContentPlayerCharacterMesh? FindSuit(string path)
    {
        foreach (RAddContentPlayerCharacterMesh s in World.All<RAddContentPlayerCharacterMesh>())
        {
            if (SpawnCatalog.PathOf(s).Equals(path, StringComparison.OrdinalIgnoreCase))
                return s;
        }

        return null;
    }

    /// <summary>A playable suit's pieces, main mesh first. The cape is left out: the player's cape cloth drives it, not the body.</summary>
    private static List<SkeletalMesh> SuitMeshes(RAddContentPlayerCharacterMesh suit)
    {
        var list = new List<SkeletalMesh>();
        void Add(SkeletalMesh? m)
        {
            if (m is { IsValid: true } && !list.Exists(x => x.Ptr == m.Ptr))
                list.Add(m);
        }

        Add(suit.MainMesh);
        Add(suit.BodyMesh);
        foreach (SkeletalMesh? m in suit.ExtraMeshes)
            Add(m);
        Add(suit.StandaloneBeltMesh);
        return list;
    }

    private static Look FromSuit(RAddContentPlayerCharacterMesh suit, string name)
    {
        PhysicsAsset? physics = suit.PhysicsAsset is { IsValid: true } pa ? pa : null;
        var pieces = SuitMeshes(suit).Select(m => new Piece
        {
            Skeletal = m,
            Physics = physics,
            FollowsBones = true,
            Mode = FollowParentMode,
        }).ToList();

        return pieces.Count == 0 ? throw new InvalidOperationException("that suit has nothing loaded") : new Look(name, pieces);
    }

    private Look Picked()
    {
        if (_pick.CurrentKey.Length == 0)
            throw new InvalidOperationException("pick a LOOK first");

        string key = _pick.CurrentKey;
        string name = _pick.State[(_pick.State.IndexOf(": ", StringComparison.Ordinal) + 1)..].Trim();
        if (key.StartsWith(ActorKey, StringComparison.Ordinal))
        {
            Actor a = FindActor(key[ActorKey.Length..]) ?? throw new InvalidOperationException("they aren't around any more, reopen the menu");
            return Copy(a, BodyOf(a) ?? throw new InvalidOperationException("they have no body to copy"), name);
        }

        if (key.StartsWith(SuitKey, StringComparison.Ordinal))
        {
            RAddContentPlayerCharacterMesh suit = FindSuit(key[SuitKey.Length..]) ?? throw new InvalidOperationException("that suit isn't loaded any more, reopen the menu");
            return FromSuit(suit, name);
        }

        RCharacterDefine d = PickedDefine() ?? throw new InvalidOperationException("that character isn't loaded any more, reopen the menu");
        return FromDefine(d, _pick.State, (long)MathF.Round(_variant.Value), out _);
    }

    private RCharacterDefine? PickedDefine()
    {
        string key = _pick.CurrentKey;
        if (key.Length == 0 || !World.InGame || key.StartsWith(ActorKey, StringComparison.Ordinal) || key.StartsWith(SuitKey, StringComparison.Ordinal))
            return null;

        foreach (RCharacterDefine d in World.All<RCharacterDefine>())
        {
            if (SpawnCatalog.PathOf(d).Equals(key, StringComparison.OrdinalIgnoreCase))
                return d;
        }

        return null;
    }

    /// <summary>The character nearest the middle of the screen, within reach, copied exactly.</summary>
    private static Look? Aimed()
    {
        RPawnPlayer player = World.Player ?? throw new InvalidOperationException("the game is still loading");
        if (!WorldView.ReadCamera())
            throw new InvalidOperationException("camera unavailable");

        nint batman = World.Pc?.OriginalBatmanPawn?.Ptr ?? 0;
        RPawn? best = null;
        float bestScore = float.MaxValue;
        foreach (RPawn p in World.All<RPawn>())
        {
            if (p.bDeleteMe || p.bHidden || p.Ptr == player.Ptr || p.Ptr == batman)
                continue;
            if (p.Mesh is not { IsValid: true } mesh || mesh.SkeletalMesh is not { IsValid: true } || mesh.HiddenGame)
                continue;

            Vector3 centre = p.Location;
            float dist = Vector3.Distance(centre, WorldView.Eye);
            if (dist < 1f || dist > Reach)
                continue;

            float size = MathF.Atan2(p.CylinderComponent is { IsValid: true } cyl ? MathF.Max(cyl.CollisionHeight, cyl.CollisionRadius) : 90f, dist) * 180f / MathF.PI;
            float outside = WorldView.AngleTo(centre) - size;
            if (outside > PickCone)
                continue;

            float score = MathF.Max(0f, outside) * 100f + dist / Metre;
            if (score < bestScore)
            {
                bestScore = score;
                best = p;
            }
        }

        return best == null ? null : Copy(best);
    }

    // ---- recipes

    // A prop that only some spawns get is worn when at least this share of them have it.
    private const float CommonProp = 50f;

    private static bool HasBody(RCharacterDefine d)
    {
        foreach (RCharacterDefine.FMainMeshArray slot in d.CharacterMeshes)
        {
            foreach (SkeletalMesh? m in slot.Meshes)
            {
                if (m is { IsValid: true })
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The look a character recipe builds, by variant number. Every choice the recipe offers is one digit: each prop
    /// (on with one of its options, or off), each body slot's colour swap, and each body slot's mesh. Option 0 of every
    /// digit is the standard look (the recipe's WhichMeshesToUse pick, else the first mesh, as BecomeJoker takes
    /// Joker's; its own colours; the props most spawns carry), so variant 0 is STANDARD and each other number is a
    /// different combination. Props are the lowest digits, so stepping the number tries hats and masks first.
    /// <paramref name="total"/> is how many combinations there are. Body-size matched parts (thug maker) keep to the
    /// size of the body picked, which can leave a later digit with fewer options; the number then wraps within them.
    /// </summary>
    private static Look FromDefine(RCharacterDefine d, string label, long variant, out long total)
    {
        int slots = d.CharacterMeshes.Count;

        // The standard body decides the digit sizes; meshes are picked again below with the real digits.
        List<SkeletalMesh>[] standardMeshes = SlotMeshes(d, null);
        var standardNames = new List<string>();
        string? standardSize = null;
        foreach (List<SkeletalMesh> options in standardMeshes)
        {
            if (options.Count == 0)
                continue;
            standardNames.Add(options[0].Name.ToString() ?? "");
            standardSize ??= BodySize(options[0]);
        }

        if (standardNames.Count == 0)
            throw new InvalidOperationException("that character has no body loaded");

        var sizes = new List<int>();
        foreach (RCharacterDefine.FCharacterStaticPropsDef sp in d.StaticProps)
            sizes.Add(StaticOptions(sp, standardNames).Count);
        foreach (RCharacterDefine.FCharacterSkeletalPropsDef sp in d.SkeletalProps)
            sizes.Add(SkeletalOptions(sp, standardNames, standardSize).Count);
        for (int i = 0; i < slots; i++)
            sizes.Add(d.CharacterMeshes[i].ThugMakerMaterialSwaps.Count + 1);
        for (int i = 0; i < slots; i++)
            sizes.Add(Math.Max(1, SlotMeshes(d, standardSize, i).Count));

        total = 1;
        var digits = new int[sizes.Count];
        long rest = Math.Max(0, variant);
        for (int i = 0; i < sizes.Count; i++)
        {
            int k = Math.Max(1, sizes[i]);
            digits[i] = (int)(rest % k);
            rest /= k;
            total = total > long.MaxValue / k ? long.MaxValue : total * k;
        }

        int staticAt = 0;
        int skeletalAt = d.StaticProps.Count;
        int swapAt = skeletalAt + d.SkeletalProps.Count;
        int meshAt = swapAt + slots;

        // Body slots: the first sets the body size the rest keep to.
        var pieces = new List<Piece>();
        var names = new List<string>();
        PhysicsAsset? physics = d.PrimaryPhysicsAsset is { IsValid: true } pa ? pa : null;
        string? size = null;
        for (int i = 0; i < slots; i++)
        {
            List<SkeletalMesh> options = SlotMeshes(d, size, i);
            if (options.Count == 0)
                continue;

            SkeletalMesh mesh = options[digits[meshAt + i] % options.Count];
            RCharacterDefine.FMainMeshArray slot = d.CharacterMeshes[i];
            int swap = digits[swapAt + i];
            MaterialInterface?[] mats = swap > 0 && swap <= slot.ThugMakerMaterialSwaps.Count
                ? Mats(slot.ThugMakerMaterialSwaps[swap - 1].Materials)
                : Overrides(d, mesh);

            pieces.Add(new Piece
            {
                Skeletal = mesh,
                Physics = physics,
                Materials = mats,
                FollowsBones = true,
                Mode = FollowParentMode,
            });
            names.Add(mesh.Name.ToString() ?? "");
            size ??= BodySize(mesh);
        }

        int n = 0;
        foreach (RCharacterDefine.FCharacterSkeletalPropsDef sp in d.SkeletalProps)
        {
            List<RCharacterDefine.FMultiSkeletonProp?> options = SkeletalOptions(sp, names, size);
            int digit = digits[skeletalAt + n++];
            if (options.Count == 0 || options[digit % options.Count] is not { } picked)
                continue;

            pieces.Add(new Piece
            {
                Skeletal = picked.Prop,
                Physics = picked.PropPhys is { IsValid: true } pp ? pp : null,
                Materials = Mats(picked.OverrideMaterials),
                FollowsBones = sp.bUsesParentAnim,
                Mode = FollowParentMode,
                Bone = sp.BoneToAttachPropTo,
                Socket = sp.SocketToAttachPropTo,
            });
        }

        n = 0;
        foreach (RCharacterDefine.FCharacterStaticPropsDef sp in d.StaticProps)
        {
            List<RCharacterDefine.FMultiStaticProp?> options = StaticOptions(sp, names);
            int digit = digits[staticAt + n++];
            if (options.Count == 0 || options[digit % options.Count] is not { } picked)
                continue;

            pieces.Add(new Piece
            {
                Static = picked.Prop,
                Materials = Mats(picked.OverrideMaterials),
                Bone = sp.BoneToAttachPropTo,
                Socket = sp.SocketToAttachPropTo,
            });
        }

        int colon = label.IndexOf(": ", StringComparison.Ordinal);
        string name = colon >= 0 ? label[(colon + 2)..] : label;
        return new Look(variant <= 0 ? name : name + " #" + variant, pieces);
    }

    /// <summary>Each body slot's meshes, standard pick first; slots after the body keep to its size when known.</summary>
    private static List<SkeletalMesh>[] SlotMeshes(RCharacterDefine d, string? size)
    {
        var all = new List<SkeletalMesh>[d.CharacterMeshes.Count];
        for (int i = 0; i < all.Length; i++)
            all[i] = SlotMeshes(d, i == 0 ? null : size, i);
        return all;
    }

    private static List<SkeletalMesh> SlotMeshes(RCharacterDefine d, string? size, int slotIndex)
    {
        RCharacterDefine.FMainMeshArray slot = d.CharacterMeshes[slotIndex];
        TArray<int> which = d.WhichMeshesToUse;
        int standard = slotIndex < which.Count ? which[slotIndex] : 0;

        var options = new List<SkeletalMesh>();
        for (int i = 0; i < slot.Meshes.Count; i++)
        {
            if (slot.Meshes[i] is not { IsValid: true } m)
                continue;
            if (i == standard)
                options.Insert(0, m);
            else
                options.Add(m);
        }

        if (size != null && options.Count > 1)
        {
            List<SkeletalMesh> fits = options.FindAll(m => SameSize(BodySize(m), size));
            if (fits.Count > 0)
                return fits;
        }

        return options;
    }

    /// <summary>A prop group's choices, standard first: off (null) or each option. Empty when it never goes on.</summary>
    private static List<RCharacterDefine.FMultiStaticProp?> StaticOptions(RCharacterDefine.FCharacterStaticPropsDef sp, List<string> meshNames)
    {
        var list = new List<RCharacterDefine.FMultiStaticProp?>();
        if (!Allowed(sp.FlagCheck, sp.MeshFlagMatch, meshNames))
            return list;

        foreach (RCharacterDefine.FMultiStaticProp option in sp.Props)
        {
            if (option.Prop is { IsValid: true })
                list.Add(option);
        }

        return WithOff(list, sp.PercentOfSpawnsThatHaveThisProp);
    }

    private static List<RCharacterDefine.FMultiSkeletonProp?> SkeletalOptions(RCharacterDefine.FCharacterSkeletalPropsDef sp, List<string> meshNames, string? size)
    {
        var list = new List<RCharacterDefine.FMultiSkeletonProp?>();
        if (!Allowed(sp.FlagCheck, sp.MeshFlagMatch, meshNames))
            return list;

        foreach (RCharacterDefine.FMultiSkeletonProp option in sp.Props)
        {
            if (option.Prop is not { IsValid: true } candidate)
                continue;
            if (sp.bIsThugMaker && size != null && !SameSize(BodySize(candidate), size))
                continue;
            list.Add(option);
        }

        return WithOff(list, sp.PercentOfSpawnsThatHaveThisProp);
    }

    /// <summary>
    /// Adds "off" to a prop's options unless every spawn has it: last when most spawns carry it (so the standard look
    /// wears option 0), first when most don't (so the standard look goes without).
    /// </summary>
    private static List<T?> WithOff<T>(List<T?> options, float percent) where T : struct
    {
        if (options.Count == 0 || percent >= 100f)
            return options;

        if (percent >= CommonProp)
            options.Add(null);
        else
            options.Insert(0, null);
        return options;
    }

    /// <summary>No story flag, and meant for the meshes picked.</summary>
    private static bool Allowed(FName flag, FString? meshMatch, List<string> meshNames)
    {
        if (flag.Index != FName.None.Index)
            return false;

        string match = meshMatch?.ToString() ?? "";
        return match.Length == 0 || meshNames.Exists(n => n.Contains(match, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Two thug-maker sizes match; a mesh with no size fits any body.</summary>
    private static bool SameSize(string? a, string b) => string.IsNullOrEmpty(a) || b.Length == 0 || a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private static MaterialInterface?[] Mats<T>(TArray<T> list) where T : MaterialInterface
    {
        int n = Math.Min(list.Count, MaxMaterials);
        var mats = new MaterialInterface?[n];
        for (int i = 0; i < n; i++)
            mats[i] = list[i] is { IsValid: true } m ? m : null;
        return mats;
    }

    private static MaterialInterface?[] Overrides(RCharacterDefine d, SkeletalMesh mesh)
    {
        foreach (RCharacterDefine.FPerMeshMatOverride o in d.PerMeshMaterialOverrides)
        {
            if (o.Mesh is { IsValid: true } m && m.Ptr == mesh.Ptr)
                return Mats(o.Materials);
        }

        return Array.Empty<MaterialInterface?>();
    }

    /// <summary>The thug-maker body size of a mesh, asked of any live character (the native needs one), or null.</summary>
    private static string? BodySize(SkeletalMesh mesh)
    {
        foreach (RBMPawnAI ai in World.All<RBMPawnAI>())
        {
            if (!ai.bDeleteMe)
                return ai.GetBodySize(mesh, false).ToString() ?? "";
        }

        return null;
    }

    // ---- copying

    /// <summary>
    /// Every visible piece of a character: the body, the pieces whose bones follow it (heads, hair, coats), and the
    /// props fixed to its bones (helmets, masks, pouches). Weapons belong to the weapon actor, not the character, so
    /// they stay behind.
    /// </summary>
    private static Look Copy(RPawn npc) => Copy(npc, npc.Mesh, Label(npc, npc.Mesh));

    private static Look Copy(Actor npc, SkeletalMeshComponent body, string name)
    {
        var pieces = new List<Piece> { Skin(body, FollowParentMode) };
        var taken = new HashSet<nint> { body.Ptr };

        // Pieces driven by the body's skeleton.
        foreach (SkeletalMeshComponent extra in Pieces(npc))
        {
            if (!taken.Add(extra.Ptr) || extra.HiddenGame || extra.SkeletalMesh is not { IsValid: true })
                continue;
            if (extra.ParentAnimComponent is not { IsValid: true } parent || parent.Ptr != body.Ptr)
                continue;

            // Every piece follows the player relative to its own rest pose, as each of Joker's pieces does. Keeping the
            // character's own mode (often a straight copy) put a cop's cap at Batman's head height (in game, 2026-11-06).
            pieces.Add(Skin(extra, FollowParentMode));
        }

        // Props fixed to a bone or socket of the body.
        var props = PropPtrs(npc);
        foreach (SkeletalMeshComponent.FAttachment a in body.Attachments)
        {
            if (a.Component is not PrimitiveComponent c || !c.IsValid || c.HiddenGame || !taken.Add(c.Ptr))
                continue;
            if (!props.Contains(c.Ptr) && c.Outer?.Ptr != npc.Ptr)
                continue;

            Piece? p = c switch
            {
                SkeletalMeshComponent sk when sk.SkeletalMesh is { IsValid: true } => Skin(sk, RSkeletalMeshComponent_Export.EParentAnimComponentMode.PACM_Original),
                StaticMeshComponent st when st.StaticMesh is { IsValid: true } => new Piece { Static = st.StaticMesh, Materials = Materials(st) },
                _ => null,
            };
            if (p == null)
                continue;

            p.FollowsBones = false;
            p.Bone = a.BoneName;
            p.Location = a.RelativeLocation;
            p.Rotation = a.RelativeRotation;
            p.Scale = a.RelativeScale;
            pieces.Add(p);
        }

        return new Look(name, pieces);
    }

    private static IEnumerable<SkeletalMeshComponent> Pieces(Actor owner)
    {
        if (owner is not RPawn npc)
        {
            // A placed character: its other skeletal components (head, hair) follow the body.
            foreach (ActorComponent? c in owner.Components)
            {
                if (c is SkeletalMeshComponent sk && sk.IsValid)
                    yield return sk;
            }

            yield break;
        }

        foreach (SkeletalMeshComponent? extra in npc.ExtraMeshes)
        {
            if (extra is { IsValid: true })
                yield return extra;
        }

        if (npc is RBMPawnAI ai)
        {
            foreach (RBMPawnAI.FCharacterSkeletalProps sp in ai.PropSkeletals)
            {
                if (!sp.bRemovedProp && sp.PropComp is { IsValid: true } comp)
                    yield return comp;
            }
        }
    }

    private static HashSet<nint> PropPtrs(Actor npc)
    {
        var set = new HashSet<nint>();
        if (npc is not RBMPawnAI ai)
            return set;

        foreach (RBMPawnAI.FCharacterStaticProps sp in ai.PropStatics)
        {
            if (sp.PropComp is { IsValid: true } c)
                set.Add(c.Ptr);
        }

        foreach (RBMPawnAI.FCharacterSkeletalProps sp in ai.PropSkeletals)
        {
            if (!sp.bRemovedProp && sp.PropComp is { IsValid: true } c)
                set.Add(c.Ptr);
        }

        return set;
    }

    private static Piece Skin(SkeletalMeshComponent src, RSkeletalMeshComponent_Export.EParentAnimComponentMode mode) => new()
    {
        Skeletal = src.SkeletalMesh,
        Physics = src.PhysicsAsset is { IsValid: true } pa ? pa : null,
        Materials = Materials(src),
        FollowsBones = true,
        Mode = mode,
    };

    private static MaterialInterface?[] Materials(MeshComponent src)
    {
        int n = Math.Clamp(src.GetNumElements(), 0, MaxMaterials);
        var mats = new MaterialInterface?[n];
        for (int i = 0; i < n; i++)
            mats[i] = src.GetMaterial(i) is { IsValid: true } m ? m : null;
        return mats;
    }

    private static string Label(RPawn npc, SkeletalMeshComponent body)
    {
        string name = body.SkeletalMesh?.Name.ToString() ?? "";
        if (name.Length == 0)
            name = npc.Class?.Name.ToString() ?? "CHARACTER";
        return name.Replace('_', ' ').ToUpperInvariant();
    }

    // ---- wearing

    private void Dress(RPawnPlayer player, Look look)
    {
        SkeletalMeshComponent own = player.Mesh;
        if (own is not { IsValid: true })
            throw new InvalidOperationException("your character has no body to dress");

        _wearer = player;
        _oldUpdateSkel = own.bUpdateSkelWhenNotRendered;
        own.bUpdateSkelWhenNotRendered = true;
        _capeWasHidden = player is RPawnPlayerBmBase { CapeComponent: { IsValid: true } cape } && cape.IsCapeHiddenGame;

        foreach (Piece p in look.Pieces)
        {
            if (p.FollowsBones)
            {
                if (MakeFollower(player, own, p) is not { } f)
                    continue;

                _followers.Add(f);
                _worn.Add(f.Ptr);
                _body ??= f;
            }
            else if (_body != null && MakeProp(player, _body, p) is { } prop)
            {
                _props.Add(prop);
                _worn.Add(prop.Ptr);
            }
        }

        if (_body != null)
            PickHead(own, _body);
        HideOwn(player);
    }

    /// <summary>A piece whose bones copy the player's, set up the way BecomeJoker sets up Joker's.</summary>
    private static SkeletalMeshComponent? MakeFollower(RPawnPlayer player, SkeletalMeshComponent own, Piece p)
    {
        if (p.Skeletal is not { IsValid: true } mesh)
            return null;

        var c = GameObject.ConstructObject<SkeletalMeshComponent>(player);
        c.SetActorCollision(false, false);
        c.SetSkeletalMesh(mesh, false);
        if (p.Physics != null && p.Physics.IsValid)
            c.SetPhysicsAsset(p.Physics, false);
        c.SetTickGroup((GameObject.ETickingGroup)2);
        Paint(c, p);
        // No Translation/Rotation/Scale copied from the player's mesh: BecomeJoker leaves them at their defaults, and
        // copying the player's floor offset sank the worn body by that much (in game, 2026-11-06).
        c.ParentAnimComponentMode = p.Mode;
        c.SetParentAnimComponent(own);
        c.SetHasPhysicsAssetInstance(own.bHasPhysicsAssetInstance, false);
        c.SetAllowRFlapsAssetInstance(own.bAllowRFlapsAssetInstance);
        c.bCastDynamicShadow = own.bCastDynamicShadow;
        c.SetLightingChannels(own.LightingChannels);
        // Characters draw in their own depth group (the player's mesh is SDPG_RockBlimp1, and the game copies the
        // owner mesh's group onto every prop and gadget it attaches). Left in SDPG_World, cinematic subsurface skin
        // (Stagg's head and arms) rendered shiny black (in game, 2026-11-06).
        c.SetDepthPriorityGroup(own.DepthPriorityGroup);
        c.SetOwnerNoSee(own.bOwnerNoSee);
        player.AttachComponent(c);
        return c;
    }

    /// <summary>A prop fixed to a bone of the worn body, where it sat on the character.</summary>
    private static PrimitiveComponent? MakeProp(RPawnPlayer player, SkeletalMeshComponent body, Piece p)
    {
        MeshComponent c;
        if (p.Static is { IsValid: true } sm)
        {
            var st = GameObject.ConstructObject<StaticMeshComponent>(player);
            st.SetStaticMesh(sm, false);
            c = st;
        }
        else if (p.Skeletal is { IsValid: true } skm)
        {
            var sk = GameObject.ConstructObject<SkeletalMeshComponent>(player);
            sk.SetSkeletalMesh(skm, false);
            if (p.Physics != null && p.Physics.IsValid)
                sk.SetPhysicsAsset(p.Physics, false);
            c = sk;
        }
        else
        {
            return null;
        }

        c.SetActorCollision(false, false);
        Paint(c, p);
        c.bCastDynamicShadow = body.bCastDynamicShadow;
        c.SetLightingChannels(body.LightingChannels);
        c.SetDepthPriorityGroup(body.DepthPriorityGroup);
        c.SetOwnerNoSee(body.bOwnerNoSee);
        if (p.Socket.Index != FName.None.Index && body.GetSocketByName(p.Socket) is { IsValid: true })
            body.AttachComponentToSocket(c, p.Socket);
        else
            body.AttachComponent(c, p.Bone, p.Location, p.Rotation, p.Scale);
        return c;
    }

    private static void Paint(MeshComponent c, Piece p)
    {
        int n = Math.Min(p.Materials.Length, c.GetNumElements());
        for (int i = 0; i < n; i++)
        {
            if (p.Materials[i] is { IsValid: true } m)
                c.SetMaterial(i, m);
        }
    }

    /// <summary>
    /// Hides the player's own look: body, extra pieces, cape, and anything else of theirs on the body. Held gadgets
    /// belong to the gadget, not the player, so they stay. Run again every tick, because the game re-meshes the
    /// player now and then (suits, battle damage) and shows pieces again.
    /// </summary>
    private void HideOwn(RPawnPlayer player)
    {
        SkeletalMeshComponent own = player.Mesh;
        Hide(own);

        foreach (SkeletalMeshComponent? extra in player.ExtraMeshes)
        {
            if (extra is { IsValid: true } && !IsWorn(extra))
                Hide(extra);
        }

        foreach (SkeletalMeshComponent.FAttachment a in own.Attachments)
        {
            if (a.Component is PrimitiveComponent c && c.IsValid && !IsWorn(c) && c.Outer?.Ptr == player.Ptr)
                Hide(c);
        }

        if (player is RPawnPlayerBmBase bm)
        {
            if (bm.CapeSkeletalMesh is { IsValid: true } capeMesh && !IsWorn(capeMesh))
                Hide(capeMesh);

            if (bm.CapeComponent is { IsValid: true } cape && !cape.IsCapeHiddenGame)
                cape.CapeSetHiddenGame(true);
        }
    }

    private void Hide(PrimitiveComponent c)
    {
        if (c.HiddenGame)
            return;

        c.SetHidden(true);
        _hidden[c.Ptr] = c;
    }

    private bool IsWorn(PrimitiveComponent c) => _worn.Contains(c.Ptr);

    /// <summary>Keeps the look on: own pieces hidden, first-person hiding mirrored, bones still following.</summary>
    private void Keep(RPawnPlayer player)
    {
        SkeletalMeshComponent own = player.Mesh;
        if (own is not { IsValid: true })
            return;

        HideOwn(player);
        if (!own.bUpdateSkelWhenNotRendered)
            own.bUpdateSkelWhenNotRendered = true;

        foreach (SkeletalMeshComponent c in _followers)
        {
            if (!c.IsValid)
                continue;

            if (c.bOwnerNoSee != own.bOwnerNoSee)
                c.SetOwnerNoSee(own.bOwnerNoSee);
            if (c.DepthPriorityGroup != own.DepthPriorityGroup)
                c.SetDepthPriorityGroup(own.DepthPriorityGroup);
            if (c.ParentAnimComponent is not { IsValid: true } parent || parent.Ptr != own.Ptr)
                c.SetParentAnimComponent(own);
        }

        foreach (PrimitiveComponent c in _props)
        {
            if (!c.IsValid)
                continue;

            if (c.bOwnerNoSee != own.bOwnerNoSee)
                c.SetOwnerNoSee(own.bOwnerNoSee);
            if (c.DepthPriorityGroup != own.DepthPriorityGroup)
                c.SetDepthPriorityGroup(own.DepthPriorityGroup);
        }

        if (_fitGadgets.On && _body is { IsValid: true } body)
            FitAttachments(own, body);
        else
            RestoreFits(own);
    }

    // ---- fitting gadgets and the like

    private const float FitReach = 300f;

    /// <summary>
    /// Everything the game attaches to the player's mesh (gadgets in hand or on the belt, the gauntlet's call
    /// hologram) is placed on Batman's hidden skeleton. This rewrites each attachment's offset every tick so it
    /// lands where the same offset would put it on the worn body's bone. It stays attached to the player's mesh,
    /// so the game's own detach and re-attach keep working. A bone the worn skeleton lacks (Batman-only point
    /// bones) is carried by its nearest ancestor that both share.
    /// </summary>
    private void FitAttachments(SkeletalMeshComponent own, SkeletalMeshComponent body)
    {
        foreach (Fit f in _fits.Values)
            f.Seen = false;

        TArray<SkeletalMeshComponent.FAttachment> list = own.Attachments;
        for (int i = 0; i < list.Count; i++)
        {
            SkeletalMeshComponent.FAttachment a = list[i];
            if (a.Component is not PrimitiveComponent c || !c.IsValid || _worn.Contains(c.Ptr))
                continue;

            bool known = _fits.TryGetValue(c.Ptr, out Fit? f) && f.Bone == a.BoneName.Index
                && Near(a.RelativeLocation, f.LastLoc) && Near(a.RelativeRotation, f.LastRot);
            if (!known || f == null)
            {
                // New, or the game attached it again: what it holds now is the game's wish.
                f = new Fit
                {
                    Bone = a.BoneName.Index,
                    Anchor = AnchorFor(own, body, a.BoneName),
                    OrigLoc = a.RelativeLocation,
                    OrigRot = a.RelativeRotation,
                    LastLoc = a.RelativeLocation,
                    LastRot = a.RelativeRotation,
                };
                _fits[c.Ptr] = f;
            }

            f.Seen = true;
            Vector3 loc = f.OrigLoc;
            Rotator rot = f.OrigRot;
            if (!c.HiddenGame && f.Anchor.Index != FName.None.Index && Moved(own, body, a.BoneName, f, out Vector3 nl, out Rotator nr))
                (loc, rot) = (nl, nr);

            if (Near(loc, a.RelativeLocation) && Near(rot, a.RelativeRotation))
            {
                (f.LastLoc, f.LastRot) = (a.RelativeLocation, a.RelativeRotation);
                continue;
            }

            a.RelativeLocation = loc;
            a.RelativeRotation = rot;
            list[i] = a;
            // Read back, so rotation rounding to the engine's integer units still matches next tick.
            SkeletalMeshComponent.FAttachment back = list[i];
            (f.LastLoc, f.LastRot) = (back.RelativeLocation, back.RelativeRotation);
        }

        if (_fits.Count > 0)
        {
            foreach (nint gone in _fits.Where(kv => !kv.Value.Seen).Select(kv => kv.Key).ToList())
                _fits.Remove(gone);
        }
    }

    /// <summary>The game's offset on the player's bone, carried over to the same place on the worn body.</summary>
    private static bool Moved(SkeletalMeshComponent own, SkeletalMeshComponent body, FName bone, Fit f, out Vector3 loc, out Rotator rot)
    {
        // Where the game wants it in the world, on Batman's skeleton.
        own.TransformFromBoneSpace(bone, f.OrigLoc, f.OrigRot, out Vector3 p0, out Rotator r0);
        // Relative to the anchor bone both skeletons have, then put on the worn body's anchor.
        own.TransformToBoneSpace(f.Anchor, p0, r0, out Vector3 la, out Rotator ra);
        body.TransformFromBoneSpace(f.Anchor, la, ra, out Vector3 p1, out Rotator r1);
        // Back into the player's bone space, which is what the attachment stores.
        own.TransformToBoneSpace(bone, p1, r1, out loc, out rot);

        return Finite(loc) && Finite(p0) && Finite(p1) && Vector3.Distance(p0, p1) < FitReach;
    }

    private static FName AnchorFor(SkeletalMeshComponent own, SkeletalMeshComponent body, FName bone)
    {
        FName b = bone;
        for (int step = 0; step < 64 && b.Index != FName.None.Index; step++)
        {
            if (body.MatchRefBone(b) >= 0)
                return b;
            b = own.GetParentBone(b);
        }

        return FName.None;
    }

    /// <summary>Puts back every offset Oracle changed and the game hasn't replaced since.</summary>
    private void RestoreFits(SkeletalMeshComponent own)
    {
        if (_fits.Count == 0)
            return;

        if (own.IsValid)
        {
            TArray<SkeletalMeshComponent.FAttachment> list = own.Attachments;
            for (int i = 0; i < list.Count; i++)
            {
                SkeletalMeshComponent.FAttachment a = list[i];
                if (a.Component is not { IsValid: true } c || !_fits.TryGetValue(c.Ptr, out Fit? f) || f.Bone != a.BoneName.Index
                    || !Near(a.RelativeLocation, f.LastLoc) || !Near(a.RelativeRotation, f.LastRot))
                    continue;

                a.RelativeLocation = f.OrigLoc;
                a.RelativeRotation = f.OrigRot;
                list[i] = a;
            }
        }

        _fits.Clear();
    }

    private static bool Near(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b) < 0.0001f;

    private static bool Near(Rotator a, Rotator b) => Deg(a.Pitch - b.Pitch) < 0.05f && Deg(a.Yaw - b.Yaw) < 0.05f && Deg(a.Roll - b.Roll) < 0.05f;

    private static float Deg(float d)
    {
        d %= 360f;
        if (d < 0f)
            d += 360f;
        return MathF.Min(d, 360f - d);
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    // ---- fitting the camera

    private static readonly string[] HeadBones = { "Bip01_Head", "Head", "head", "Bip01_Neck", "Neck" };

    private void PickHead(SkeletalMeshComponent own, SkeletalMeshComponent body)
    {
        _head = FName.None;
        foreach (string name in HeadBones)
        {
            var n = new FName(name);
            if (own.MatchRefBone(n) >= 0 && body.MatchRefBone(n) >= 0)
            {
                _head = n;
                return;
            }
        }

        Log.Info("disguise: no head bone shared with " + (body.SkeletalMesh?.Name.ToString() ?? "the worn body") + ", so FIT CAMERA can't follow it.");
    }

    /// <summary>
    /// For the camera: Batman's head (where the game aims its camera), how far the worn head sits from it, and
    /// how tall the worn body stands against Batman's, both measured from the feet this frame.
    /// </summary>
    internal bool CameraFit(out Vector3 pivot, out Vector3 shift, out float ratio)
    {
        pivot = shift = Vector3.Zero;
        ratio = 1f;
        if (!_fitCamera.On || _look == null || _head.Index == FName.None.Index || Characters.I.Busy)
            return false;
        if (_wearer is not { IsValid: true, bDeleteMe: false } wearer || World.Pawn?.Ptr != wearer.Ptr)
            return false;
        if (wearer.Mesh is not { IsValid: true } own || _body is not { IsValid: true } body)
            return false;

        Vector3 oh = own.GetBoneLocation(_head, 0);
        Vector3 wh = body.GetBoneLocation(_head, 0);
        if (!Finite(oh) || !Finite(wh) || oh == Vector3.Zero || wh == Vector3.Zero)
            return false;

        float feet = wearer.Location.Z - (wearer.CylinderComponent is { IsValid: true } cyl ? cyl.CollisionHeight : 90f);
        float ownTall = oh.Z - feet, wornTall = wh.Z - feet;
        if (ownTall < 20f || wornTall < 5f)
            return false;

        pivot = oh;
        shift = wh - oh;
        ratio = Math.Clamp(wornTall / ownTall, 0.3f, 2.5f);
        return Vector3.Distance(oh, wh) < FitReach;
    }

    // ---- taking off

    private void TakeOff()
    {
        Strip();
        _look = null;
        _on.On = false;
    }

    /// <summary>Removes the worn pieces and shows exactly what was hidden.</summary>
    private void Strip()
    {
        RPawnPlayer? wearer = _wearer is { IsValid: true, bDeleteMe: false } w ? w : null;
        if (wearer != null)
        {
            SkeletalMeshComponent? body = _body is { IsValid: true } b ? b : null;
            foreach (PrimitiveComponent c in _props)
            {
                if (c.IsValid && body != null)
                    Safe.Run("disguise prop", () => body.DetachComponent(c));
            }

            foreach (SkeletalMeshComponent c in _followers)
            {
                if (c.IsValid)
                    Safe.Run("disguise piece", () => wearer.DetachComponent(c));
            }

            foreach (PrimitiveComponent c in _hidden.Values)
            {
                if (c.IsValid && c.HiddenGame)
                    c.SetHidden(false);
            }

            if (wearer.Mesh is { IsValid: true } own)
            {
                Safe.Run("disguise fit", () => RestoreFits(own));
                own.bUpdateSkelWhenNotRendered = _oldUpdateSkel;
            }

            if (wearer is RPawnPlayerBmBase { CapeComponent: { IsValid: true } cape } && cape.IsCapeHiddenGame != _capeWasHidden)
                cape.CapeSetHiddenGame(_capeWasHidden);
        }

        Forget();
    }

    private void Forget()
    {
        _followers.Clear();
        _props.Clear();
        _worn.Clear();
        _hidden.Clear();
        _fits.Clear();
        _head = FName.None;
        _body = null;
        _wearer = null;
    }
}
