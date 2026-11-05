using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>Plays the player's own songs through Windows MCI, on a worker thread because MCI calls can stall.</summary>
internal sealed class Music : Module
{
    public static readonly Music I = new();

    private const string Folder = "Music";
    private const string Alias = "oraclemusic";
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

    private readonly List<string> _tracks = new();
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

    public override void LeftGame()
    {
        if (_playing || _track >= 0)
            Stop();
    }

    public override void Shutdown()
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
