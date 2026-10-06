using System.Text;
using ClaudeCounter.Core.Auth;
using Xunit;

namespace ClaudeCounter.Tests.Auth;

public class PkceTests
{
    [Fact]
    public void MatchesTheRfc7636ReferenceVector()
    {
        // RFC 7636 appendix B: this exact verifier must produce this challenge.
        // If this fails the server will reject every code exchange.
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        const string expected = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        Assert.Equal(expected, PkceCodes.FromVerifier(verifier).Challenge);
    }

    [Fact]
    public void ChallengeIsBase64UrlWithNoPadding()
    {
        var challenge = PkceCodes.Generate().Challenge;
        Assert.DoesNotContain('+', challenge);
        Assert.DoesNotContain('/', challenge);
        Assert.DoesNotContain('=', challenge);
    }

    [Fact]
    public void VerifierLengthIsWithinTheRfcRange()
    {
        var verifier = PkceCodes.Generate().Verifier;
        Assert.InRange(verifier.Length, 43, 128);
    }

    [Fact]
    public void VerifierUsesOnlyUnreservedCharacters() =>
        Assert.All(PkceCodes.Generate().Verifier.ToCharArray(), c =>
            Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~',
                $"'{c}' is not an unreserved character"));

    [Fact]
    public void EachGenerationProducesADifferentVerifier()
    {
        var verifiers = Enumerable.Range(0, 20).Select(_ => PkceCodes.Generate().Verifier).ToList();
        Assert.Equal(verifiers.Count, verifiers.Distinct().Count());
    }

    [Fact]
    public void InjectedRandomnessIsUsed()
    {
        var bytes = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var codes = PkceCodes.Generate(_ => bytes);
        Assert.Equal(Base64Url.Encode(bytes), codes.Verifier);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF }, "____")]
    [InlineData(new byte[] { 0xFB, 0xFF, 0xBF }, "-_-_")]
    [InlineData(new byte[] { 0x01 }, "AQ")]           // padding stripped
    public void Base64UrlEncodesToTheUrlSafeAlphabet(byte[] input, string expected) =>
        Assert.Equal(expected, Base64Url.Encode(input));

    [Fact]
    public void ChallengeIsDerivedFromTheAsciiBytesOfTheVerifier()
    {
        var codes = PkceCodes.Generate();
        var expected = Base64Url.Encode(
            System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(codes.Verifier)));
        Assert.Equal(expected, codes.Challenge);
    }
}
