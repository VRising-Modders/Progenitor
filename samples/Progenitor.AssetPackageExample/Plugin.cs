using System;
using BepInEx;
using Progenitor.Plugins;
using UnityEngine;

namespace Progenitor.AssetPackageExample;

[BepInPlugin("progenitor.assetpackage.example", "Progenitor Asset Package Example", "0.1.0")]
[BepInDependency("progenitor")]
[ProgenitorSide(Side.Client)]
public sealed class Plugin : ProgenitorPlugin<AssetPackageContext>
{
    protected override void OnLoad(AssetPackageContext ctx)
    {
        var directory = ctx.Bind("Package", "Directory", "",
            "Absolute path to a validated static model package directory. Empty: import nothing.");
        var scale = ctx.Bind("Placement", "Scale", 1f, "Positive uniform visual scale; source units are preserved at 1.");
        var x = ctx.Bind("Placement", "X", 0f, "Client world X coordinate.");
        var y = ctx.Bind("Placement", "Y", 0f, "Client world Y (height) coordinate.");
        var z = ctx.Bind("Placement", "Z", 0f, "Client world Z coordinate.");
        if (string.IsNullOrWhiteSpace(directory.Value))
        {
            ctx.Log.LogInfo("Asset package example: set [Package] Directory and [Placement] X/Y/Z "
                + "in BepInEx/config/progenitor.assetpackage.example.cfg, then reload this plugin.");
            return;
        }
        if (!float.IsFinite(x.Value) || !float.IsFinite(y.Value) || !float.IsFinite(z.Value))
            throw new ArgumentException("Placement X/Y/Z must be finite coordinates.");

        if (!float.IsFinite(scale.Value) || scale.Value <= 0f)
            throw new ArgumentException("Placement Scale must be finite and positive.");

        var model = ctx.Models.ImportPackage(directory.Value.Trim(), "AssetPackageExample");
        model.Root.transform.localScale = Vector3.one * scale.Value;
        model.Root.transform.position = new Vector3(x.Value, y.Value, z.Value);
        ctx.Log.LogInfo("Asset package example: imported client visual at " + model.Root.transform.position);
        // ImportPackage tracks the model and generated resources for every unload reason.
    }

    protected override void OnUnload(AssetPackageContext ctx, PluginUnloadReason reason) =>
        ctx.Log.LogInfo("Asset package example: unloading, reason=" + reason);
}

public sealed class AssetPackageContext : PluginContext
{
}
