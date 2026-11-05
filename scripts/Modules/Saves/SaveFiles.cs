using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using BmSDK;
using Microsoft.Win32;

namespace CeZeY.Oracle;

/// <summary>
/// Arkham Knight's save files on disk. Each slot has three files in Steam's remote folder,
/// BAK1Save{slot}x{n}.sgd, which the game writes in turn and loads the newest of. Steamworks also copies every
/// write into Documents\WB Games\Batman Arkham Knight\{SteamID64}\SaveData\backup as
/// BAK1Save{slot}x{n}.sgd_{yyyy-MM-dd_HH-mm-ss}, and no menu in the game shows those. Oracle's own copies use the
/// backup naming in the mod's saves folder, so one reader handles all three places.
/// </summary>
internal static class SaveFiles
{
    public enum Kind { Current, Backup, Kept }

    /// <summary>One save file. <see cref="When"/> is the time stored in the save, or the file's time if unreadable.</summary>
    public sealed record Entry(string Path, Kind Kind, int Slot, int XId, DateTime When, int LocationId, string Difficulty, int Percent, bool Valid)
    {
        /// <summary>The save's own timestamp as a FILETIME, 0 when the header couldn't be read. Same save = same stamp.</summary>
        public long Stamp { get; init; }

        /// <summary>Main story and New Game + progress as the main menu shows them before DLC bonuses, -1 if unread.</summary>
        public float Story { get; init; } = -1f;
        public float StoryPlus { get; init; } = -1f;

        /// <summary>True when the save holds a New Game + run (its progress only grows once one has started).</summary>
        public bool HasPlus => StoryPlus > 0f;
    }

    private const string AppId = "208650";
    private const long SteamId64Base = 76561197960265728L;
    private const int SaveVersion = 6;
    private const int ScanBytes = 256 * 1024;
    private const string FileStamp = "yyyy-MM-dd_HH-mm-ss";

    private static readonly Regex FileName = new(@"^BAK1Save(\d)x(\d)\.sgd(?:_\d{4}-\d\d-\d\d_\d\d-\d\d-\d\d)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly long MinStamp = new DateTime(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
    private static readonly long MaxStamp = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();

    // Reading a header costs 64 KB, and the backup folder keeps dozens, so each is read once per change.
    private static readonly Dictionary<string, (DateTime Written, long Size, Entry Entry)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static string RemoteDir { get; private set; } = "";
    public static string BackupDir { get; private set; } = "";
    public static string KeptDir => System.IO.Path.Combine(App.Dir, "saves");

    /// <summary>Finds the logged-in Steam user's save folders. Missing folders stay empty and list nothing.</summary>
    public static void Locate()
    {
        string steam = (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string)?.Replace('/', '\\')
            ?? @"C:\Program Files (x86)\Steam";
        int user = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", 0) is int u ? u : 0;

        string userdata = System.IO.Path.Combine(steam, "userdata");
        string mine = System.IO.Path.Combine(userdata, user.ToString(CultureInfo.InvariantCulture), AppId, "remote");
        RemoteDir = user != 0 && Directory.Exists(mine) ? mine
            : Newest(Directory.Exists(userdata) ? Directory.GetDirectories(userdata).Select(d => System.IO.Path.Combine(d, AppId, "remote")) : Enumerable.Empty<string>());

        // The backup folder is named by SteamID64; the remote folder by the 32-bit account id inside it.
        string wb = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WB Games", "Batman Arkham Knight");
        string? account = RemoteDir.Length > 0 ? System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(RemoteDir))) : null;
        string ours = long.TryParse(account, out long id) ? System.IO.Path.Combine(wb, (SteamId64Base + id).ToString(CultureInfo.InvariantCulture), "SaveData", "backup") : "";
        BackupDir = ours.Length > 0 && Directory.Exists(ours) ? ours
            : Newest(Directory.Exists(wb) ? Directory.GetDirectories(wb).Select(d => System.IO.Path.Combine(d, "SaveData", "backup")) : Enumerable.Empty<string>());

