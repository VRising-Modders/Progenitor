using System;
using System.Text.Json;
using Progenitor.Models;

try
{
    if (args.Length == 4 && (args[0] == "build" || args[0] == "build-animated"))
    {
        if (args[0] == "build-animated") ModelPackage.BuildAnimated(args[1], args[2], args[3]);
        else ModelPackage.Build(args[1], args[2], args[3]);
        Console.WriteLine(JsonSerializer.Serialize(new { status = "built", directory = args[2], assetId = args[3] }));
    }
    else if (args.Length == 2 && args[0] == "validate")
    {
        var scene = ModelPackage.Load(args[1]);
        Console.WriteLine(JsonSerializer.Serialize(new { status = "validated", meshes = scene.Meshes.Count, nodes = scene.Nodes.Count, materials = scene.Materials.Count, skins = scene.Skins.Count, animations = scene.Animations.ConvertAll(a => a.Name), qualification = "offline-only" }));
    }
    else
    {
        Console.Error.WriteLine("Usage: AssetBuild build|build-animated <source.glb> <new-package-directory> <asset-id> | validate <package-directory>");
        return 2;
    }
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "failed", message = error.Message }));
    return 1;
}
