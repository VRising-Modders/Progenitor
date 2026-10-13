using System.Text.Json;
using Progenitor.Cameras;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Debug-bridge <c>photo</c>: photo mode through <see cref="PhotoModeBridge"/>. <c>on</c> / <c>off</c> run
/// on the client (main thread); <c>status</c> answers on either side. <c>on</c> with <c>enable: true</c>
/// turns <c>[PhotoMode] Enabled</c> on first. Every action answers with the status after it.
/// </summary>
internal static class PhotoOps
{
    public static object Run(JsonElement args)
    {
        var request = PhotoArgs.Parse(args);
        return PhotoModeBridge.Run(request.Action, request.Enable, request.EntityIndex, request.EntityVersion);
    }
}
