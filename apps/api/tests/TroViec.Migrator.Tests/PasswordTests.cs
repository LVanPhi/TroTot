using TroViec.Security;
using Shouldly;

namespace TroViec.Migrator.Tests;

public sealed class PasswordPolicyTests
{
    [Theory]
    [InlineData("short")]
    [InlineData("123456789")]
    public void Validate_ShorterThan10_ReturnsReason(string password) =>
        PasswordPolicy.Validate(password).ShouldNotBeNull();

    [Theory]
    [InlineData("1234567890")]
    [InlineData("qwertyuiop")]
    [InlineData("QWERTYUIOP")]
    public void Validate_CommonPassword_ReturnsReason(string password) =>
        PasswordPolicy.Validate(password).ShouldBe("password is too common");

    [Fact]
    public void Validate_StrongPassword_ReturnsNull() =>
        PasswordPolicy.Validate("Correct-Horse-Battery-42").ShouldBeNull();
}

public sealed class PasswordHasherTests
{
    [Fact]
    public void Hash_Always_UsesPhcFormatWithOwaspParameters()
    {
        var phc = PasswordHasher.Hash("Correct-Horse-Battery-42");

        phc.ShouldStartWith("$argon2id$v=19$m=19456,t=2,p=1$");
        phc.Split('$').Length.ShouldBe(6);
    }

    [Fact]
    public void Hash_SamePasswordTwice_UsesDifferentSalts() =>
        PasswordHasher.Hash("Correct-Horse-Battery-42").ShouldNotBe(PasswordHasher.Hash("Correct-Horse-Battery-42"));

    [Fact]
    public void Verify_CorrectAndWrongPassword_ReturnsTrueThenFalse()
    {
        var phc = PasswordHasher.Hash("Correct-Horse-Battery-42");

        PasswordHasher.Verify("Correct-Horse-Battery-42", phc).ShouldBeTrue();
        PasswordHasher.Verify("Correct-Horse-Battery-43", phc).ShouldBeFalse();
    }
}
