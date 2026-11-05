using BmSDK.Engine;

namespace CeZeY.Oracle;

/// <summary>
/// One self-contained area of Oracle. To add one: subclass this, register features in <see cref="Build"/>, and
/// list the instance in <see cref="App.Modules"/>. App isolates every callback, so a failing module logs and
/// the rest keep running. [Redirect] methods are entry points of their own and must guard themselves.
/// </summary>
internal abstract class Module
{
    public virtual string Name => GetType().Name;

    /// <summary>True while <see cref="Draw"/> has something to show.</summary>
    public virtual bool HasHud => false;

    /// <summary>Registers this module's features. Runs once per load, before settings are read.</summary>
    public virtual void Build() { }

    /// <summary>Settings have been read for the first time.</summary>
    public virtual void Started() { }

    /// <summary>A new world began loading. Every cached object is about to go stale.</summary>
    public virtual void WorldChanged() { }

    /// <summary>The player went back to the main menu.</summary>
    public virtual void LeftGame() { }

    /// <summary>Runs every game tick once saved cheats have been applied.</summary>
    public virtual void Tick(float dt) { }

    /// <summary>Runs every rendered frame, including while paused and when game ticks stop.</summary>
    public virtual void Frame() { }

    /// <summary>Draws always-on HUD elements, beneath the menu.</summary>
    public virtual void Draw(Canvas c) { }

    /// <summary>The menu is about to open, so lists it shows should be refreshed.</summary>
    public virtual void MenuOpening() { }

    /// <summary>The mod is unloading. Put the game back exactly as it was found.</summary>
    public virtual void Shutdown() { }
}
