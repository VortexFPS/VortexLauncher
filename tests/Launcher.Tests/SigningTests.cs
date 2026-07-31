using System.Text;
using Launcher.Core.Signing;
using Xunit;

namespace Launcher.Tests;

/// <summary>Ed25519 against the RFC 8032 §7.1 vectors.
///
/// These are not decoration. Launcher.Core carries its own Ed25519 because the BCL has none and the
/// project takes no package references, so the usual reason to trust the primitive — someone else
/// maintains it — does not apply, and known-answer tests are what stands in for it. A failure here
/// means the release-signature check is not doing what it claims, which is worse than not having
/// one, so treat it as a stop-the-line failure rather than something to bisect around.</summary>
public class Ed25519Tests
{
    // secret keys omitted deliberately — this half of the algorithm does not exist in the launcher.
    [Theory]
    // TEST 1: empty message
    [InlineData("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", "",
        "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e397" +
        "01cf9b46bd25bf5f0595bbe24655141438e7a100b")]
    // TEST 2: one byte
    [InlineData("3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c", "72",
        "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f361" +
        "3d0f11d8c387b2eaeb4302aeeb00d291612bb0c00")]
    // TEST 3: two bytes
    [InlineData("fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025", "af82",
        "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f76" +
        "0984dc6594a7c15e9716ed28dc027beceea1ec40a")]
    // TEST SHA(abc): 64-byte message
    [InlineData("ec172b93ad5e563bf4932c70e1245034c35467ef2efd4d64ebf819683467e2bf",
        "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c2" +
        "3a3feebbd454d4423643ce80e2a9ac94fa54ca49f",
        "dc2a4459e7369633a52b1bf277839a00201009a3efbf3ecb69bea2186c26b58909351fc9ac90b3ecfdfbc7c" +
        "66431e0303dca179c138ac17ad9bef1177331a704")]
    public void Rfc8032_vectors_verify(string publicKey, string message, string signature) =>
        Assert.True(Ed25519.Verify(Hex(signature), Hex(message), Hex(publicKey)));

    [Theory]
    [InlineData(10)] // inside R
    [InlineData(40)] // inside S
    public void A_single_flipped_signature_bit_is_rejected(int index)
    {
        var signature = Hex("92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da" +
                            "085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00");
        signature[index] ^= 0x01;
        Assert.False(Ed25519.Verify(signature,
            Hex("72"), Hex("3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c")));
    }

    [Fact]
    public void A_changed_message_is_rejected() =>
        Assert.False(Ed25519.Verify(
            Hex("92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da" +
                "085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00"),
            Hex("7200"),
            Hex("3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c")));

    /// <summary>S is checked against the group order, so the same signature with L added to S —
    /// which satisfies the verification equation just as well — is refused. Without this a third
    /// party can mint a second valid signature for a message they cannot sign, and anything
    /// downstream that treats a signature as an identifier stops being able to tell them apart.</summary>
    [Fact]
    public void A_non_canonical_S_is_rejected()
    {
        var signature = Hex("92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da" +
                            "085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00");
        var order = Hex("edd3f55c1a631258d69cf7a2def9de1400000000000000000000000000000010");
        var carry = 0;
        for (var i = 0; i < 32; i++)
        {
            var sum = signature[32 + i] + order[i] + carry;
            signature[32 + i] = (byte)sum;
            carry = sum >> 8;
        }
        Assert.False(Ed25519.Verify(signature,
            Hex("72"), Hex("3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c")));
    }

    [Theory]
    [InlineData(63, 32)]
    [InlineData(64, 31)]
    public void Wrong_lengths_are_rejected_not_thrown(int signatureLength, int keyLength) =>
        Assert.False(Ed25519.Verify(new byte[signatureLength], [], new byte[keyLength]));

    internal static byte[] Hex(string hex) => Convert.FromHexString(hex);
}