        Log.Info("Saves: game files in " + Shown(RemoteDir) + ", game backups in " + Shown(BackupDir) + ", kept copies in " + KeptDir + ".");
    }

    private static string Shown(string dir) => dir.Length > 0 ? dir : "(not found)";

    private static string Newest(IEnumerable<string> dirs) =>
        dirs.Where(Directory.Exists).OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault() ?? "";

    /// <summary>Every save in the three places, newest first. A game backup that matches a file still in use is left out.</summary>
    public static List<Entry> Scan()
    {
        var list = new List<Entry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(list, seen, RemoteDir, Kind.Current);
        Collect(list, seen, KeptDir, Kind.Kept);
        Collect(list, seen, BackupDir, Kind.Backup);

        foreach (string gone in _cache.Keys.Where(k => !seen.Contains(k)).ToList())
            _cache.Remove(gone);

        // The game backs up every write, so its newest backups are the very files still in the remote folder.
        var inUse = new HashSet<(int, long)>(list.Where(e => e.Kind != Kind.Backup && e.Stamp != 0).Select(e => (e.Slot, e.Stamp)));
        list.RemoveAll(e => e.Kind == Kind.Backup && inUse.Contains((e.Slot, e.Stamp)));
        list.Sort((a, b) => b.When.CompareTo(a.When));
        return list;
    }

    private static void Collect(List<Entry> list, HashSet<string> seen, string dir, Kind kind)
    {
        if (dir.Length == 0 || !Directory.Exists(dir))
            return;

        foreach (string path in Directory.EnumerateFiles(dir, "BAK1Save*"))
        {
            Match m = FileName.Match(System.IO.Path.GetFileName(path));
            if (!m.Success)
                continue;

            seen.Add(path);
            try
            {
                var info = new FileInfo(path);
                if (_cache.TryGetValue(path, out var hit) && hit.Written == info.LastWriteTimeUtc && hit.Size == info.Length)
                {
                    list.Add(hit.Entry);
                    continue;
                }

                Entry e = Read(path, kind, m.Groups[1].Value[0] - '0', m.Groups[2].Value[0] - '0', info.LastWriteTime);
                _cache[path] = (info.LastWriteTimeUtc, info.Length, e);
                list.Add(e);
            }
            catch (Exception ex)
            {
                Log.Throttled("save read " + path, "Saves: could not read " + path + ": " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Reads the summary the game's slot screen shows. The file starts with version 6, and the options block holds,
    /// in a row: the save time as a FILETIME, day, month, year, location id, the difficulty as a length-prefixed
    /// string ("Hard"), then the completion percentage. What comes before it varies in length (unlocked
    /// showcase names), so it is found by that shape rather than at a fixed offset.
    /// </summary>
    private static Entry Read(string path, Kind kind, int slot, int xid, DateTime written)
    {
        byte[] head = new byte[ScanBytes];
        int n;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            n = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);

        if (n < 8 || BitConverter.ToInt32(head, 0) != SaveVersion)
            return new Entry(path, kind, slot, xid, written, -1, "", -1, false);

        int I(int at) => BitConverter.ToInt32(head, at);
        for (int o = 8; o + 32 <= n; o++)
        {
            long stamp = BitConverter.ToInt64(head, o);
            if (stamp < MinStamp || stamp > MaxStamp)
                continue;

            int day = I(o + 8), month = I(o + 12), year = I(o + 16), location = I(o + 20), len = I(o + 24);
            if (day is < 1 or > 31 || month is < 1 or > 12 || year is < 2015 or > 2100 || location is < 0 or > 4096 || len is < 2 or > 32)
                continue;
            if (o + 28 + len + 4 > n || head[o + 28 + len - 1] != 0)
                continue;

            string difficulty = Encoding.ASCII.GetString(head, o + 28, len - 1);
            if (!difficulty.All(char.IsLetterOrDigit))
                continue;

            DateTime when = DateTime.FromFileTimeUtc(stamp).ToLocalTime();
            (float story, float plus) = StoryPercents(head, o + 28 + len, n);
            return new Entry(path, kind, slot, xid, when, location, difficulty, Percent(head, o + 28 + len, n), true) { Stamp = stamp, Story = story, StoryPlus = plus };
        }

        return new Entry(path, kind, slot, xid, written, -1, "", -1, true);
    }

    private static readonly Regex Tally = new(@"^\d{1,3}/\d{1,3}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The completion percentage the slot screen shows (122% is possible). Later in the options block it sits just
    /// before a list of four progress strings ("6/243", "259/286"...): percent, count 4, then length-prefixed
    /// strings. Checked against the slot screen on 2026-11-05: 25% and 122% read correctly. -1 if not found.
    /// </summary>
    private static int Percent(byte[] head, int from, int n)
    {
        for (int o = Math.Max(from, 4); o + 12 <= n; o++)
        {
            if (BitConverter.ToInt32(head, o) != 4)
                continue;

            int len = BitConverter.ToInt32(head, o + 4);
            if (len is < 4 or > 8 || o + 8 + len > n || head[o + 8 + len - 1] != 0)
                continue;
            if (!Tally.IsMatch(Encoding.ASCII.GetString(head, o + 8, len - 1)))
                continue;

            int percent = BitConverter.ToInt32(head, o - 4);
            return percent is >= 0 and <= 999 ? percent : -1;
        }

        return -1;
    }

    /// <summary>
    /// RPersistentShared.StoryOverAllPercent, one float per story slot (main, temp, New Game +), which the main menu
    /// shows as CONTINUE STORY and CONTINUE STORY+ (plus 5% per finished DLC Most Wanted). Stored as count 3 then
    /// the floats, with no name before it, so it is found by shape: the first such array after the options block
    /// with main progress above zero and the temp slot at exactly 0. Checked on 2026-11-05 against 26 saves: slot 1
    /// read 25.4 / 0, slot 3 read 100 / 2.72 (menu: 120% and 2%). A later stray 1.0 / 0 also fits, hence the first.
    /// </summary>
    private static (float Story, float Plus) StoryPercents(byte[] head, int from, int n)
    {
        for (int o = from; o + 16 <= n; o++)
        {
            if (BitConverter.ToInt32(head, o) != 3)
                continue;

            float story = BitConverter.ToSingle(head, o + 4), temp = BitConverter.ToSingle(head, o + 8), plus = BitConverter.ToSingle(head, o + 12);
            if (story is > 0.01f and <= 100f && temp == 0f && plus is >= 0f and <= 100f)
                return (story, plus);
        }

        return (-1f, -1f);
    }

    /// <summary>The whole file, as the game's slot screen hands it over, even while Steam has it open.</summary>
    public static byte[] ReadAll(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var data = new byte[fs.Length];
        fs.ReadExactly(data);
        return data;
    }

    /// <summary>Copies a save into the kept folder. Returns false if that exact save is already kept.</summary>
    public static bool Keep(Entry e, out string dest)
    {
        Directory.CreateDirectory(KeptDir);
        dest = System.IO.Path.Combine(KeptDir, "BAK1Save" + e.Slot + "x" + e.XId + ".sgd_" + e.When.ToString(FileStamp, CultureInfo.InvariantCulture));
        if (File.Exists(dest))
            return false;

        // Through a temp name, so a half-written copy never shows up in the list.
        string temp = dest + ".tmp";
        File.WriteAllBytes(temp, ReadAll(e.Path));
        File.Move(temp, dest);
        return true;
    }

    /// <summary>
    /// The bytes in a game-owned array, for natives that take array&lt;byte&gt;. The constructor allocates with the
    /// game's allocator; the data is copied straight in and the count set in the header (data, count, capacity),
    /// as Push would do one element at a time for 2.4 MB.
    /// </summary>
    public static TArray<byte> ToGameArray(byte[] data)
    {
        var arr = new TArray<byte>(data.Length);
        try
        {
            nint dst = Marshal.ReadIntPtr(arr.Ptr);
            if (arr.Capacity >= data.Length && dst != 0)
            {
                Marshal.Copy(data, 0, dst, data.Length);
                Marshal.WriteInt32(arr.Ptr + 8, data.Length);
            }
            else
            {
                arr.Push(data);
            }

            if (arr.Count != data.Length)
                throw new InvalidOperationException("the save didn't fit in the game's buffer");
            return arr;
        }
        catch
        {
            arr.Dispose();
            throw;
        }
    }
}
