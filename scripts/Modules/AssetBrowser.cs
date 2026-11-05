using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// SYSTEM > ASSETS: a browser for the game's own textures and fonts. It lists what is loaded right now, saves the
/// whole list to assets.txt, and previews a texture beside the menu. Also owns the MENU FONT setting, which picks
/// from the fonts it finds. Textures the menu uses from the game (like the fade spinner) were found with it.
/// </summary>
internal sealed class AssetBrowser : Module
{
    public static readonly AssetBrowser I = new();

    public const string TabTitle = "ASSETS";
    private const string ListFile = "assets.txt";

    private static readonly (string Key, string Label, string[] Words)[] Filters =
    {
        ("ui", "UI AND HUD", new[] { "UI", "HUD", "GFX", "MENU", "FRONTEND", "SCALEFORM", "ICON", "MAP", "RETICLE", "CROSSHAIR" }),
        ("icons", "ICONS", new[] { "ICON", "BUTTON", "PROMPT", "GLYPH" }),
        ("fonts", "FONT PAGES", new[] { "FONT" }),
        ("all", "EVERYTHING", Array.Empty<string>()),
    };

    /// <summary>
    /// Generated textures that are never menu art and flood the list: reflection cubemap faces (thousands, and
    /// they draw as plain white), baked lightmaps and shadowmaps. Left out of the browser, kept in the saved file.
    /// </summary>
    private static readonly string[] Junk = { "CUBEMAPFACE", "TEXTURECUBE_", "LIGHTMAPTEXTURE2D", "SHADOWMAPTEXTURE2D" };

    private readonly List<(string Path, int W, int H)> _textures = new();
    private Choice _filter = null!;
    private Choice _texture = null!;
    private Choice _font = null!;
    private Toggle _preview = null!;
    private bool _fontsListed;
    private Texture2D? _shown;
    private string _shownPath = "";

    public override void Build()
    {
        AddCommand("asset_refresh", "REFRESH LISTS", "Lists every texture and font the game has loaded right now. What's loaded changes with where you are, so refresh after moving somewhere new.", Refresh).Setting();
        AddCommand("asset_dump", "SAVE LIST TO FILE", "Writes every loaded texture (with its size) and font to assets.txt in the Oracle folder.", Dump).Setting();
        _filter = AddChoice("asset_filter", "SHOW", "Which textures to list: names that look like interface art, icons, font pages, or everything.",
            Filters.Select(f => f.Key).ToArray(), Filters.Select(f => f.Label).ToArray(), _ => ListTextures()).Setting();
        _texture = AddChoice("asset_texture", "TEXTURE", "Hold left or right to scroll. The selected texture is drawn beside the menu.", new[] { "" }, new[] { "PRESS REFRESH LISTS" }).Pref();
        _texture.HoldToScroll = true;
        _preview = AddToggle("asset_preview", "PREVIEW", "Draws the selected texture beside the menu while this tab is open, over a checkerboard so see-through parts show.", on: true).Pref();

        _font = AddChoice("menu_font", "MENU FONT", "The font the Oracle menu is drawn in, from the fonts the game has loaded. Each is sized to match. A font that isn't loaded yet uses the default until it is.",
            new[] { Gfx.DefaultFont }, new[] { "ENGINE (DEFAULT)" }, _ => Gfx.UseFont(_font.Saved.Length > 0 ? _font.Saved : _font.CurrentKey)).Setting();
        _font.HoldToScroll = true;
    }

    public override void MenuOpening()
    {
        if (!_fontsListed)
            ListFonts();
    }

    private void Refresh()
    {
        ListFonts();
        ListTextures();
        Overlay.Notice("ASSETS", _textures.Count + " TEXTURES, " + (_font.Labels.Length - 1) + " FONTS", true);
    }

    private void ListFonts()
    {
        _fontsListed = true;
        var paths = new List<string> { Gfx.DefaultFont };
        foreach (Font f in World.All<Font>())
        {
            string path = f.GetPathName(null!);
            if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                paths.Add(path);
        }

        string[] labels = paths.Select((p, i) => i == 0 ? "ENGINE (DEFAULT)" : Short(p)).ToArray();
        _font.SetOptions(paths.ToArray(), labels);
    }

