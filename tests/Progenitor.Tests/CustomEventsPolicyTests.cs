using System;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FEX4 (R57 findings 3, 4 and 10). The event-id rules, driven rather than pinned.
///
/// <para>FEX2 put both refusals in <c>CustomEvents</c>, which is <c>unsafe</c> and binds ProjectM,
/// Stunlock.Network and Unity, so nothing in this suite could execute them and the only guard was a
/// source pin. R57 finding 4 measured what that was worth: deleting both <c>return;</c> statements
/// after the refusal's log line — turning every refusal into "log an error, then store the handler
/// and claim the id anyway" — passed all twenty-five tests, the one named
/// <c>A_refused_registration_returns_before_it_stores_anything</c> included. The decision and the
/// store now live together in the game-free <c>CustomEventPolicy.cs</c>, so the tests below store,
/// refuse and read back for real; deleting the early-out is no longer possible, because there is no
/// store outside <see cref="EventHandlerTable{THandler}.Register"/> to reach.</para>
/// </summary>
public class CustomEventsPolicyTests
{
    private const string Core = "Progenitor";
    private const int Reserved = 0x50726851;        // "PrhQ", the handshake result
    private const int Mine = 0x536D4E70;            // "SmNp", the Sample's ping

    /// <summary>A plugin. Nothing about it is Core, whatever it is called.</summary>
    private static readonly EventIdOwner ModA = EventIdOwner.Named("ModA");

    private static readonly EventIdOwner ModB = EventIdOwner.Named("ModB");

    /// <summary>
    /// Core, the only way there is to be Core: a flag, set by the one factory whose caller compares
    /// the declaring assembly against <c>typeof(CustomEvents).Assembly</c> by reference.
    /// </summary>
    private static readonly EventIdOwner CoreOwner = EventIdOwner.ForCore(Core);

    private static EventHandlerTable<Action> Table(string side = "client") =>
        new EventHandlerTable<Action>(side);

    /// <summary>
    /// A fresh delegate instance per call. The capture matters: a lambda that closes over nothing
    /// is cached by the compiler in a static field, so <c>() => { }</c> would hand every caller the
    /// same instance and the reference comparisons below would all read as "the same handler".
    /// </summary>
    private static Action Handler()
    {
        object tag = new object();
        return () => GC.KeepAlive(tag);
    }

    // ---- finding 4: a refusal stores nothing, an acceptance stores both ------------------------

    [Fact]
    public void An_accepted_registration_stores_the_handler_and_owns_the_id()
    {
        var table = Table();
        var handler = Handler();
        var owner = EventIdOwner.Named("ModA");

        Assert.Equal(EventRegistrationResult.Stored, table.Register(Mine, handler, owner, out string? log));

        Assert.Null(log);
        Assert.True(table.Owns(Mine));
        Assert.True(table.TryGetHandler(Mine, out var stored));
        Assert.Same(handler, stored);
        Assert.True(table.TryGetOwner(Mine, out var held));
        Assert.Equal(owner, held);
        Assert.Equal(1, table.Count);
    }

