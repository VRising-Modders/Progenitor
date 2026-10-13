#if !PROGENITOR_TESTS
using System;
using System.Text.Json;
using Progenitor.Content;
using Progenitor.Install;
using ProjectM;
using ProjectM.Network;
using Unity.Entities;

namespace Progenitor.Networking;

/// <summary>Pull-only item quarantine admin channel. Every request is authorized from a freshly read server User.</summary>
internal static class ItemManagementChannel
{
    internal const int EventId = 0x50724971; // PrIq, Core-reserved.
    internal const int MaxBytes = 48 * 1024;
    private static bool _installed;
    private static int _nonce;
    private static readonly string ClientEpoch = Guid.NewGuid().ToString("N");
    private static int _pending;
    private static double _deadline;
    private static Action<ItemManagementStatus?, string>? _answer;
    private static readonly RemoteInstallRateLimiter Reads = new(30, 60);
    private static readonly RemoteInstallRateLimiter Edits = new(8, 60);
    private static double Now => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    internal sealed class Packet
    {
        public int Version { get; set; } = 1;
        public bool Response { get; set; }
        public int Nonce { get; set; }
        public string Epoch { get; set; } = "";
        public string Owner { get; set; } = "";
        public string Decision { get; set; } = "";
        public string Note { get; set; } = "";
        public ItemManagementStatus? Status { get; set; }
    }

    internal static unsafe void Install()
    {
        if (_installed) return;
        _installed = true;
        if (Plugin.IsServerProcess) CustomEvents.OnReceiveFromClient(EventId, OnRequest);
        else
        {
            CustomEvents.OnReceive(EventId, OnResponse);
            Handshake.ServerContextCleared += Clear;
            Plugin.Frame += Tick;
        }
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        if (Plugin.IsServerProcess) CustomEvents.RemoveClientHandler(EventId);
        else
        {
            CustomEvents.RemoveHandler(EventId);
            Handshake.ServerContextCleared -= Clear;
            Plugin.Frame -= Tick;
            Clear("disconnected");
            Progenitor.Ui.ItemManagementPane.Close();
        }
    }

    internal static void Request(string owner, string decision, Action<ItemManagementStatus?, string> answer)
    {
        if (!JoinAdminClient.IsAdmin) { answer(null, "Only an admin of the joined server may manage saved items."); return; }
        if (_pending != 0) { answer(null, "An item request is already waiting for the server."); return; }
        int nonce = _nonce = _nonce == int.MaxValue ? 1 : _nonce + 1;
        var packet = new Packet { Nonce = nonce, Epoch = ClientEpoch, Owner = owner, Decision = decision };
        _pending = nonce;
        _answer = answer;
        _deadline = Now + 8;
        try
        {
            if (!CustomEvents.SendToServer(EventId, JsonSerializer.SerializeToUtf8Bytes(packet)))
                Complete(null, "The item request could not be sent.");
        }
        catch { Complete(null, "The item request could not be sent."); }
    }

    private static void Clear(string reason)
    {
        Progenitor.Ui.ItemManagementPane.Close();
        Complete(null, "The server connection ended.");
    }
    private static void Tick() { if (_pending != 0 && Now >= _deadline) Complete(null, "No answer from the server. It may not support item management."); }
    private static void Complete(ItemManagementStatus? status, string note)
    {
        var answer = _answer;
        _answer = null;
        _pending = 0;
        answer?.Invoke(status, note);
    }

    private static unsafe Packet? Decode(byte* payload, int length)
    {
        if (payload == null || length <= 0 || length > MaxBytes) return null;
        try { var value = JsonSerializer.Deserialize<Packet>(new ReadOnlySpan<byte>(payload, length)); return value?.Version == 1 ? value : null; }
        catch { return null; }
    }

    private static unsafe void OnResponse(int _, byte* payload, int length)
    {
        var response = Decode(payload, length);
        if (response == null || !ItemManagementPolicy.MatchesResponse(response.Response, response.Nonce, _pending, response.Epoch, ClientEpoch)) return;
        if (response.Status != null && (response.Status.Owners == null || response.Status.Owners.Count > 128)) { Complete(null, "The server returned too many item owners."); return; }
        if (response.Status != null)
            foreach (var owner in response.Status.Owners)
                if (owner == null || owner.OwnerId == null || owner.OwnerId.Length > 256 || owner.Pending < 0 || owner.Unavailable < 0 || owner.Restored < 0)
                { Complete(null, "The server returned invalid item status."); return; }
        Complete(response.Status, response.Note);
    }

    private static unsafe void OnRequest(int _, Entity userEntity, User ignored, byte* payload, int length)
    {
        var request = Decode(payload, length);
        if (request == null || !ItemManagementPolicy.ValidRequest(request.Version, request.Response, request.Nonce, request.Owner, request.Decision) || request.Epoch == null || request.Epoch.Length != 32) return;
        var world = Worlds.Server;
        if (world == null || !world.IsCreated || !world.EntityManager.HasComponent<User>(userEntity)) return;
        var user = world.EntityManager.GetComponentData<User>(userEntity);
        if (!user.IsConnected) return;
        var response = new Packet { Response = true, Nonce = request.Nonce, Epoch = request.Epoch };
        bool edit = request.Decision.Length != 0;
        try
        {
            if (!(edit ? Edits : Reads).TryTake(user.PlatformId, Now)) response.Note = "Too many item requests; try again shortly.";
            else if (ItemManagementPolicy.Refusal(Handshake.UserAnswerStatus(user.Index) == "ok",
                ServerAdminList.AuthorizeShared(user.PlatformId, user.IsAdmin) != RemoteAuthorizedBy.None,
                request.Owner, request.Decision) is { Length: > 0 } refusal) response.Note = refusal;
            else
            {
                if (edit) response.Note = ItemManagementRuntime.Decide(request.Owner, request.Decision);
                response.Status = ItemManagementRuntime.GetStatus();
                if (response.Status.Owners.Count > 128) throw new InvalidOperationException("Too many item owners to show safely.");
            }
        }
        catch (Exception e) { response.Status = null; response.Note = "Item management refused: " + e.Message; }
        if (edit || response.Status == null)
        {
            string line = "ItemManagement actor=" + user.PlatformId + " decision=" + JsonSerializer.Serialize(request.Decision)
                + " owner=" + JsonSerializer.Serialize(request.Owner) + " result=" + JsonSerializer.Serialize(response.Note);
            Plugin.Logger?.LogInfo(line);
            try { SettingsAuditLog.Append(BepInEx.Paths.BepInExRootPath, line); }
            catch (Exception e) { Plugin.Logger?.LogWarning("ItemManagement audit: " + e.Message); }
        }
        var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
        if (bootstrap == null) return;
        response.Note = ItemManagementPolicy.Clip(response.Note, 1024);
        if (response.Status != null) response.Status.Note = ItemManagementPolicy.Clip(response.Status.Note, 2048);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(response);
        if (bytes.Length > MaxBytes) bytes = JsonSerializer.SerializeToUtf8Bytes(new Packet { Response = true, Nonce = request.Nonce, Epoch = request.Epoch, Note = "Item status exceeds the response limit." });
        CustomEvents.SendToUser(bootstrap, user.Index, EventId, new ReadOnlySpan<byte>(bytes));
    }
}
#endif