    private void ListTextures()
    {
        _textures.Clear();
        string[] words = Filters[Math.Clamp(_filter.Index, 0, Filters.Length - 1)].Words;
        foreach (Texture2D t in World.All<Texture2D>())
        {
            string path = t.GetPathName(null!);
            string upper = path.ToUpperInvariant();
            if (Junk.Any(j => upper.Contains(j, StringComparison.Ordinal)))
                continue;
            if (words.Length == 0 || words.Any(w => upper.Contains(w, StringComparison.Ordinal)))
                _textures.Add((path, t.SizeX, t.SizeY));
        }

        _textures.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        if (_textures.Count == 0)
            _texture.SetOptions(new[] { "" }, new[] { "NONE LOADED" });
        else
            _texture.SetOptions(_textures.Select(t => t.Path).ToArray(), _textures.Select(t => Short(t.Path)).ToArray());
    }

    private void Dump()
    {
        var lines = new List<string> { "# Oracle asset list, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"), "", "[fonts]" };
        lines.AddRange(World.All<Font>().Select(f => f.GetPathName(null!)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        lines.Add("");
        lines.Add("[textures] path  width x height");
        int count = 0;
        foreach (Texture2D t in World.All<Texture2D>().OrderBy(t => t.GetPathName(null!), StringComparer.OrdinalIgnoreCase))
        {
            lines.Add(t.GetPathName(null!) + "  " + t.SizeX + "x" + t.SizeY);
            count++;
        }

        File.WriteAllLines(Path.Combine(App.Dir, ListFile), lines);
        Log.Info("Assets: saved " + count + " textures and the loaded fonts to " + ListFile + ".");
        Overlay.Notice("SAVED " + ListFile.ToUpperInvariant(), count + " TEXTURES", true);
    }

    private static string Short(string path)
    {
        int dot = path.LastIndexOf('.');
        return (dot >= 0 ? path.Substring(dot + 1) : path).ToUpperInvariant();
    }

    /// <summary>True while the ASSETS tab is open with PREVIEW on and a texture picked.</summary>
    public bool Previewing => _preview.On && Menu.IsOpen && Menu.InRows && !Menu.Searching && Menu.Current.Title == TabTitle && _texture.CurrentKey.Length > 0;

    /// <summary>Draws the selected texture's details and image inside a panel the menu has already drawn.</summary>
    public void DrawPreview(float x, float y, float maxW, float maxH, Rgba accent)
    {
        string path = _texture.CurrentKey;
        if (path != _shownPath || _shown is not { IsValid: true })
        {
            _shownPath = path;
            try
            {
                _shown = Game.FindObject<Texture2D>(path);
            }
            catch (Exception ex)
            {
                _shown = null;
                Log.Throttled("asset preview", "Asset preview: " + path + " failed: " + ex.Message);
            }
        }

        float pad = Gfx.S(14);
        float w = maxW;

        var entry = _textures.Find(t => t.Path == path);
        float textY = y + pad;
        Gfx.Text(x + pad, textY, Short(path), Theme.Hi, 0.7f);
        textY += Gfx.TextH(0.7f) + Gfx.S(3);
        Gfx.Text(x + pad, textY, path, Theme.Low, 0.5f);
        textY += Gfx.TextH(0.5f) + Gfx.S(3);
        Gfx.Text(x + pad, textY, entry.W + " x " + entry.H + "   " + (_texture.Index + 1) + " OF " + _texture.Labels.Length, accent, 0.5f);
        textY += Gfx.TextH(0.5f) + Gfx.S(10);

        // Checkerboard behind the image, so transparent parts read as transparent.
        float boxW = w - pad * 2f;
        float boxH = maxH - (textY - y) - pad;
        if (boxH < Gfx.S(40))
            return;

        float cell = Gfx.S(16);
        for (float cy = 0; cy < boxH; cy += cell)
        {
            for (float cx = 0; cx < boxW; cx += cell)
            {
                bool dark = ((int)(cx / cell) + (int)(cy / cell)) % 2 == 0;
                Gfx.Rect(x + pad + cx, textY + cy, Math.Min(cell, boxW - cx), Math.Min(cell, boxH - cy), dark ? Theme.Off : Theme.Low, 0.5f);
            }
        }

        if (_shown is not { IsValid: true } shown)
        {
            Gfx.Text(x + pad, textY + Gfx.S(6), "NOT LOADED ANY MORE, REFRESH", Theme.Warn, 0.6f);
            return;
        }

        float tw = Math.Max(1, shown.SizeX);
        float th = Math.Max(1, shown.SizeY);
        float fit = Math.Min(boxW / tw, boxH / th);
        float iw = tw * fit;
        float ih = th * fit;
        Gfx.Image(shown, x + pad + (boxW - iw) * 0.5f, textY + (boxH - ih) * 0.5f, iw, ih);
    }
}
