#if !PROGENITOR_TESTS
using System;
using Progenitor.Plugins;

namespace Progenitor.Ui;

/// <summary>
/// <c>ctx.Keybinds</c>. A thin, tracked wrapper over <see cref="Keybinds"/>: it supplies the
/// plugin's owner, GUID and <c>GUID.cfg</c>, records the handle so unload unregisters, and
/// registers the plugin's config file as a <see cref="SettingsCatalog"/> source so its settings
/// show up in the panel alongside its keys.
///
/// <para>
/// Server-safe: registration succeeds and logs, but the server's key source is
/// <see cref="NullKeyInputSource"/>, so nothing ever fires. A plugin that ships one assembly for
/// both sides therefore needs no <c>ctx.IsClient</c> check around its binds.
/// </para>
/// </summary>
public sealed class PluginKeybinds
{
    private readonly PluginContext _ctx;

    internal PluginKeybinds(PluginContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        try { SettingsCatalog.Register(ctx.Guid, ctx.Config.File, ctx.Owner); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(
                "Ui: could not register '" + ctx.Guid + "' as a settings source: " + e.Message);
        }
    }

    /// <summary>
    /// Register a keybind backed by a <c>[Keybinds]</c> string entry in this plugin's
    /// <c>GUID.cfg</c>. The returned handle is already tracked on the context, so unload
    /// unregisters it; dispose it yourself only to drop a bind early.
    /// </summary>
    /// <param name="id">Stable id; also the cfg key. Shown in the settings panel.</param>
    /// <param name="defaultKey">Default chord, used when the cfg has no value or an unreadable one.</param>
    /// <param name="description">Shown in the cfg and in the settings panel.</param>
    /// <param name="onPressed">Runs on the frame the chord goes down, on the main thread.</param>
    /// <param name="allowWhileCaptured">
    /// True lets the bind fire while a mod panel is capturing gameplay input. Default false: a
    /// panel is open, the player is clicking it, and a stray hotkey is almost never what they
    /// meant. Chat focus suppresses the bind either way.
    /// </param>
    public IKeybindHandle Register(
        string id,
        KeyChord defaultKey,
        string description,
        Action onPressed,
        bool allowWhileCaptured = false)
    {
        var handle = Keybinds.Register(
            _ctx.Owner,
            _ctx.Guid,
            id,
            _ctx.Config.File,
            defaultKey,
            description,
            onPressed,
            allowWhileCaptured);
        _ctx.Track(handle);
        return handle;
    }

    /// <summary>
    /// KEYS1: a bind with press, hold and release handlers (any may be null, not all). While the main
    /// key stays down after the press, <paramref name="whileHeld"/> runs once per frame; when it comes
    /// up — or the bind stops being allowed to fire (a panel takes input, chat or a text field gets
    /// focus) — <paramref name="onReleased"/> runs once. Same cfg entry, Keys row, Keybinds-tab row
    /// and winner rule as <see cref="Register(string, KeyChord, string, Action, bool)"/>.
    /// </summary>
    public IKeybindHandle RegisterHold(
        string id,
        KeyChord defaultKey,
        string description,
        Action? onPressed = null,
        Action? whileHeld = null,
        Action? onReleased = null,
        bool allowWhileCaptured = false)
    {
        var handle = Keybinds.Register(
            _ctx.Owner,
            _ctx.Guid,
            id,
            _ctx.Config.File,
            defaultKey,
            description,
            onPressed,
            whileHeld,
            onReleased,
            allowWhileCaptured);
        _ctx.Track(handle);
        return handle;
    }

    /// <summary>
    /// MAPTP1: a bind that works only while the game's world map is open (the full-screen menu on its
    /// Map tab, which the M key opens). It never fires in normal play, so it never clashes with a
    /// gameplay key: it competes only with what is live in the map — other map binds, menu binds, the
    /// game's own map keys (Map Pan, Map Marker, Center Player, Toggle Legend, Map Zoom) and menu keys,
    /// where an exact game chord wins, and Push To Talk, which the game reads on every screen and binds to
    /// <see cref="UiKey.MouseMiddle"/> by default: that clash is reported and both fire. Same cfg entry,
    /// Keys row and Keybinds-tab row as
    /// <see cref="Register(string, KeyChord, string, Action, bool)"/>; the tab notes where it works.
    /// The map holds no chat input, so an unreadable chat window does not suppress it; a focused text
    /// field still does.
    /// </summary>
    public IKeybindHandle RegisterInMap(
        string id,
        KeyChord defaultKey,
        string description,
        Action onPressed) =>
        RegisterHoldInMap(id, defaultKey, description, onPressed ?? throw new ArgumentNullException(nameof(onPressed)));