/// <summary>BLAKE2b-512 against RFC 7693's vector plus lengths that exercise the block boundary,
/// which is the part of a hash implementation that actually goes wrong.</summary>
public class Blake2bTests
{
    [Theory]
    [InlineData(0, "786a02f742015903c6c6fd852552d272912f4740e15847618a86e217f71f5419" +
                   "d25e1031afee585313896444934eb04b903a685b1448b755d56f701afe9be2ce")]
    // 128 = exactly one block: the final block must still be the one flagged last, not an extra
    // empty one, and this is the input length that tells the two apart.
    [InlineData(128, "fc6c71f688f43ea7d60817478808f3cac753e61571865c95adbc2d9122c943a7" +
                     "6b92c2cb1047ef3fe7bf6e436ec1d0a99a9e5b216780bf7fed9d7ca91d3a8f3b")]
    [InlineData(129, "55e6e0eb418149a8af92fd9ddc99254781b2f522a131b4f4d984404b71a00e11" +
                     "67b8124d5dcddd4c6977b299392335d6edd303da6d344d74bbef2d38101b232b")]
    public void Digests_a_run_of_a_bytes(int length, string expected) =>
        Assert.Equal(expected,
            Convert.ToHexString(Blake2b.Hash512(Encoding.ASCII.GetBytes(new string('a', length))))
                .ToLowerInvariant());

    [Fact]
    public void Rfc7693_abc_vector() =>
        Assert.Equal(
            "ba80a53f981c4d0d6a2797b69f12f6e94c212f14685ac4b74b12bb6fdbffa2d1" +
            "7d87c5392aab792dc252d5de4533cc9518d38aa8dbf1925ab92386edd4009923",
            Convert.ToHexString(Blake2b.Hash512("abc"u8)).ToLowerInvariant());
}

/// <summary>The minisign container and the signature policy.
///
/// The fixtures below were produced by an independent Ed25519 implementation (python's cryptography
/// package) writing minisign's documented file format, so a pass here means our parser and our
/// verifier agree with something that was not written alongside them. The keypair is throwaway test
/// data generated from a fixed seed — it is NOT a release key and nothing trusts it outside this
/// file.</summary>
public class MinisignTests
{
    private const string PublicKeyFile =
        "untrusted comment: minisign public key 1FE8B442C10A621F\n" +
        "RWQfYgrBQrToHyaX436HAuWxQF8O/VjB8DWvKKSbSmaGPws6aEvPLynJ\n";

    /// <summary>A miniature latest.json. Signed as these exact bytes — do not reformat.</summary>
    private const string Manifest =
        "{\n  \"schema\": 1,\n  \"version\": \"0.2.0\",\n  \"tag\": \"v0.2.0\",\n" +
        "  \"channel\": \"stable\",\n  \"platforms\": {\n    \"windows-x86_64\": {\n" +
        "      \"core\": {\n        \"name\": \"VortexArena-0.2.0-windows-client-core.zip\",\n" +
        "        \"root\": \"windows-client\",\n        \"size\": 41943040,\n" +
        "        \"sha256\": \"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\",\n" +
        "        \"url\": \"https://example.invalid/core.zip\"\n      }\n    }\n  }\n}\n";

    /// <summary>minisign's legacy "Ed" mode: the signature covers the file itself.</summary>
    private const string LegacySignature =
        "untrusted comment: signature from minisign secret key\n" +
        "RWQfYgrBQrToH3rN6d5tIpjzfOdQv/emLVo1wBcs3ODzMUbsO5Ajxx0iuOWFlwFpZ3uA501/c1yGrGYIr9ci5I1jPFdSDUKFcAU=\n" +
        "trusted comment: timestamp:1764460800\tfile:latest.json\n" +
        "8/kSSbruX8pjwbYrmzmQwmzwqaoFVqv4qqsKR8iAbg6nE5KPYCCFM6Nn+cI2br6UnqQV01/SlfBfDhUSs7FYCg==\n";

    /// <summary>minisign's prehashed "ED" mode: the signature covers BLAKE2b-512 of the file. Which
    /// mode a signature uses is the signer's choice, which is why both are read.</summary>
    private const string PrehashedSignature =
        "untrusted comment: signature from minisign secret key\n" +
        "RUQfYgrBQrToH38sJpabbKNX8apyKGro5+IN2q2KRYEoQ7j8DXf1iiNGeQoExxklFiWAVWiMrDK7Cyu87V790wfWRvEefvCqvwo=\n" +
        "trusted comment: timestamp:1764460800\tfile:latest.json\n" +
        "bFHm++hFQVp0aMnGJ7WBnxxvfIHNpBVKtLlZxtwG5l0AZWbm9fBqDyLKSb6vgbo5h4YrXt5GtfHTqNIYvbN3Aw==\n";

