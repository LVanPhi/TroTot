using Shouldly;
using TroViec.Api.Infrastructure.Crypto;

namespace TroViec.Api.Tests;

public sealed class IdentifierHasherTests
{
    // Same vector as apps/worker/tests/test_identifiers.py: key = base64 of bytes 0..31.
    private const string SharedKeyBase64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private const string SharedHmacHex = "b0654f42d896f81d38b1e33a1170f472472115a2f8d99196dc8866737f8d85ab";

    [Fact]
    public void Hash_SharedTestVector_MatchesWorker() =>
        Convert.ToHexStringLower(new IdentifierHasher(SharedKeyBase64).Hash("phone", "+84912345678")).ShouldBe(SharedHmacHex);

    [Theory]
    [InlineData("not base64 !!")]
    [InlineData("AAEC")]   // only 3 bytes
    public void Constructor_BadKey_Throws(string key) =>
        Should.Throw<InvalidOperationException>(() => new IdentifierHasher(key));
}
