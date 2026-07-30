using System.Text;
using Launcher.Core.GameControl;
using Xunit;

namespace Launcher.Tests;

/// <summary>RFC 1320 known-answer vectors.
///
/// This is the test to run first if any of these are ever run in isolation. MD4 is hand-written here
/// because the BCL dropped it and DarkPlaces srcon needs it, and a wrong MD4 does not throw: it
/// produces a well-formed packet that the server silently refuses, which looks like a wrong password
/// and sends whoever is debugging it in the wrong direction for an afternoon.</summary>
public class Md4Tests
{
    [Theory]
    [InlineData("", "31d6cfe0d16ae931b73c59d7e0c089c0")]
    [InlineData("a", "bde52cb31de33e46245e05fbdbd6fb24")]
    [InlineData("abc", "a448017aaf21d8525fc10ae87aa6729d")]
    [InlineData("message digest", "d9130a8164549fe818874806e1c7014b")]
    [InlineData("abcdefghijklmnopqrstuvwxyz", "d79e1c308aa5bbcdeea8ed63df412da9")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789",
        "043f8582f241db351ce627e153e7f0e4")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345678901234567890",
        "e33b4ddc9c38f2199c3e7b164fcc0536")]
    public void Rfc1320_vectors(string input, string expected)
    {
        var digest = Md4.Hash(Encoding.ASCII.GetBytes(input));
        Assert.Equal(expected, Convert.ToHexString(digest).ToLowerInvariant());
    }

    /// <summary>Padding boundaries: a message exactly one block, and one that forces a second block
    /// purely for the length field. Both are where a hand-written pad goes wrong.</summary>
    [Theory]
    [InlineData(55)]
    [InlineData(56)]
    [InlineData(57)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    public void Padding_boundaries_produce_a_digest(int length)
    {
        var digest = Md4.Hash(Encoding.ASCII.GetBytes(new string('x', length)));
        Assert.Equal(16, digest.Length);
    }

    [Fact]
    public void Hmac_is_keyed()
    {
        var a = Md4.Hmac("secret"u8, "challenge status"u8);
        var b = Md4.Hmac("other"u8, "challenge status"u8);

        Assert.Equal(16, a.Length);
        Assert.NotEqual(Convert.ToHexString(a), Convert.ToHexString(b));
    }

    /// <summary>A key longer than the 64-byte block is hashed first (RFC 2104). Getting this wrong
    /// only shows up for operators with long rcon passwords.</summary>
    [Fact]
    public void Hmac_handles_a_key_longer_than_the_block()
    {
        var longKey = Encoding.ASCII.GetBytes(new string('k', 100));
        var mac = Md4.Hmac(longKey, "challenge status"u8);
        Assert.Equal(16, mac.Length);
    }
}

/// <summary>The srcon packet is binary: a raw 16-byte HMAC sits mid-payload, so it cannot be built by
/// string concatenation. These assert the byte layout the game's RconProtocol parses.</summary>
public class RconClientTests
{
    [Fact]
    public void Challenge_request_has_the_layout_the_server_parses()
    {
        var packet = RconClient.BuildChallengeRequest("pw", "CHAL123", "status");

        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, packet[..4]);
        Assert.Equal("srcon HMAC-MD4 CHALLENGE ", Encoding.ASCII.GetString(packet, 4, 25));

        // 4 OOB + 25 prefix + 16 HMAC, then a single space before the hashed tail.
        Assert.Equal((byte)' ', packet[4 + 25 + 16]);
        Assert.Equal("CHAL123 status", Encoding.ASCII.GetString(packet, 4 + 25 + 16 + 1,
            packet.Length - (4 + 25 + 16 + 1)));
    }

    /// <summary>The HMAC covers exactly "&lt;challenge&gt; &lt;command&gt;", which is what DP hashes.
    /// Any other span verifies on neither side.</summary>
    [Fact]
    public void Challenge_hmac_covers_the_trailing_string()
    {
        var packet = RconClient.BuildChallengeRequest("pw", "CHAL123", "status");
        var embedded = packet[(4 + 25)..(4 + 25 + 16)];
        var expected = Md4.Hmac("pw"u8, Encoding.UTF8.GetBytes("CHAL123 status"));

        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(embedded));
    }

    [Fact]
    public void Time_request_uses_the_shorter_prefix()
    {
        var packet = RconClient.BuildTimeRequest("pw", 1700000000, "status");
        Assert.Equal("srcon HMAC-MD4 TIME ", Encoding.ASCII.GetString(packet, 4, 20));
    }

    [Fact]
    public void Insecure_request_is_plain_text()
    {
        var packet = RconClient.BuildInsecureRequest("pw", "status");
        Assert.Equal("rcon pw status", Encoding.ASCII.GetString(packet, 4, packet.Length - 4));
    }

    /// <summary>The server refuses these even with a valid password, so refusing locally turns a
    /// silent no-op into an error the caller can see.</summary>
    [Theory]
    [InlineData("status; quit")]
    [InlineData("status\nquit")]
    [InlineData("say hidden")]
    public void Command_injection_attempts_are_refused(string command)
    {
        Assert.False(RconClient.IsCommandSafe(command));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("say hello world")]
    [InlineData("kick 3")]
    public void Ordinary_commands_are_allowed(string command)
    {
        Assert.True(RconClient.IsCommandSafe(command));
    }
}