    /// <summary>Valid in every structural respect, signed by a key we do not trust.</summary>
    private const string ForeignSignature =
        "untrusted comment: signature from minisign secret key\n" +
        "RWSqu8zd7v8AEW7NjUzYfigVDLOJlDL+AizW9j2PeC0JKn5JAYj+cnzkKLKXcjgl2KZrrOEeT2aS2YybIX2FR+njVVkh8h6suQg=\n" +
        "trusted comment: x\n" +
        "XYk8MsSUas56tO3TGKmun8GU/csfWOYE4duk20Ot/VPlowwYyQxP8RS9ZgDrZY/LNySugUmMUbnl2flp8F8BBw==\n";

    private static readonly byte[] ManifestBytes = Encoding.UTF8.GetBytes(Manifest);
    private static readonly IReadOnlyList<MinisignPublicKey> Trusted = [MinisignPublicKey.Parse(PublicKeyFile)];

    [Fact]
    public void Key_id_is_printed_the_way_minisign_prints_it() =>
        Assert.Equal("1FE8B442C10A621F", Trusted[0].KeyIdHex);

    [Theory]
    [InlineData(LegacySignature)]
    [InlineData(PrehashedSignature)]
    public void Both_signature_modes_verify(string signature) =>
        Assert.True(Minisign.Verify(ManifestBytes, signature, Trusted, out _));

    /// <summary>The file travels over HTTP and through text editors; a CR that got added on the way
    /// is not tampering and must not read as tampering.</summary>
    [Fact]
    public void Crlf_line_endings_still_verify() =>
        Assert.True(Minisign.Verify(ManifestBytes, LegacySignature.Replace("\n", "\r\n"), Trusted, out _));

    [Theory]
    [InlineData(LegacySignature)]
    [InlineData(PrehashedSignature)]
    public void One_changed_byte_in_the_manifest_fails(string signature)
    {
        var tampered = (byte[])ManifestBytes.Clone();
        tampered[^3] ^= 0x20;
        Assert.False(Minisign.Verify(tampered, signature, Trusted, out _));
    }

    /// <summary>The case the whole feature exists for: a well-formed signature from a key that is
    /// not ours is a failure, not a shrug. If this ever returned true, signing would be theatre —
    /// anyone can generate a keypair.</summary>
    [Fact]
    public void A_signature_from_an_untrusted_key_fails()
    {
        Assert.False(Minisign.Verify(ManifestBytes, ForeignSignature, Trusted, out var detail));
        Assert.Contains("unknown key", detail);
    }

    [Fact]
    public void A_signature_with_no_trusted_keys_at_all_fails() =>
        Assert.False(Minisign.Verify(ManifestBytes, LegacySignature, [], out _));