    /// <summary>
    /// MAPTP1: the press, hold and release form of <see cref="RegisterInMap"/> (any handler may be null,
    /// not all). <paramref name="onReleased"/> also runs once when the map closes under a held key, so a
    /// hold-to-act gesture can cancel there.
    /// </summary>
    public IKeybindHandle RegisterHoldInMap(
        string id,
        KeyChord defaultKey,
        string description,
        Action? onPressed = null,
        Action? whileHeld = null,
        Action? onReleased = null)
    {
        var handle = Keybinds.RegisterScoped(
            _ctx.Owner,
            _ctx.Guid,
            id,
            _ctx.Config.File,
            defaultKey,
            description,
            onPressed,
            whileHeld,
            onReleased,
            allowWhileCaptured: false,
            allowWhenChatUnknown: true,
            KeybindScope.Map);
        _ctx.Track(handle);
        return handle;
    }

    /// <summary>
    /// PAD1: give one of this plugin's binds a controller default, so it also fires on a gamepad:
    /// <c>ctx.Keybinds.SetGamepad(handle, KeyChord.Gamepad(UiKey.GamepadNorth, UiKey.GamepadDpadDown))</c> presses Pad Y
    /// while D-Pad Down is held. The player's choice in the Keybinds tab (saved as <c>[Keybinds] &lt;id&gt;.Gamepad</c>)
    /// wins over the default. The controller chord goes through the bind's own gates, scope and handlers; it is checked
    /// against the game's controller bindings and the other binds' controller chords by the same winner rule as keys.
    /// Inside a handler, <see cref="Keybinds.PressedWithGamepad"/> says which chord fired. Returns <paramref name="handle"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="gamepadDefault"/> is bound and not a controller chord.</exception>
    public IKeybindHandle SetGamepad(IKeybindHandle handle, KeyChord gamepadDefault)
    {
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        if (!string.Equals(handle.PluginId, _ctx.Guid, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The keybind belongs to another plugin.", nameof(handle));
        return Keybinds.SetGamepadDefault(handle, gamepadDefault);
    }

    /// <summary>Convenience overload for a bind with no modifiers.</summary>
    public IKeybindHandle Register(
        string id,
        UiKey defaultKey,
        string description,
        Action onPressed,
        bool allowWhileCaptured = false) =>
        Register(id, new KeyChord(defaultKey), description, onPressed, allowWhileCaptured);

    /// <summary>
    /// KEYOPEN1: a key that opens or closes this mod's own UI (a Mods page through <c>ctx.Menu.OpenPage</c>, a window,
    /// a panel). Unlike <see cref="Register(string, KeyChord, string, Action, bool)"/> it also fires where no chat
    /// window exists: the title screen, the loading screen, the moments before the HUD is up. It also fires while a
    /// panel has input, so the key that opened a window closes it. A focused chat box or text field still suppresses
    /// it. Same cfg entry, Keys row, Keybinds-tab row and winner rule as <c>Register</c>.
    ///
    /// <para><b>Only for a handler that opens or closes the mod's own UI.</b> It must not send chat, run a command or
    /// write into a text field it does not own: at the title screen there is no chat box to tell the player is typing.
    /// For the same reason a printable key (a letter, a digit, Space, Enter, punctuation) without Ctrl or Alt is refused:
    /// a player's cfg value of that kind falls back to <paramref name="defaultKey"/>, and a printable default keeps its
    /// key but fires in play only, as <c>Register</c> does, with a warning in the log.</para>
    /// </summary>
    public IKeybindHandle RegisterOpener(
        string id,
        KeyChord defaultKey,
        string description,
        Action onPressed)
    {
        var handle = Keybinds.Register(
            _ctx.Owner,
            _ctx.Guid,
            id,
            _ctx.Config.File,
            defaultKey,
            description,
            onPressed ?? throw new ArgumentNullException(nameof(onPressed)),
            allowWhileCaptured: true,
            allowWhenChatUnknown: true);
        _ctx.Track(handle);
        return handle;
    }
}
#endif
