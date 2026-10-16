// SPDX-License-Identifier: MIT
using Progenitor.Networking;
using Progenitor.Testing;
using Xunit;

namespace ProgenitorMod.Tests;

public class ModRulesTests
{
    [Fact]
    public void A_refusal_reads_as_one_sentence_with_its_reason() =>
        Assert.Equal("The server said no: busy.", ModRules.Describe(RequestStatus.Refused, " busy "));

    [Fact]
    public void The_greeting_types_are_a_valid_request_pair() =>
        ExchangeCompat.AssertRequestPair<GreetingRequest, GreetingReply>();

    [Fact]
    public void The_server_answers_with_its_greeting_through_the_real_exchange()
    {
        var call = ExchangeCompat.Call<GreetingRequest, GreetingReply>(
            new GreetingRequest { ClientVersion = "1.0.0" },
            (request, info) => ModRules.Answer(request, "Hello", "1.0.0"));
        Assert.True(call.Ok, call.ToString());
        Assert.Equal("Hello", call.Reply!.Text);
    }

    [Fact]
    public void An_empty_greeting_is_refused_with_a_reason()
    {
        var call = ExchangeCompat.Call<GreetingRequest, GreetingReply>(
            new GreetingRequest(), (request, info) => ModRules.Answer(request, "  ", "1.0.0"));
        Assert.Equal(RequestStatus.Refused, call.Status);
        Assert.Equal("the server has no greeting set", call.Reason);
    }

    [Fact]
    public void A_newer_peer_s_extra_fields_are_skipped() =>
        MessageCompat.AssertSkipsUnknownFields(new GreetingReply { Text = "Hi", ServerVersion = "1.1.0" });

    [Fact]
    public void Long_greetings_are_cut_to_the_limit() =>
        Assert.Equal(ModRules.MaxGreetingLength,
            ModRules.Answer(new GreetingRequest(), new string('a', 500), "1.0.0").Text.Length);
}