    /// <summary>The trusted comment carries its own signature, so it cannot be rewritten underneath
    /// a valid file signature.</summary>
    [Fact]
    public void A_rewritten_trusted_comment_fails()
    {
        var lines = LegacySignature.Split('\n');
        lines[2] = "trusted comment: timestamp:9999999999\tfile:latest.json";
        Assert.False(Minisign.Verify(ManifestBytes, string.Join('\n', lines), Trusted, out var detail));
        Assert.Contains("trusted comment", detail);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a signature at all")]
    [InlineData("untrusted comment: x\nAAAA\ntrusted comment: y\nAAAA\n")]
    public void Malformed_signature_files_fail_without_throwing(string signature) =>
        Assert.False(Minisign.Verify(ManifestBytes, signature, Trusted, out _));

    public class Policy
    {
        [Fact]
        public void Off_accepts_anything_including_a_bad_signature()
        {
            Check(ManifestSignaturePolicy.Off, null);
            Check(ManifestSignaturePolicy.Off, ForeignSignature);
        }

        /// <summary>Today's shipped behaviour, and the reason the default is not Required: every
        /// release published so far is unsigned.</summary>
        [Fact]
        public void Verify_if_present_accepts_an_unsigned_release() =>
            Check(ManifestSignaturePolicy.VerifyIfPresent, null);

        [Fact]
        public void Verify_if_present_accepts_a_valid_signature() =>
            Assert.Contains("signed by key", Check(ManifestSignaturePolicy.VerifyIfPresent, LegacySignature));

        /// <summary>"If present" governs whether a signature is required, never whether it has to be
        /// correct. A signature that is there and wrong stops the update under every policy but Off.</summary>
        [Fact]
        public void Verify_if_present_still_refuses_a_bad_signature() =>
            Refuse(ManifestSignaturePolicy.VerifyIfPresent, ForeignSignature);

        /// <summary>A launcher too old to hold the signing key cannot check the signature, and
        /// "cannot check" is not "passes".</summary>
        [Fact]
        public void A_signed_release_with_no_key_provisioned_is_refused() =>
            Assert.Throws<ManifestSignatureException>(() => ReleaseSigning.Check(
                ManifestSignaturePolicy.VerifyIfPresent, ManifestBytes, LegacySignature, "latest.json", []));

        /// <summary>The state this repo actually ships in right now: no key, no signatures, updates
        /// keep working.</summary>
        [Fact]
        public void An_unsigned_release_with_no_key_provisioned_is_accepted() =>
            ReleaseSigning.Check(
                ManifestSignaturePolicy.VerifyIfPresent, ManifestBytes, null, "latest.json", []);

        [Fact]
        public void Required_refuses_an_unsigned_release() =>
            Refuse(ManifestSignaturePolicy.Required, null);

        [Fact]
        public void Required_accepts_a_valid_signature() =>
            Assert.Contains("signed by key", Check(ManifestSignaturePolicy.Required, LegacySignature));

        /// <summary>The API fallback synthesizes its manifest client-side, so there is nothing to
        /// verify. Under Required it has to be refused, or deleting one .minisig from the release
        /// host would push every launcher onto an unsigned path.</summary>
        [Theory]
        [InlineData(ManifestSignaturePolicy.Off, false)]
        [InlineData(ManifestSignaturePolicy.VerifyIfPresent, false)]
        [InlineData(ManifestSignaturePolicy.Required, true)]
        public void Unsignable_feeds_are_refused_only_under_required(
            ManifestSignaturePolicy policy, bool refused)
        {
            var act = () => ReleaseSigning.EnsureUnsignedFeedAllowed(policy, "api fallback");
            if (refused)
                Assert.Throws<UnsignableFeedException>(act);
            else
                act();
        }

        /// <summary>The two failures carry different types because CompositeFeed handles them
        /// oppositely: an unsignable feed is skipped so the chain can try a signed one, a failed
        /// check aborts the chain. Collapsing them would either let a tampered manifest fall through
        /// to a weaker source, or take the beta channel down (it asks the unsignable feed first).</summary>
        [Fact]
        public void A_failed_check_is_not_the_same_exception_as_an_unsignable_feed()
        {
            Assert.IsType<UnsignableFeedException>(Record.Exception(() =>
                ReleaseSigning.EnsureUnsignedFeedAllowed(ManifestSignaturePolicy.Required, "api")));
            Assert.IsType<ManifestSignatureException>(Record.Exception(() =>
                Check(ManifestSignaturePolicy.VerifyIfPresent, ForeignSignature)));
        }

        /// <summary>The transition state. Flipping this to Required is the last step of the rollout
        /// described in release-signing.md, not something to tidy up early.</summary>
        [Fact]
        public void The_shipped_default_is_verify_if_present() =>
            Assert.Equal(ManifestSignaturePolicy.VerifyIfPresent, ReleaseSigning.DefaultPolicy);

        /// <summary>Guards the ordering rule: a key here before the release job signs with it is
        /// safe, the reverse is an outage. When this starts failing, someone has provisioned the
        /// release key — check that the game repo is not already signing with it.</summary>
        [Fact]
        public void No_release_key_is_provisioned_yet() =>
            Assert.Empty(ReleaseSigning.TrustedKeys);

        private static string Check(ManifestSignaturePolicy policy, string? signature) =>
            ReleaseSigning.Check(policy, ManifestBytes, signature, "latest.json", Trusted);

        private static void Refuse(ManifestSignaturePolicy policy, string? signature) =>
            Assert.Throws<ManifestSignatureException>(() => Check(policy, signature));
    }
}