    [Fact]
    // The reserved-range refusal. Nothing stored means the caller's unload handle is a no-op and
    // the id never reaches Bootstrap's owned set - an id in that set with no handler behind it has
    // its fourcc dropped rather than passed to the game.
    public void A_reserved_id_from_a_plugin_stores_no_handler_and_owns_no_id()
    {
        var table = Table();

        Assert.Equal(
            EventRegistrationResult.Refused,
            table.Register(Reserved, Handler(), EventIdOwner.Named("ModA"), out string? log));

        Assert.NotNull(log);
        Assert.Contains("0x50726851", log);
        Assert.Contains("'ModA'", log);
        Assert.Contains("Progenitor's own range", log);
        Assert.False(table.Owns(Reserved));
        Assert.False(table.TryGetHandler(Reserved, out _));
        Assert.False(table.TryGetOwner(Reserved, out _));
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void Core_may_register_inside_the_reserved_range()
    {
        var table = Table();

        Assert.Equal(
            EventRegistrationResult.Stored,
            table.Register(Reserved, Handler(), EventIdOwner.ForCore(Core), out string? log));

        Assert.Null(log);
        Assert.True(table.Owns(Reserved));
    }

    [Fact]
    // Refusal two, and the half that matters: the first handler is still the one that runs.
    public void An_id_another_assembly_owns_is_refused_and_the_first_handler_survives()
    {
        var table = Table("server");
        var first = Handler();
        table.Register(Mine, first, EventIdOwner.Named("ModA"), out _);

        Assert.Equal(
            EventRegistrationResult.Refused,
            table.Register(Mine, Handler(), EventIdOwner.Named("ModB"), out string? log));

        Assert.NotNull(log);
        Assert.Contains("'ModA' already owns it", log);
        Assert.Contains("server handler", log);
        Assert.True(table.TryGetHandler(Mine, out var stored));
        Assert.Same(first, stored);
    }

    [Fact]
    // A hot reload comes back from a fresh collectible assembly with the same simple name, so it
    // is the same owner and replaces its own handler.
    public void The_same_owner_replaces_its_own_handler()
    {
        var table = Table();
        table.Register(Mine, Handler(), EventIdOwner.Named("ModA"), out _);
        var second = Handler();

        Assert.Equal(
            EventRegistrationResult.ReplacedOwn,
            table.Register(Mine, second, EventIdOwner.Named("ModA"), out string? log));

        Assert.NotNull(log);
        Assert.Contains("replacing it", log);
        Assert.True(table.TryGetHandler(Mine, out var stored));
        Assert.Same(second, stored);
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void Registering_the_identical_delegate_again_is_not_a_replacement()
    {
        var table = Table();
        var handler = Handler();
        table.Register(Mine, handler, EventIdOwner.Named("ModA"), out _);

        Assert.Equal(
            EventRegistrationResult.Stored,
            table.Register(Mine, handler, EventIdOwner.Named("ModA"), out string? log));
        Assert.Null(log);
    }

    [Fact]
    // Unloading plugin A must not silence plugin B. Remove-by-handler only removes its own.
    public void Remove_by_handler_leaves_a_later_registrant_alone()
    {
        var table = Table();
        var first = Handler();
        var second = Handler();
        table.Register(Mine, first, EventIdOwner.Named("ModA"), out _);
        table.Register(Mine, second, EventIdOwner.Named("ModA"), out _);

        Assert.False(table.Remove(Mine, first, ModA, out _));
        Assert.True(table.Owns(Mine));

        Assert.True(table.Remove(Mine, second, ModA, out _));
        Assert.False(table.Owns(Mine));
        Assert.False(table.TryGetOwner(Mine, out _));
    }

    [Fact]
    public void Remove_drops_the_owner_with_the_handler_so_the_id_is_free_again()
    {
        var table = Table();
        table.Register(Mine, Handler(), EventIdOwner.Named("ModA"), out _);
        Assert.True(table.Remove(Mine, ModA, out _));

        Assert.Equal(
            EventRegistrationResult.Stored,
            table.Register(Mine, Handler(), EventIdOwner.Named("ModB"), out _));
    }

    // ---- finding 10: an owner nothing can name is its own identity -----------------------------

    [Fact]
    // Both un-attributable owners used to be the name "", which compared equal, so the second one
    // replaced the first with the old warning - the one-owner rule off for exactly the delegates
    // nothing can name.
    public void Two_un_attributable_owners_cannot_replace_each_other()
    {
        var table = Table();
        var first = Handler();
        var second = Handler();
        table.Register(Mine, first, EventIdOwner.Anonymous(first), out _);

        Assert.Equal(
            EventRegistrationResult.Refused,
            table.Register(Mine, second, EventIdOwner.Anonymous(second), out string? log));

        Assert.NotNull(log);
        Assert.Contains("<unknown assembly>", log);
        Assert.True(table.TryGetHandler(Mine, out var stored));
        Assert.Same(first, stored);
    }

    [Fact]
    public void An_un_attributable_owner_still_replaces_its_own_registration()
    {
        var table = Table();
        var handler = Handler();
        table.Register(Mine, handler, EventIdOwner.Anonymous(handler), out _);

        Assert.Equal(
            EventRegistrationResult.Stored,
            table.Register(Mine, handler, EventIdOwner.Anonymous(handler), out _));
    }

    [Fact]
    public void An_un_attributable_owner_cannot_take_an_id_a_named_owner_holds()
    {
        var table = Table();
        table.Register(Mine, Handler(), EventIdOwner.Named("ModA"), out _);
        var late = Handler();

        Assert.Equal(
            EventRegistrationResult.Refused,
            table.Register(Mine, late, EventIdOwner.Anonymous(late), out _));
    }

    [Fact]
    // The reserved half was already safe - "" never equaled the Core name - and stays safe.
    public void An_un_attributable_owner_is_never_core()
    {
        var table = Table();
        var handler = Handler();

        Assert.Equal(
            EventRegistrationResult.Refused,
            table.Register(Reserved, handler, EventIdOwner.Anonymous(handler), out _));
    }

    [Fact]
    public void A_named_owner_is_the_same_owner_across_a_hot_reload()
    {
        Assert.Equal(EventIdOwner.Named("ModA"), EventIdOwner.Named("ModA"));
        Assert.NotEqual(EventIdOwner.Named("ModA"), EventIdOwner.Named("ModB"));
        Assert.Equal(
            EventIdOwner.Named("ModA").GetHashCode(),
            EventIdOwner.Named("ModA").GetHashCode());
    }

    [Fact]
    public void An_owner_reads_as_its_assembly_name_or_unknown()
    {
        Assert.Equal("ModA", EventIdOwner.Named("ModA").Describe());
        Assert.True(EventIdOwner.Named("ModA").IsAttributed);

        var anonymous = EventIdOwner.Anonymous(new object());
        Assert.Equal("<unknown assembly>", anonymous.Describe());
        Assert.False(anonymous.IsAttributed);
        Assert.Equal("", anonymous.Name);
        Assert.NotEqual(anonymous, EventIdOwner.Anonymous(new object()));
    }

    [Fact]
    // The line the dispatcher writes once per generation when it could not name a registrant.
    public void The_unattributed_note_names_the_id_and_the_consequence()
    {
        string line = CustomEventPolicy.UnattributedOwnerLog(Mine, "client");
        Assert.Contains("0x536D4E70", line);
        Assert.Contains("client handler", line);
        Assert.Contains("its own owner", line);
    }

    [Fact]
    public void The_unattributed_note_is_gated_to_once_per_generation()
    {
        CustomEventPolicy.ResetLogGates();
        try
        {
            Assert.True(CustomEventPolicy.NoteUnattributedOwner());
            Assert.False(CustomEventPolicy.NoteUnattributedOwner());
        }
        finally { CustomEventPolicy.ResetLogGates(); }
    }

    // ---- finding 3: the range on the way out ---------------------------------------------------

    [Fact]
    // The honest case FEX2 was written for: a mod picks 0x50726D4D because it looked distinctive,
    // is told its OnReceive was refused, and its SendToAll on the same id used to reach every
    // connected client's ModManifest handler anyway.
    public void A_plugin_send_inside_the_reserved_range_is_refused()
    {
        string? log = CustomEventPolicy.RefuseSend(0x50726D4D, "ModA", senderIsCore: false);

        Assert.NotNull(log);
        Assert.Contains("refusing to send", log);
        Assert.Contains("0x50726D4D", log);
        Assert.Contains("'ModA'", log);
        Assert.Contains("Progenitor's own range", log);
    }

    [Fact]
    public void Cores_own_sends_are_unaffected()
    {
        Assert.Null(CustomEventPolicy.RefuseSend(Reserved, Core, senderIsCore: true));
        Assert.Null(CustomEventPolicy.RefuseSend(0x50726D4D, Core, senderIsCore: true));
    }

    [Fact]
    public void A_send_outside_the_range_is_allowed_for_anyone()
    {
        Assert.Null(CustomEventPolicy.RefuseSend(Mine, "ModA", senderIsCore: false));
        Assert.Null(CustomEventPolicy.RefuseSend(Mine, "", senderIsCore: false));
        Assert.Null(CustomEventPolicy.RefuseSend(0x4D794D64, "ModA", senderIsCore: false));   // "MyMd"
    }

    [Fact]
    // A sender nothing could name is not Core, so the reserved range is closed to it as well.
    public void A_send_from_an_unnamed_caller_inside_the_range_is_refused()
    {
        string? log = CustomEventPolicy.RefuseSend(Reserved, "", senderIsCore: false);
        Assert.NotNull(log);
        Assert.Contains("<unknown assembly>", log);
    }

    [Fact]
    public void The_send_refusal_is_logged_once_per_id_per_generation()
    {
        CustomEventPolicy.ResetLogGates();
        try
        {
            Assert.True(CustomEventPolicy.NoteSendRefusal(Reserved));
            Assert.False(CustomEventPolicy.NoteSendRefusal(Reserved));
            Assert.True(CustomEventPolicy.NoteSendRefusal(0x50726D4D));
            Assert.False(CustomEventPolicy.NoteSendRefusal(0x50726D4D));

            CustomEventPolicy.ResetLogGates();
            Assert.True(CustomEventPolicy.NoteSendRefusal(Reserved));
        }
        finally { CustomEventPolicy.ResetLogGates(); }
    }

    [Fact]
    // Both refusals quote the same range sentence, so an author who hits either one is told the
    // same thing about where their ids belong.
    public void Both_refusals_point_at_the_authors_own_four_letter_tag()
    {
        var table = Table();
        table.Register(Reserved, Handler(), EventIdOwner.Named("ModA"), out string? registration);
        string? send = CustomEventPolicy.RefuseSend(Reserved, "ModA", senderIsCore: false);

        Assert.NotNull(registration);
        Assert.NotNull(send);
        Assert.Contains("your own four-letter tag", registration);
        Assert.Contains("your own four-letter tag", send);
    }

    // ---- R58 finding 12: Core is an identity, not a name ---------------------------------------

    [Fact]
    // The whole finding, driven. Before FEX6 both halves of rule 1 compared the simple name against
    // "Progenitor", so a fork built from this tree with the project file unchanged - or a mod
    // shipping a private copy of Progenitor.dll - registered on PrhQ and was accepted.
    public void A_plugin_that_calls_itself_Progenitor_is_refused_inside_the_reserved_range()
    {
        var table = Table();
        var impostor = EventIdOwner.Named(Core);

        Assert.False(impostor.IsCore);
        Assert.Equal(
            EventRegistrationResult.Refused,
            table.Register(Reserved, Handler(), impostor, out string? log));

        Assert.NotNull(log);
        Assert.Contains("'Progenitor'", log);
        Assert.Contains("Progenitor's own range", log);
        Assert.False(table.Owns(Reserved));
    }

    [Fact]
    // And the send half, which the plugin facade reaches with a name it never gets to interpret.
    public void A_send_from_a_plugin_named_Progenitor_is_refused_inside_the_range()
    {
        string? log = CustomEventPolicy.RefuseSend(Reserved, Core, senderIsCore: false);

        Assert.NotNull(log);
        Assert.Contains("refusing to send", log);
        Assert.Contains("'Progenitor'", log);
    }

    [Fact]
    // Core keeps the range, and its name is not what buys it: ForCore("anything") is Core and
    // Named("Progenitor") is not.
    public void Core_ness_comes_from_the_factory_and_not_from_the_name()
    {
        Assert.True(EventIdOwner.ForCore(Core).IsCore);
        Assert.True(EventIdOwner.ForCore("SomethingElse").IsCore);
        Assert.False(EventIdOwner.Named(Core).IsCore);
        Assert.False(EventIdOwner.Anonymous(new object()).IsCore);

        var table = Table();
        Assert.Equal(
            EventRegistrationResult.Stored,
            table.Register(Reserved, Handler(), EventIdOwner.ForCore("SomethingElse"), out _));
    }

    [Fact]
    // Rule 2 is untouched: it asks "the same registrant?", which is by simple name so a hot reload
    // still is. Core-ness is not part of that comparison in either direction.
    public void The_core_flag_does_not_change_who_equals_whom()
    {
        Assert.Equal(EventIdOwner.Named(Core), CoreOwner);
        Assert.Equal(CoreOwner, EventIdOwner.Named(Core));
        Assert.Equal(CoreOwner.GetHashCode(), EventIdOwner.Named(Core).GetHashCode());
        Assert.NotEqual(CoreOwner, ModA);
    }

    // ---- R58 finding 1: the third direction, removal -------------------------------------------

    [Fact]
    // The rule-2 bypass. Removing dropped the owner row with the handler, so the very next
    // registration on that id saw currentOwner == null and was accepted: two public lines to take
    // an id the one-owner rule refuses head-on.
    public void A_plugin_cannot_remove_an_id_another_plugin_owns()
    {
        var table = Table();
        var theirs = Handler();
        table.Register(Mine, theirs, ModA, out _);

        Assert.False(table.Remove(Mine, ModB, out string? log));

        Assert.NotNull(log);
        Assert.Contains("refusing to remove", log);
        Assert.Contains("0x536D4E70", log);
        Assert.Contains("'ModB'", log);
        Assert.Contains("'ModA' owns it", log);
        Assert.True(table.TryGetHandler(Mine, out var still));
        Assert.Same(theirs, still);
        Assert.True(table.TryGetOwner(Mine, out _));
    }

    [Fact]
    // And having been refused the removal, the id is still not available.
    public void A_refused_removal_leaves_the_id_unavailable_to_the_remover()
    {
        var table = Table();
        table.Register(Mine, Handler(), ModA, out _);
        table.Remove(Mine, ModB, out _);

        Assert.Equal(
            EventRegistrationResult.Refused,
            table.Register(Mine, Handler(), ModB, out string? log));
        Assert.NotNull(log);
        Assert.Contains("'ModA' already owns it", log);
    }

    [Fact]
    // The destructive half, and the worse one: a plugin could silence Core's prefab-definition or
    // handshake-response handler for the rest of the generation with one argument-free call, then
    // neither register nor send on that id. The fourcc stays routed and the packet is dropped on
    // the miss, so nothing says what happened.
    public void A_plugin_cannot_remove_a_core_protocol_handler()
    {
        var table = Table();
        var coreHandler = Handler();
        table.Register(Reserved, coreHandler, CoreOwner, out _);

        Assert.False(table.Remove(Reserved, ModA, out string? log));
        Assert.NotNull(log);
        Assert.Contains("'Progenitor' owns it", log);

        Assert.False(table.Remove(Reserved, coreHandler, ModA, out string? matched));
        Assert.NotNull(matched);

        Assert.True(table.TryGetHandler(Reserved, out var still));
        Assert.Same(coreHandler, still);
    }

    [Fact]
    // Nor by presenting the registered delegate: holding the reference is a proof of ownership only
    // when the holder is the owner.
    //
    // R59 finding 2, job FEX8: this assertion was true of the table and false of the framework.
    // CustomEvents handed this overload OwnerOf(handler) - the same function that computed the
    // recorded owner from that same delegate at registration - so ModB here was a combination no
    // production call could construct, and the real check compared a value against itself. The
    // entry points now attribute the caller, which is what makes the pair below reachable: plugin
    // B, holding plugin A's handler, calling RemoveHandler(id, itsHandler).
    public void Handler_matched_removal_is_owner_checked_too()
    {
        var table = Table("server");
        var theirs = Handler();
        table.Register(Mine, theirs, ModA, out _);

        Assert.False(table.Remove(Mine, theirs, ModB, out string? log));
        Assert.NotNull(log);
        Assert.Contains("server handler", log);
        Assert.True(table.Owns(Mine));

        Assert.True(table.Remove(Mine, theirs, ModA, out string? own));
        Assert.Null(own);
        Assert.False(table.Owns(Mine));
    }

    [Fact]
    // The scenario, end to end: A registers, B holds A's delegate (a shared registry, a published
    // field, an event payload) and presents it. Before FEX8 this returned true, removed the row,
    // wrote no log line, and left the id free for B's own OnReceive - rule 2 walked around one
    // indirection further out than R58 finding 1 closed.
    public void Holding_another_plugins_delegate_is_not_a_second_way_in()
    {
        var table = Table();
        var theirs = Handler();
        table.Register(Mine, theirs, ModA, out _);

        Assert.False(table.Remove(Mine, theirs, ModB, out string? refused));
        Assert.NotNull(refused);
        Assert.Contains("'ModA' owns it", refused);

        // And the id is still not available to B afterwards.
        Assert.Equal(EventRegistrationResult.Refused, table.Register(Mine, Handler(), ModB, out _));
        Assert.True(table.TryGetHandler(Mine, out var still));
        Assert.Same(theirs, still);
    }

    [Fact]
    // The other half of the same change (R59 finding 7): one attribution for both overloads. A
    // handler declared in a library the plugin ships is owned by the library, on the id-only form
    // and on the handler-matched one alike - so the answer is the same either way, instead of
    // "refused" through one door and "removed" through the other for one removal.
    public void Both_removal_forms_give_the_same_answer_for_one_remover()
    {
        var shipped = EventIdOwner.Named("Shared");

        var byId = Table();
        var handler = Handler();
        byId.Register(Mine, handler, shipped, out _);
        Assert.False(byId.Remove(Mine, ModA, out string? one));
        Assert.NotNull(one);

        var byHandler = Table();
        byHandler.Register(Mine, handler, shipped, out _);
        Assert.False(byHandler.Remove(Mine, handler, ModA, out string? two));
        Assert.NotNull(two);

        // Both refusals name the same owner, and the library itself gets through on both.
        Assert.Contains("'Shared' owns it", one);
        Assert.Contains("'Shared' owns it", two);
        Assert.True(byId.Remove(Mine, shipped, out _));
        Assert.True(byHandler.Remove(Mine, handler, shipped, out _));
    }

    [Fact]
    // A wrong handler is the quiet case - a later registrant took the id, unloading plugin A must
    // not silence plugin B - and stays quiet: no refusal line, because nobody did anything wrong.
    public void A_handler_that_is_no_longer_registered_removes_nothing_and_says_nothing()
    {
        var table = Table();
        var first = Handler();
        var second = Handler();
        table.Register(Mine, first, ModA, out _);
        table.Register(Mine, second, ModA, out _);

        Assert.False(table.Remove(Mine, first, ModA, out string? log));
        Assert.Null(log);
        Assert.True(table.Owns(Mine));
    }

    [Fact]
    public void An_owner_may_remove_its_own_id_and_free_it()
    {
        var table = Table();
        table.Register(Mine, Handler(), ModA, out _);

        Assert.True(table.Remove(Mine, ModA, out string? log));
        Assert.Null(log);
        Assert.False(table.Owns(Mine));

        Assert.Equal(EventRegistrationResult.Stored, table.Register(Mine, Handler(), ModB, out _));
    }

    [Fact]
    // A hot reload comes back from a fresh collectible assembly with the same simple name, so its
    // teardown can still drop the rows its previous generation registered.
    public void A_hot_reload_may_remove_the_previous_generations_handler()
    {
        var table = Table();
        table.Register(Mine, Handler(), EventIdOwner.Named("ModA"), out _);

        Assert.True(table.Remove(Mine, EventIdOwner.Named("ModA"), out _));
    }

    [Fact]
    // Core may clean up anything - Uninstall tears the whole table down, and Core's own modules
    // remove their protocol ids by id.
    public void Core_may_remove_any_handler()
    {
        var table = Table();
        table.Register(Mine, Handler(), ModA, out _);

        Assert.True(table.Remove(Mine, CoreOwner, out string? log));
        Assert.Null(log);
        Assert.False(table.Owns(Mine));
    }

    [Fact]
    // Nothing to protect and nothing to remove: not a refusal, just false.
    public void Removing_an_id_nobody_holds_is_not_a_refusal()
    {
        var table = Table();

        Assert.False(table.Remove(Mine, ModA, out string? log));
        Assert.Null(log);
    }

    [Fact]
    // An un-attributable holder is its own identity per registration - a delegate instance - and
    // no calling assembly can ever equal one, so Core is the only remover it has. That is the
    // honest end of "nothing can name it", and the tracked path still works because PluginNet's
    // unload disposable calls from Core.
    //
    // R59 finding 2, job FEX8: this test used to end by removing with EventIdOwner.Anonymous(the
    // handler) as the remover, and that is the deleted double - a remover only the tautology could
    // build. CustomEvents never constructed it; it derived the remover from the presented handler,
    // which is why the check could not refuse. The removal entry points attribute the caller now,
    // and a caller is an assembly.
    public void An_un_attributable_owner_can_be_removed_only_by_core()
    {
        var table = Table();
        var handler = Handler();
        table.Register(Mine, handler, EventIdOwner.Anonymous(handler), out _);

        Assert.False(table.Remove(Mine, ModA, out string? refused));
        Assert.NotNull(refused);
        Assert.Contains("<unknown assembly>", refused);

        // Nor by presenting the delegate: the remover is still an assembly, and no assembly is it.
        Assert.False(table.Remove(Mine, handler, ModA, out string? matched));
        Assert.NotNull(matched);
        Assert.True(table.Owns(Mine));

        Assert.True(table.Remove(Mine, handler, CoreOwner, out string? log));
        Assert.Null(log);
        Assert.False(table.Owns(Mine));
    }

    [Fact]
    public void The_removal_refusal_is_logged_once_per_id_per_generation()
    {
        CustomEventPolicy.ResetLogGates();
        try
        {
            Assert.True(CustomEventPolicy.NoteRemovalRefusal(Mine));
            Assert.False(CustomEventPolicy.NoteRemovalRefusal(Mine));
            Assert.True(CustomEventPolicy.NoteRemovalRefusal(Reserved));

            CustomEventPolicy.ResetLogGates();
            Assert.True(CustomEventPolicy.NoteRemovalRefusal(Mine));
        }
        finally { CustomEventPolicy.ResetLogGates(); }
    }

    [Fact]
    // The removal gate and the send gate count separately: being told once that you cannot send on
    // an id must not use up the line that would tell you why your removal did nothing.
    public void The_removal_gate_and_the_send_gate_are_separate_books()
    {
        CustomEventPolicy.ResetLogGates();
        try
        {
            Assert.True(CustomEventPolicy.NoteSendRefusal(Reserved));
            Assert.True(CustomEventPolicy.NoteRemovalRefusal(Reserved));
        }
        finally { CustomEventPolicy.ResetLogGates(); }
    }

    // ---- R59 finding 8: the whole-table erasure ------------------------------------------------

    [Fact]
    // FEX6's structural claim was "there is no removal path that skips the decision: this method
    // and its handler-matched sibling are the only things that erase a row". Clear() was a third
    // one and erased all of them, with no decision at all, eleven lines below the sentence. Both
    // of its callers were Core and correct, so nothing was exploitable - but the file whose whole
    // point is that its refusals are driven rather than pinned had an undriven erasure in it.
    public void Only_core_may_erase_the_whole_table()
    {
        var table = Table();
        table.Register(Mine, Handler(), ModA, out _);
        table.Register(Reserved, Handler(), CoreOwner, out _);

        Assert.Equal(0, table.ClearForUninstall(ModA, out string? refused));
        Assert.NotNull(refused);
        Assert.Contains("refusing to clear the whole client handler table", refused);
        Assert.Contains("'ModA'", refused);
        Assert.Equal(2, table.Count);
        Assert.True(table.Owns(Mine));

        Assert.Equal(2, table.ClearForUninstall(CoreOwner, out string? log));
        Assert.Null(log);
        Assert.Equal(0, table.Count);
        Assert.False(table.TryGetOwner(Mine, out _));
    }

    [Fact]
    // An owner that happens to be called Progenitor is not Core here either: IsCore is a flag only
    // ForCore sets, and its one caller compares assemblies by reference (R58 finding 12).
    public void A_plugin_named_progenitor_cannot_erase_the_table()
    {
        var table = Table();
        table.Register(Mine, Handler(), EventIdOwner.Named(Core), out _);

        Assert.Equal(0, table.ClearForUninstall(EventIdOwner.Named(Core), out string? refused));
        Assert.NotNull(refused);
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void The_clear_refusal_is_decided_in_the_policy_and_points_at_the_per_id_path()
    {
        Assert.Null(CustomEventPolicy.RefuseClear(CoreOwner, "server"));

        string? log = CustomEventPolicy.RefuseClear(ModB, "server");
        Assert.NotNull(log);
        Assert.Contains("server handler table", log);
        Assert.Contains("'ModB'", log);
        Assert.Contains("one at a time", log);
    }

    [Fact]
    // The refusal says what the caller lost, not just that it was refused.
    public void The_removal_refusal_names_both_parties_and_the_consequence()
    {
        string? log = CustomEventPolicy.RefuseRemoval(
            Reserved, EventIdOwner.ForCore(Core), ModA, "client");

        Assert.NotNull(log);
        Assert.Contains("0x50726851", log);
        Assert.Contains("client handler", log);
        Assert.Contains("'ModA'", log);
        Assert.Contains("'Progenitor' owns it", log);
        Assert.Contains("silence it", log);
    }
}
