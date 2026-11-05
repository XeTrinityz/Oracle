namespace CeZeY.Oracle;

/// <summary>
/// What's on the favourites radial: one flat list of feature ids, read in pages of <see cref="PerPage"/> slots.
/// An empty string is an empty slot, so a layout keeps its gaps. Changing the slots per page re-flows the same
/// list, in the same order, into the new page size.
/// </summary>
internal static class Favourites
{
    public const int MaxPages = 9;
    public const int MinPerPage = 4;
    public const int MaxPerPage = 12;

    /// <summary>SLOTS PER PAGE.</summary>
    public static int PerPage = 8;

    public static readonly List<string> Slots = new();

    public static int Capacity => MaxPages * PerPage;

    /// <summary>The same rule as a hotkey: anything but a row that changes the save, which always asks first.</summary>
    public static bool CanHold(Feature f) => f.Bindable;

    /// <summary>Pages that hold something, at least one.</summary>
    public static int UsedPages
    {
        get
        {
            int last = LastFilled();
            return last < 0 ? 1 : Math.Min(MaxPages, last / PerPage + 1);
        }
    }

    public static int Count
    {
        get
        {
            int n = 0;
            foreach (string s in Slots)
            {
                if (s.Length > 0)
                    n++;
            }

            return n;
        }
    }

    public static int IndexOf(string id) => Slots.FindIndex(s => s.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static bool Contains(Feature f) => IndexOf(f.Id) >= 0;

    public static Feature? At(int page, int slot) => slot < 0 || slot >= PerPage ? null : At(page * PerPage + slot);

    public static Feature? At(int index) =>
        index >= 0 && index < Slots.Count && Slots[index].Length > 0 && Catalog.ById.TryGetValue(Slots[index], out Feature? f) ? f : null;

    /// <summary>The first empty slot, or -1 when every page is full.</summary>
    public static int FirstFree()
    {
        for (int i = 0; i < Capacity; i++)
        {
            if (i >= Slots.Count || Slots[i].Length == 0)
                return i;
        }

        return -1;
    }

    /// <summary>Puts a feature in a slot, replacing whatever was there.</summary>
    public static void Put(int index, string id)
    {
        if (index < 0 || index >= Capacity)
            return;

        while (Slots.Count <= index)
            Slots.Add("");
        Slots[index] = id;
        Changed();
    }

    public static void Empty(int index)
    {
        if (index < 0 || index >= Slots.Count)
            return;

        Slots[index] = "";
        Changed();
    }

    /// <summary>Swaps two slots, either of which may be empty.</summary>
    public static void Swap(int a, int b)
    {
        if (a == b || a < 0 || b < 0 || a >= Capacity || b >= Capacity)
            return;

        while (Slots.Count <= Math.Max(a, b))
            Slots.Add("");
        (Slots[a], Slots[b]) = (Slots[b], Slots[a]);
        Changed();
    }

    public static void ClearAll()
    {
        Slots.Clear();
        Changed();
    }

    /// <summary>
    /// Reads the saved list. Unknown ids (a feature that was removed) and repeats become empty slots, so the
    /// rest of the layout stays where it was.
    /// </summary>
    public static void Load(TomlArray? list)
    {
        Slots.Clear();
        if (list == null)
            return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (object? item in list)
        {
            if (Slots.Count >= MaxPages * MaxPerPage)
                break;

            bool keep = item is string id && id.Length > 0 && Catalog.ById.TryGetValue(id, out Feature? f) && CanHold(f) && seen.Add(id);
            Slots.Add(keep ? (string)item! : "");
        }

        Trim();
    }

    /// <summary>Takes the old PINNED page's rows, in order, the first time settings are read after the update.</summary>
    public static void FromPins(IEnumerable<string> pins)
    {
        Slots.Clear();
        foreach (string id in pins)
        {
            if (Slots.Count >= Capacity)
                break;
            if (Catalog.ById.TryGetValue(id, out Feature? f) && CanHold(f) && IndexOf(id) < 0)
                Slots.Add(f.Id);
        }
    }

    /// <summary>The list as a TOML array, without trailing empty slots.</summary>
    public static string Toml()
    {
        int last = LastFilled();
        return "[" + string.Join(", ", Slots.Take(last + 1).Select(id => "\"" + id + "\"")) + "]";
    }

    private static int LastFilled() => Slots.FindLastIndex(s => s.Length > 0);

    private static void Trim()
    {
        int last = LastFilled();
        if (last + 1 < Slots.Count)
            Slots.RemoveRange(last + 1, Slots.Count - last - 1);
    }

    private static void Changed()
    {
        Trim();
        Settings.MarkDirty();
    }
}
