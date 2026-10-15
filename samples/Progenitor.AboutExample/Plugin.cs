using BepInEx;
using Progenitor.Plugins;

namespace Progenitor.AboutExample;

/// <summary>
/// The smallest mod with its own About text. The embedded <c>ABOUT.md</c> (published as the resource
/// <c>README.md</c>) is what the Mods page shows; <c>[ModAbout]</c> is what a plugin without an embedded README would
/// show. Neither is replaced by a store listing unless the install ledger or a package manifest beside the DLL ties
/// one to this file.
/// </summary>
[BepInPlugin("progenitor.aboutexample", "Progenitor About Example", "0.1.0")]
[BepInDependency("progenitor")]
[ProgenitorSide(Side.Client)]
[ModAbout("Shown only when the assembly has no embedded README: **[ModAbout]** text.")]
public sealed class Plugin : ProgenitorPlugin<AboutContext>
{
    protected override void OnLoad(AboutContext ctx) =>
        ctx.Log.LogInfo("About example: loaded; open its Mods page to see the embedded README.");
}

public sealed class AboutContext : PluginContext { }
