using System.Globalization;

namespace CeZeY.Oracle;

/// <summary>
/// One row of the menu and one line of settings.toml. Modules hold typed references (Slider.Value, Toggle.On)
/// and read them directly; the row itself only knows how to change, describe and persist its value.
/// </summary>
internal abstract class Feature
{
    public string Id = "";
    public string Label = "";
    public string Help = "";
    public string Section = "cheats";
    public string Key = "";

    /// <summary>A preference, not a cheat: it never counts as running and isn't switched on by a load.</summary>
    public bool IsPref;

    /// <summary>A menu setting, which applies at once even outside a game.</summary>
    public bool IsSetting;

    /// <summary>Changes the save file. Always confirmed, can't be bound to a key.</summary>
    public bool ChangesSave;

    public bool NeedsConfirm;

    /// <summary>Replaces the generic confirmation question.</summary>
    public Func<string>? ConfirmPrompt;

    /// <summary>Re-applied every 0.25 s, and at once on a new pawn.</summary>
    public Action? MaintainStep;

    /// <summary>Re-applied every tick.</summary>
    public Action? TickStep;

    public abstract bool IsDefault { get; }

    public abstract string State { get; }

    public virtual bool Bindable => !ChangesSave;

    public bool Active => !IsPref && !IsSetting && !IsDefault;

    public abstract void Apply();

    public abstract void ResetValue();

    /// <summary>Moves the value one step; returns false when it can't move.</summary>
    public abstract bool Step(int dir);

    /// <summary>What a hotkey does: flips, cycles or jumps to the preset value.</summary>
    public abstract void Hotkey();

    public abstract void Read(TomlTable table);

    /// <summary>The TOML literal for this value, or null if it isn't saved.</summary>
    public abstract string? Toml { get; }
}

internal sealed class Toggle : Feature
{
    public bool On;
    public bool Default;
    public Action<bool>? Changed;

    public override bool IsDefault => On == Default;

    public override string State => On ? "ON" : "OFF";

    public override void Apply() => Changed?.Invoke(On);

    public override void ResetValue() => On = Default;

    public override bool Step(int dir)
    {
        On = !On;
        return true;
    }

    public override void Hotkey() => On = !On;

    public override void Read(TomlTable table) => On = table.TryGetValue(Key, out object? v) && v is bool b ? b : Default;

    public override string Toml => On ? "true" : "false";
}

internal sealed class Slider : Feature
{
    public float Value;
    public float Default;
    public float Min;
    public float Max;
    public float Stride;
    public float HotValue;
    public Func<float, string> Format = v => v.ToString("0.##", CultureInfo.InvariantCulture);
    public Action<float>? Changed;

    public override bool IsDefault => MathF.Abs(Value - Default) <= 0.0001f;

    public override string State => Format(Value);

    public override void Apply() => Changed?.Invoke(Value);

    public override void ResetValue() => Value = Default;

    public override bool Step(int dir)
    {
        float next = MathF.Round(Math.Clamp(Value + dir * Stride, Min, Max) / Stride) * Stride;
        next = MathF.Round(next, 3);
        if (MathF.Abs(next - Value) < 0.0001f)
            return false;

        Value = next;
        return true;
    }

    public override void Hotkey() => Value = IsDefault ? HotValue : Default;

    public override void Read(TomlTable table)
    {
        Value = Default;
        if (table.TryGetValue(Key, out object? v))
        {
            if (v is double d)
                Value = (float)d;
            else if (v is long l)
                Value = l;
        }

        Value = Math.Clamp(Value, Min, Max);
    }

    public override string Toml => Value.ToString("0.0##", CultureInfo.InvariantCulture);
}

internal sealed class Choice : Feature
{
    public int Index;
    public string[] Keys = Array.Empty<string>();
    public string[] Labels = Array.Empty<string>();
    public Action<int>? Changed;

    /// <summary>Raw saved key, kept for lists that fill in later (songs, skins) so the pick survives until they do.</summary>
    public string Saved = "";

    /// <summary>Optional colour chip for each option.</summary>
    public Func<int, Rgba>? Swatch;

    /// <summary>A long list: holding left or right keeps moving through it.</summary>
    public bool HoldToScroll;

    public override bool IsDefault => Index == 0;

    public override string State => Labels.Length == 0 ? "" : Labels[Math.Clamp(Index, 0, Labels.Length - 1)];

    public string CurrentKey => Keys.Length == 0 ? "" : Keys[Math.Clamp(Index, 0, Keys.Length - 1)];

    public override void Apply() => Changed?.Invoke(Index);

    public override void ResetValue()
    {
        Index = 0;
        Saved = CurrentKey;
    }

    public override bool Step(int dir)
    {
        if (Labels.Length < 2)
            return false;

        Index = (Index + dir + Labels.Length) % Labels.Length;
        Saved = CurrentKey;
        return true;
    }

    public override void Hotkey() => Step(1);

    public override void Read(TomlTable table)
    {
        Saved = table.TryGetValue(Key, out object? v) && v is string s ? s : Keys.Length == 0 ? "" : Keys[0];
        Index = Math.Max(0, Array.FindIndex(Keys, k => k.Equals(Saved, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Replaces the options, keeping the saved pick when it's still among them.</summary>
    public void SetOptions(string[] keys, string[] labels, int fallback = 0)
    {
        Keys = keys;
        Labels = labels;
        int found = Array.FindIndex(keys, k => k.Equals(Saved, StringComparison.OrdinalIgnoreCase));
        Index = found >= 0 ? found : Math.Clamp(fallback, 0, Math.Max(0, keys.Length - 1));
    }

    public override string Toml => "\"" + (Saved.Length > 0 ? Saved : CurrentKey) + "\"";
}

internal sealed class Command : Feature
{
    public Action? Run;

    /// <summary>
    /// Moves the player somewhere. On a shared key these are alternatives: they are tried in menu order until one
    /// works, while every other command on the key runs.
    /// </summary>
    public bool MovesPlayer;

    public override bool IsDefault => true;

    public override string State => "";

    public override void Apply() { }

    public override void ResetValue() { }

    public override bool Step(int dir) => false;

    public override void Hotkey() { }

    public override void Read(TomlTable table) { }

    public override string? Toml => null;
}

internal static class FeatureSetup
{
    public static T Pref<T>(this T f) where T : Feature
    {
        f.IsPref = true;
        return f;
    }

    public static T Setting<T>(this T f) where T : Feature
    {
        f.IsSetting = true;
        f.Section = "general";
        return f;
    }

    public static T Maintain<T>(this T f, Action step) where T : Feature
    {
        f.MaintainStep = step;
        return f;
    }

    public static T EveryTick<T>(this T f, Action step) where T : Feature
    {
        f.TickStep = step;
        return f;
    }

    public static T Confirmed<T>(this T f) where T : Feature
    {
        f.NeedsConfirm = true;
        return f;
    }

    public static Command Moves(this Command c)
    {
        c.MovesPlayer = true;
        return c;
    }

    public static T Stored<T>(this T f, string section, string key) where T : Feature
    {
        f.Section = section;
        f.Key = key;
        return f;
    }
}
