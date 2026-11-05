using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// Plays the player's own songs through Windows MCI, on a worker thread because MCI calls can stall, and lets
/// the player browse and play the game's own sound events through its audio engine.
/// </summary>
internal sealed class Music : Module
{
    public static readonly Music I = new();

    private const string Folder = "Music";
    private const string Alias = "oraclemusic";
    private const string SoundsFile = "sounds.txt";
    private const string StopKey = "MusicStop";
    private const int Shuffle = 1;
    private const int RepeatOne = 2;
    private const int MaxLabel = 30;

    private static readonly string[] Extensions = { ".mp3", ".wav", ".wma" };

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder? returnValue, int returnLength, nint hwndCallback);

    private sealed class Worker
    {
        private readonly BlockingCollection<Action> _jobs = new();
        private volatile bool _open;
        public volatile bool Finished;
        public volatile string Error = "";

        public Worker()
        {
            var thread = new Thread(Loop) { IsBackground = true, Name = "Oracle music" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public void Post(Action job)
        {
            if (!_jobs.IsAddingCompleted)
                _jobs.Add(job);
        }

        /// <summary>Ends the thread. A running thread would keep the old assembly alive after a hot reload.</summary>
        public void Stop() => _jobs.CompleteAdding();

        private static string Send(string cmd)
        {
            var sb = new StringBuilder(128);
            int err = mciSendString(cmd, sb, sb.Capacity, 0);
            return err == 0 ? sb.ToString() : "!" + err;
        }

        public void Open(string file, int volume)
        {
            Close();
            string type = file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? "waveaudio" : "mpegvideo";
            string r = Send("open \"" + file + "\" type " + type + " alias " + Alias);
            if (r.StartsWith('!') && type == "waveaudio")
                r = Send("open \"" + file + "\" type mpegvideo alias " + Alias);
            if (r.StartsWith('!'))
            {
                Error = "could not open " + Path.GetFileName(file) + " (MCI error " + r.Substring(1) + ")";
                return;
            }

            _open = true;
            Finished = false;
            SetVolume(volume);
            Send("play " + Alias);
        }

        public void SetVolume(int volume)
        {
            if (_open)
                Send("setaudio " + Alias + " volume to " + Math.Clamp(volume, 0, 1000));
        }

        public void Pause()
        {
            if (_open)
                Send("pause " + Alias);
        }

        public void Resume()
        {
            if (_open)
                Send("resume " + Alias);
        }

        public void Close()
        {
            if (!_open)
                return;

            Send("stop " + Alias);
            Send("close " + Alias);
            _open = false;
        }


        private void Loop()
        {
            while (!_jobs.IsCompleted)
            {
                if (_jobs.TryTake(out Action? job, 250))
                {
                    try
                    {
                        job();
                    }
                    catch (Exception ex)
                    {
                        Error = ex.Message;
                    }
                }

                if (_open && Send("status " + Alias + " mode") == "stopped")
                {
                    Close();
                    Finished = true;
                }
            }

            Close();
        }
    }

    /// <summary>
    /// The game's soundtrack is not a set of sound events but the states of one Wwise state group,
    /// Ww_MUSIC_GameState (explore, combat, chase, predator, puzzle...), which its sound director switches
    /// between. Other state groups shape the mix (rooms, districts, fear gas). Sound events live in packages
    /// named Ww_&lt;category&gt;_... The browser shows all of it, by category.
    /// </summary>
    private static readonly (string Key, string Label)[] Categories =
    {
        ("music", "MUSIC"),
        ("mix", "MIX STATES"),
        ("MUS", "MUSIC CUES"),
        ("PSO", "WORLD OBJECTS"),
        ("AMB", "AMBIENCE"),
        ("WEA", "WEATHER"),
        ("CHR", "CHARACTER"),
        ("NPC", "ENEMIES"),
        ("DLG", "VOICES"),
        ("BML", "BATMOBILE"),
        ("VEH", "VEHICLES"),
        ("WPN", "WEAPONS"),
        ("OBJ", "OBJECTS"),
        ("DVC", "DEVICES"),
        ("VFX", "EFFECTS"),
        ("UI", "INTERFACE"),
        ("SBJ", "SUBJECTIVE"),
        ("SEQ", "SEQUENCES"),
        ("other", "OTHER EVENTS"),
        ("all", "EVERY EVENT"),
    };

    private const string NoSound = "none";
    private const string DefaultTeleportSound = "Ww_PSO_PenguinVan_Music.Play_Penguinn_Car_V3";
    private const string StatePrefix = "state:";
    private const string MusicGroup = "MUSIC";
    private const string MusicStatePrefix = "MUSIC_";

    /// <summary>Words that mark an event as placed music (the Penguin van jingle, lifts, jukeboxes), whatever its package.</summary>
    private static readonly string[] MusicWords = { "MUSIC", "_MUS", "_MX", "MUZAK", "JINGLE", "JUKEBOX", "SONG", "PIANO", "GRAMOPHONE", "RECORD_PLAYER", "ELEVATOR", "LIFT_MUS" };

    private readonly List<string> _tracks = new();
    private readonly List<string> _sounds = new();
    private Choice _soundFilter = null!;
    private Choice _sound = null!;
    private Choice _transitionSound = null!;
    private string _playingSound = "";
    private string _transitionPlaying = "";
    private nint _attachedTo;
    private (int Music, int Dialogue)? _volumes;
    private readonly List<AkParameterName> _quietened = new();

    /// <summary>Mixer parameters that take the world down under the teleport sound: the weather, combat and effects buses, and a muffle on the ambience beds.</summary>
    private static readonly (string Name, float Value)[] QuietParameters =
    {
        ("BUS_SFX_Weather", -1f),
        ("BUS_SFX_Combat", -1f),
        ("BUS_SFX_VFX", -1f),
        ("Ambience_LowPass", 1f),
    };
    private readonly Random _random = new();
    private Worker? _worker;
    private int _track = -1;
    private bool _playing;
    private bool _pausedByGame;
    private Choice _pick = null!;
    private Slider _volume = null!;
    private Choice _order = null!;
    private Toggle _followPause = null!;

    private string Dir => Path.Combine(App.Dir, Folder);


    private int Volume => (int)MathF.Round(_volume.Value * 1000f);

    public override void Build()
    {
        _pick = AddChoice("music_track", "TRACK", "Songs in BmGame\\Mods\\Oracle\\Music (mp3, wav or wma). Pick one to play it.", new[] { "none" }, new[] { "NO SONGS FOUND" }, PlayPicked).Pref();
        AddCommand("music_play", "PLAY / PAUSE", "Starts the music, or pauses and resumes it.", PlayPause);
        AddCommand("music_next", "NEXT TRACK", "Skips to the next song.", () => Step(1));
        AddCommand("music_prev", "PREVIOUS TRACK", "Goes back to the previous song.", () => Step(-1));
        AddCommand("music_stop", "STOP", "Stops the music.", Stop);
        _volume = AddSlider("music_volume", "VOLUME", "Volume of your music. The game's own music volume is in the game options.", 0.7f, 0f, 1f, 0.05f, 0.35f, Fmt.Percent,
            _ => _worker?.Post(() => _worker.SetVolume(Volume))).Pref();
        _order = AddChoice("music_order", "ORDER", "IN ORDER plays the folder top to bottom, SHUFFLE picks at random, REPEAT ONE loops the current song.",
            new[] { "in_order", "shuffle", "repeat_one" }, new[] { "IN ORDER", "SHUFFLE", "REPEAT ONE" }).Pref();
        _followPause = AddToggle("music_pause_with_game", "PAUSE WITH GAME", "Pauses your music while the game's pause menu is open.", on: true).Pref();
        AddCommand("music_rescan", "RELOAD SONG LIST", "Reads the Music folder again after you add or remove songs.", () =>
        {
            Scan();
            Overlay.Notice("MUSIC", Fmt.Plural(_tracks.Count, "SONG"), _tracks.Count > 0);
        });
        AddCommand("music_folder", "OPEN MUSIC FOLDER", "Opens the Music folder in Windows Explorer so you can drop songs in.", () =>
        {
            Directory.CreateDirectory(Dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + Dir + "\"") { UseShellExecute = true });
        });

        AddCommand("sound_refresh", "REFRESH SOUND LIST", "Lists every sound event the game has loaded right now: music, ambience, voices and effects. What's loaded changes with where you are, so refresh after moving somewhere new.", RefreshSounds).Setting();
        _soundFilter = AddChoice("sound_filter", "SHOW", "Which sounds to list. MUSIC is the soundtrack: the moods the game's sound director switches between (explore, combat, chase, predator, puzzle...). MIX STATES shape the sound of rooms and districts. The rest are the game's sound events by category.",
            Categories.Select(f => f.Key).ToArray(), Categories.Select(f => f.Label).ToArray(), _ => ListSounds()).Setting();
        _sound = AddChoice("sound_event", "SOUND", "Hold left or right to scroll. PLAY SOUND starts the selected one.", new[] { "" }, new[] { "PRESS REFRESH SOUND LIST" }).Pref();
        _sound.HoldToScroll = true;
        AddCommand("sound_play", "PLAY SOUND", "Plays the selected sound through the game's own audio engine. A music state takes over the soundtrack until STOP SOUND; loops keep going until then; a one-off sound plays once.", PlaySound);
        AddCommand("sound_stop", "STOP SOUND", "Stops the sound started with PLAY SOUND, or hands the soundtrack back to the game.", StopSound);
        AddCommand("sound_use_teleport", "USE FOR TELEPORTS", "Plays the selected sound behind the black screen of every long teleport, and stops it as the picture returns.", UseForTeleports);
        _transitionSound = AddChoice("sound_teleport", "TELEPORT SOUND", "The game sound that plays during a teleport's black screen: the Penguin van's jingle unless you pick another with USE FOR TELEPORTS, or NONE for silence. A sound only plays where the game has it loaded.",
            new[] { DefaultTeleportSound, NoSound }, new[] { SoundLabel(DefaultTeleportSound), "NONE" }).Pref();
        AddCommand("sound_dump", "SAVE SOUND LIST TO FILE", "Writes every loaded sound event to sounds.txt in the Oracle folder.", DumpSounds).Setting();
    }

    // ---- the game's own sounds

    private static string ShortName(string path)
    {
        int dot = path.LastIndexOf('.');
        return (dot >= 0 ? path[(dot + 1)..] : path).Replace('_', ' ').ToUpperInvariant();
    }

    private static AkEvent? FindSound(string path)
    {
        if (path.Length == 0 || path == NoSound || path.StartsWith(StatePrefix, StringComparison.Ordinal))
            return null;

        foreach (AkEvent e in World.All<AkEvent>())
        {
            if (e.IsValid && e.GetPathName(null!).Equals(path, StringComparison.OrdinalIgnoreCase))
                return e;
        }

        return null;
    }

    private static AkStateName? FindState(string key)
    {
        if (!key.StartsWith(StatePrefix, StringComparison.Ordinal))
            return null;

        string path = key[StatePrefix.Length..];
        foreach (AkStateName s in World.All<AkStateName>())
        {
            if (s.IsValid && s.GetPathName(null!).Equals(path, StringComparison.OrdinalIgnoreCase))
                return s;
        }

        return null;
    }

    /// <summary>A music state shows by its own name, a mix state as GROUP / STATE, an event by its name.</summary>
    private static string SoundLabel(string key)
    {
        if (!key.StartsWith(StatePrefix, StringComparison.Ordinal))
            return ShortName(key);

        string path = key[StatePrefix.Length..];
        string state = ShortName(path);
        if (state.StartsWith(MusicStatePrefix, StringComparison.OrdinalIgnoreCase))
            return state[MusicStatePrefix.Length..].TrimStart();

        int dot = path.LastIndexOf('.');
        string group = dot >= 0 ? path[..dot] : "";
        int gdot = group.LastIndexOf('.');
        if (gdot >= 0)
            group = group[(gdot + 1)..];
        if (group.StartsWith("Ww_", StringComparison.OrdinalIgnoreCase))
            group = group[3..];
        return (group.Length > 0 ? group.Replace('_', ' ').ToUpperInvariant() + " / " : "") + state;
    }

    /// <summary>True for a state of the soundtrack's group rather than a mix state.</summary>
    private static bool IsMusicState(AkStateName state)
    {
        try
        {
            if (state.ParentStateGroup is { IsValid: true } group && group.GetPathName(null!).Contains(MusicGroup, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch
        {
            // A state with no readable group is judged by its name below.
        }

        return state.GetPathName(null!).Contains(MusicGroup, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The Ww_&lt;category&gt;_ token of an event's package, upper-cased, or "other".</summary>
    private static string CategoryOf(string path)
    {
        string upper = path.ToUpperInvariant();
        if (MusicWords.Any(w => upper.Contains(w, StringComparison.Ordinal)))
            return "MUS";
        if (!upper.StartsWith("WW_", StringComparison.Ordinal))
            return "other";

        int end = upper.IndexOf('_', 3);
        if (end < 0)
            return "other";

        string token = upper[3..end];
        return Categories.Any(c => c.Key == token) ? token : "other";
    }

    /// <summary>
    /// Starts a sound or music state by its key, for the browser and the teleport screen. A sound event is
    /// attached to the player, the way the game attaches gadget and vehicle sounds: many events are positional
    /// with a short range (the Penguin van's jingle), and on a global source they played in silence.
    /// </summary>
    private bool Start(string key, AkWwise.EGlobalAudioSourceID source)
    {
        if (FindState(key) is { } state)
        {
            AkWwise.SetGlobalAudioState(state);
            return true;
        }

        if (FindSound(key) is not { } sound)
            return false;

        if (World.Pawn is { IsValid: true } pawn)
        {
            pawn.AddLoopingAudioEvent(sound);
            _attachedTo = pawn.Ptr;
            return true;
        }

        _attachedTo = 0;
        return AkWwise.AddGlobalLoopingEvent(sound, source);
    }

    private void Finish(string key)
    {
        if (FindState(key) is { } state)
        {
            AkWwise.ResetGlobalAudioState(state);
            return;
        }

        if (FindSound(key) is not { } sound)
            return;

        if (_attachedTo != 0 && World.Pawn is { IsValid: true } pawn && pawn.Ptr == _attachedTo)
            pawn.RemoveLoopingAudioEvent(sound);
        else
            AkWwise.RemoveGlobalLoopingEvent(sound);
        _attachedTo = 0;
    }

    private void RefreshSounds()
    {
        ListSounds();
        Overlay.Notice("SOUNDS", Fmt.Plural(_sounds.Count, "SOUND") + " LISTED", _sounds.Count > 0);
    }

    private void ListSounds()
    {
        _sounds.Clear();
        string category = Categories[Math.Clamp(_soundFilter.Index, 0, Categories.Length - 1)].Key;
        var all = new List<string>();
        foreach (AkStateName s in World.All<AkStateName>())
        {
            if (!s.IsValid)
                continue;

            string key = StatePrefix + s.GetPathName(null!);
            all.Add(key);
            bool music = IsMusicState(s);
            if ((category == "music" && music) || (category == "mix" && !music))
                _sounds.Add(key);
        }

        foreach (AkEvent e in World.All<AkEvent>())
        {
            if (!e.IsValid)
                continue;

            string path = e.GetPathName(null!);
            all.Add(path);
            if (category == "all" || CategoryOf(path) == category)
                _sounds.Add(path);
        }

        _sounds.Sort(StringComparer.OrdinalIgnoreCase);
        if (_sounds.Count == 0)
            _sound.SetOptions(new[] { "" }, new[] { "NONE LOADED" });
        else
            _sound.SetOptions(_sounds.ToArray(), _sounds.Select(SoundLabel).ToArray());

        ShowTransitionChoice(all);
    }

    /// <summary>
    /// The TELEPORT SOUND row: the default jingle first (so it counts as the default), NONE, the saved choice even
    /// when it isn't loaded right now, and every loaded event.
    /// </summary>
    private void ShowTransitionChoice(List<string> loaded)
    {
        string saved = TransitionKey();
        var keys = new List<string> { DefaultTeleportSound, NoSound };
        if (saved != NoSound && !keys.Contains(saved, StringComparer.OrdinalIgnoreCase))
            keys.Add(saved);
        foreach (string path in loaded.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (!keys.Contains(path, StringComparer.OrdinalIgnoreCase))
                keys.Add(path);
        }

        _transitionSound.SetOptions(keys.ToArray(), keys.Select(k => k == NoSound ? "NONE" : SoundLabel(k)).ToArray());
    }

    private string TransitionKey()
    {
        string key = _transitionSound.Saved.Length > 0 ? _transitionSound.Saved : _transitionSound.CurrentKey;
        return key.Length == 0 ? NoSound : key;
    }

    private void PlaySound()
    {
        string key = _sound.CurrentKey;
        if (key.Length == 0)
            throw new InvalidOperationException("pick a sound first");

        StopSound();
        if (!Start(key, AkWwise.EGlobalAudioSourceID.AK_GLOBAL_SOURCE_ID))
            throw new InvalidOperationException("that sound isn't loaded any more, refresh the list");

        _playingSound = key;
        Overlay.Notice("PLAYING", SoundLabel(key), true);
        Log.Info("Sound: playing " + key + ".");
    }

    private void StopSound()
    {
        if (_playingSound.Length == 0)
            return;

        Finish(_playingSound);
        _playingSound = "";
    }

    private void UseForTeleports()
    {
        string key = _sound.CurrentKey;
        if (key.Length == 0)
            throw new InvalidOperationException("pick a sound first");

        _transitionSound.Saved = key;
        ShowTransitionChoice(_sounds);
        Overlay.Notice("TELEPORT SOUND", SoundLabel(key), true);
    }

    /// <summary>
    /// Starts the chosen game sound or music state for a teleport's black screen. Returns true when it started;
    /// nothing plays when none is chosen or it isn't loaded here.
    /// </summary>
    public bool BeginTransition()
    {
        string key = TransitionKey();
        if (key == NoSound)
            return false;
        if (_transitionPlaying.Length > 0)
            return true;

        StopSound();
        if (Start(key, AkWwise.EGlobalAudioSourceID.AK_MUSIC_SOURCE_ID))
        {
            _transitionPlaying = key;
            Safe.Run("game volumes", MuteGame);
            Log.Info("Sound: teleport sound " + key + " started.");
            return true;
        }

        Log.Info("Sound: the teleport sound " + key + " isn't loaded here.");
        return false;
    }

    /// <summary>Stops the teleport sound, or hands the soundtrack back, as the picture comes back.</summary>
    public void EndTransition()
    {
        Safe.Run("game volumes", RestoreGame);
        if (_transitionPlaying.Length == 0)
            return;

        Finish(_transitionPlaying);
        _transitionPlaying = "";
    }

    /// <summary>
    /// Quietens the game under the teleport sound: the music and dialogue channels go to nothing, the way the
    /// options menu sets them, which takes the score with them. The effects channel stays up, because placed
    /// music like the Penguin van's jingle plays through it (and the director ignores a MUSIC_Silence state set
    /// from outside). Everything comes back exactly as it was in <see cref="RestoreGame"/>.
    /// </summary>
    private void MuteGame()
    {
        if (_volumes != null || World.Ri?.PersistentOptions is not { IsValid: true } options)
            return;

        _volumes = (options.GetVolumeMusic(), options.GetVolumeDialogue());
        options.SetVolumeMusic(0f, false);
        options.SetVolumeDialogue(0f, false);

        _quietened.Clear();
        foreach (AkParameterName prm in World.All<AkParameterName>())
        {
            if (!prm.IsValid)
                continue;

            string path = prm.GetPathName(null!);
            foreach ((string name, float value) in QuietParameters)
            {
                if (path.EndsWith("." + name, StringComparison.OrdinalIgnoreCase))
                {
                    AkWwise.SetGlobalAudioParameter(prm, value);
                    _quietened.Add(prm);
                }
            }
        }
    }

    private void RestoreGame()
    {
        if (_volumes is not { } v)
            return;

        _volumes = null;
        foreach (AkParameterName prm in _quietened)
        {
            if (prm.IsValid)
                AkWwise.ResetGlobalAudioParameter(prm);
        }

        _quietened.Clear();
        if (World.Ri?.PersistentOptions is not { IsValid: true } options)
            return;

        options.SetVolumeMusic(v.Music, false);
        options.SetVolumeDialogue(v.Dialogue, false);
    }

    private void DumpSounds()
    {
        var lines = new List<string> { "# Oracle sound list, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"), "", "[states] path  group  (music or mix)" };
        foreach (AkStateName s in World.All<AkStateName>().Where(s => s.IsValid).OrderBy(s => s.GetPathName(null!), StringComparer.OrdinalIgnoreCase))
        {
            string group = s.ParentStateGroup is { IsValid: true } g ? g.GetPathName(null!) : "?";
            lines.Add(s.GetPathName(null!) + "  " + group + "  " + (IsMusicState(s) ? "music" : "mix"));
        }
        lines.Add("");
        lines.Add("[parameters] path  min  max  default");
        foreach (AkParameterName prm in World.All<AkParameterName>().Where(x => x.IsValid).OrderBy(x => x.GetPathName(null!), StringComparer.OrdinalIgnoreCase))
            lines.Add(prm.GetPathName(null!) + "  " + prm.MinValue + "  " + prm.MaxValue + "  " + prm.DefaultValue);
        lines.Add("");
        lines.Add("[switches] path  group");
        foreach (AkSwitchName sw in World.All<AkSwitchName>().Where(x => x.IsValid).OrderBy(x => x.GetPathName(null!), StringComparer.OrdinalIgnoreCase))
            lines.Add(sw.GetPathName(null!) + "  " + (sw.ParentSwitchGroup is { IsValid: true } g ? g.GetPathName(null!) : "?"));
        lines.Add("");
        lines.Add("[sound events] path  kind");
        int count = 0;
        foreach (AkEvent e in World.All<AkEvent>().Where(e => e.IsValid).OrderBy(e => e.GetPathName(null!), StringComparer.OrdinalIgnoreCase))
        {
            lines.Add(e.GetPathName(null!) + "  " + e.DurationType.ToString().Replace("AK_EVENT_DURATION_", "").ToLowerInvariant());
            count++;
        }

        File.WriteAllLines(Path.Combine(App.Dir, SoundsFile), lines);
        Log.Info("Sound: saved " + count + " events to " + SoundsFile + ".");
        Overlay.Notice("SAVED " + SoundsFile.ToUpperInvariant(), Fmt.Plural(count, "SOUND"), true);
    }

    public override void Started()
    {
        Safe.Run("music folder", () =>
        {
            Directory.CreateDirectory(Dir);
            string readme = Path.Combine(Dir, "PUT YOUR SONGS HERE.txt");
            if (!File.Exists(readme))
                File.WriteAllText(readme, "Drop .mp3, .wav or .wma files in this folder, then open the Oracle menu (F3) > MUSIC.\r\nSongs play in alphabetical order; rename them with numbers (01 ..., 02 ...) to choose the order.\r\n");
        });

        // A leftover worker from before a hot reload has a different type, so it's stopped through a plain delegate.
        Shared.Get<Action>(StopKey)?.Invoke();
        Worker worker = new();
        _worker = worker;
        Shared.Set(StopKey, (Action)worker.Stop);
        Scan();
    }

    public override void WorldChanged()
    {
        Safe.Run("game volumes", RestoreGame);
        _playingSound = "";
        _transitionPlaying = "";
    }

    public override void LeftGame()
    {
        Safe.Run("game volumes", RestoreGame);
        _playingSound = "";
        _transitionPlaying = "";
        if (_playing || _track >= 0)
            Stop();
    }

    public override void Shutdown()
    {
        Safe.Run("sound stop", StopSound);
        Safe.Run("sound stop", EndTransition);
        ShutdownPlayer();
    }

    private void ShutdownPlayer()
    {
        if (_worker is { } worker)
        {
            worker.Post(worker.Close);
            worker.Stop();
        }

        Shared.Set(StopKey, null);
    }

    private void Scan()
    {
        string current = _track >= 0 && _track < _tracks.Count ? _tracks[_track] : "";
        _tracks.Clear();
        try
        {
            if (Directory.Exists(Dir))
            {
                foreach (string f in Directory.GetFiles(Dir))
                {
                    if (Array.IndexOf(Extensions, Path.GetExtension(f).ToLowerInvariant()) >= 0)
                        _tracks.Add(f);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Info("Music scan failed: " + ex.Message);
        }

        _tracks.Sort(StringComparer.OrdinalIgnoreCase);
        _track = current.Length > 0 ? _tracks.IndexOf(current) : -1;

        if (_tracks.Count == 0)
            _pick.SetOptions(new[] { "none" }, new[] { "NO SONGS FOUND" });
        else
            _pick.SetOptions(_tracks.Select(t => Path.GetFileName(t)).ToArray(), _tracks.Select(Label).ToArray(), Math.Max(0, _track));
    }

    private static string Label(string file)
    {
        string name = Path.GetFileNameWithoutExtension(file).Replace('_', ' ').ToUpperInvariant();
        return name.Length > MaxLabel ? name.Substring(0, MaxLabel - 1) + "..." : name;
    }

    private void PlayPicked(int index)
    {
        if (_tracks.Count > 0)
            Play(Math.Clamp(index, 0, _tracks.Count - 1));
    }

    private void Play(int index)
    {
        if (_worker is not { } worker || _tracks.Count == 0)
            return;

        _track = (index % _tracks.Count + _tracks.Count) % _tracks.Count;
        string file = _tracks[_track];
        int volume = Volume;
        worker.Error = "";
        worker.Post(() => worker.Open(file, volume));
        _playing = true;
        _pausedByGame = false;
        _pick.Index = _track;
        _pick.Saved = _pick.CurrentKey;
        Overlay.Notice("NOW PLAYING", Label(file), true);
        Log.Info("Music: " + Path.GetFileName(file));
    }

    private bool HaveSongs()
    {
        if (_tracks.Count == 0)
            Scan();
        if (_tracks.Count > 0)
            return true;

        Overlay.Notice("MUSIC", "NO SONGS IN THE MUSIC FOLDER", false);
        return false;
    }

    private void PlayPause()
    {
        if (!HaveSongs())
            return;

        if (_track < 0)
        {
            Play(_order.Index == Shuffle ? _random.Next(_tracks.Count) : 0);
            return;
        }

        if (_playing)
        {
            _worker?.Post(_worker.Pause);
            _playing = false;
            Overlay.Notice("MUSIC", "PAUSED", false);
        }
        else
        {
            _worker?.Post(_worker.Resume);
            _playing = true;
            Overlay.Notice("MUSIC", "PLAYING", true);
        }
    }

    private void Step(int dir)
    {
        if (!HaveSongs())
            return;

        if (_order.Index == Shuffle && _tracks.Count > 1)
        {
            int next;
            do
                next = _random.Next(_tracks.Count);
            while (next == _track);
            Play(next);
            return;
        }

        Play(_track < 0 ? 0 : _track + dir);
    }

    private void Stop()
    {
        _worker?.Post(_worker.Close);
        _playing = false;
        _pausedByGame = false;
        _track = -1;
    }

    public override void Frame()
    {
        if (_worker is not { } worker)
            return;

        if (worker.Error.Length > 0)
        {
            Log.Info("Music: " + worker.Error);
            Overlay.Notice("MUSIC", "CAN'T PLAY THIS FILE", false);
            worker.Error = "";
            _playing = false;
        }

        if (_playing && _followPause.On)
        {
            bool paused = World.Paused;
            if (paused && !_pausedByGame)
            {
                _pausedByGame = true;
                worker.Post(worker.Pause);
            }
            else if (!paused && _pausedByGame)
            {
                _pausedByGame = false;
                worker.Post(worker.Resume);
            }
        }

        if (_playing && worker.Finished)
        {
            worker.Finished = false;
            if (_order.Index == RepeatOne)
                Play(_track);
            else
                Step(1);
        }
    }
}