public class InfoStringTests
{
    [Fact]
    public void Parses_key_value_pairs()
    {
        var map = InfoString.Parse(@"\hostname\Test Server\mapname\stormkeep\clients\5");

        Assert.Equal("Test Server", map["hostname"]);
        Assert.Equal("stormkeep", map["mapname"]);
        Assert.Equal("5", map["clients"]);
    }

    [Fact]
    public void Keys_are_case_insensitive()
    {
        var map = InfoString.Parse(@"\HostName\x");
        Assert.Equal("x", map["hostname"]);
    }

    /// <summary>A trailing key with no value is a server reporting an unset cvar. Dropping it would
    /// look like the key was never sent, which is a different thing.</summary>
    [Fact]
    public void Trailing_key_without_a_value_is_kept_as_empty()
    {
        var map = InfoString.Parse(@"\hostname\x\gametype");
        Assert.True(map.ContainsKey("gametype"));
        Assert.Equal("", map["gametype"]);
    }

    /// <summary>DP reports "clients" as everything connected and "bots" separately, so humans are the
    /// difference. Reading "clients" as the human count makes a bot-filled server look busy, and the
    /// notempty filter stops meaning anything.</summary>
    [Fact]
    public void Human_players_exclude_bots()
    {
        var info = ServerInfo.FromInfoString(
            @"\hostname\x\mapname\m\gametype\dm\clients\10\bots\7\sv_maxclients\16");

        Assert.Equal(3, info.Players);
        Assert.Equal(7, info.Bots);
        Assert.Equal(16, info.MaxPlayers);
    }

    [Fact]
    public void Bot_only_server_reports_zero_humans()
    {
        var info = ServerInfo.FromInfoString(@"\clients\8\bots\8\sv_maxclients\8");
        Assert.Equal(0, info.Players);
    }

    [Fact]
    public void Building_rejects_a_backslash_in_a_value()
    {
        Assert.Throws<ArgumentException>(() =>
            InfoString.Build([new KeyValuePair<string, string>("hostname", @"a\b")]));
    }
}

public class EventLogParserTests
{
    [Theory]
    [InlineData(":chat:3:hello there")]
    [InlineData(":chat_team:3:1:on my way")]
    [InlineData(":chat_spec:4:nice shot")]
    [InlineData(":chat_minigame:2:nexball:gg")]
    public void All_four_chat_variants_are_flagged(string line)
    {
        var evt = EventLogParser.Parse(line);

        Assert.NotNull(evt);
        Assert.True(evt!.IsChat);
    }

    /// <summary>The chat flag is what a control plane without the chat-read scope filters on. A kill
    /// line marked as chat, or the reverse, is a privacy bug rather than a display one.</summary>
    [Theory]
    [InlineData(":kill:frag:3:4:type=rocket")]
    [InlineData(":join:3:1:127.0.0.1:player")]
    [InlineData(":gamestart:ctf_stormkeep:42")]
    public void Non_chat_events_are_not_flagged(string line)
    {
        Assert.False(EventLogParser.Parse(line)!.IsChat);
    }

    [Fact]
    public void Ordinary_console_output_is_not_an_event()
    {
        Assert.Null(EventLogParser.Parse("Loading map stormkeep"));
        Assert.Null(EventLogParser.Parse(""));
        Assert.Null(EventLogParser.Parse("not:an:event"));
    }

    [Fact]
    public void Match_state_follows_gamestart_and_gameover()
    {
        var parser = new EventLogParser();
        Assert.False(parser.MatchLive);

        parser.Feed(":gamestart:ctf_stormkeep:42");
        Assert.True(parser.MatchLive);
        Assert.Equal("ctf", parser.Gametype);
        Assert.Equal("stormkeep", parser.Map);
        Assert.NotNull(parser.MatchElapsedSeconds);

        parser.Feed(":gameover:");
        Assert.False(parser.MatchLive);
    }

    /// <summary>A restarted server is not mid-match. Carrying the state across would make the next
    /// release look like it interrupted something, which is exactly the distinction the alert
    /// severity turns on.</summary>
    [Fact]
    public void Reset_clears_match_state()
    {
        var parser = new EventLogParser();
        parser.Feed(":gamestart:dm_aerowalk:1");
        parser.Reset();

        Assert.False(parser.MatchLive);
        Assert.Null(parser.Map);
    }

    /// <summary>Chat containing a colon splits into extra fields, which is the format's fault and not
    /// something callers should have to know. TextFrom rejoins.</summary>
    [Fact]
    public void Chat_text_containing_a_colon_can_be_rejoined()
    {
        var evt = EventLogParser.Parse(":chat:3:check this out: it works")!;
        Assert.Equal("check this out: it works", EventLogParser.TextFrom(evt, 1));
    }
}
