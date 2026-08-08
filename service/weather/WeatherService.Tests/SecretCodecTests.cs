using TUnit.Core;
using WeatherService.Configuration;

namespace WeatherService.Tests;

/// <summary>
/// Covers the <c>enc:v1:</c> value format.
///
/// <para>The payloads below were produced by <c>efj-backend/secret/Protect-Env.ps1</c> under the fixed
/// key in <see cref="KeyHex"/> — the same fixtures the Java <c>SecretCodecTest</c> uses — so these are
/// genuine cross-language interop tests: if the PowerShell encryptor, the Java decryptor and this C#
/// decryptor ever drift on nonce placement, tag position, base64 alphabet, or additional authenticated
/// data, they fail. The values are invented, not real credentials.</para>
/// </summary>
public class SecretCodecTests
{
    private const string KeyHex = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";

    private const string EncDbUrl =
        "enc:v1:GdbrPJpx_QeoN5-s7XAzyG0ROIL7IFPAOwmPVO5LgrSOs1KPkKoyYrtIhuIyVX_J8Xc"
        + "VZY8VE6ybG0AN_q6iBSQK8Z0H3EGpPN0KXX9fVc6Szdb7wg";

    private const string EncDbUsername = "enc:v1:oNuX3o5NIC67K4HiN1M95YhWvh3S5AcORZLKOWSbzQ1NiQ";

    private const string EncDbPassword = "enc:v1:6tiG1Z4iC24LMP6h7LN5x9s_OraeqDKDny_lHf9rAm94XKFNbu3pSaqv9A";

    private static byte[] Key(string hex) => Convert.FromHexString(hex);

    /// <summary>Runs an action expected to fail and hands back the exception for message assertions.</summary>
    private static InvalidOperationException Capture(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }

        throw new InvalidOperationException("expected an InvalidOperationException, but none was thrown");
    }

    /// <summary>Flips a character in the middle of the payload, which always alters a full 6-bit group.</summary>
    private static string Tamper(string value)
    {
        string body = value.Substring("enc:v1:".Length);
        int index = body.Length / 2;
        char replacement = body[index] == 'A' ? 'B' : 'A';
        return "enc:v1:" + body.Substring(0, index) + replacement + body.Substring(index + 1);
    }

    [Test]
    public async Task DecryptsPayloadsProducedByTheProtectEnvScript()
    {
        byte[] key = Key(KeyHex);

        await Assert.That(SecretCodec.DecryptWith(key, "DB_URL", EncDbUrl))
            .IsEqualTo("jdbc:sqlserver://db.example.net:1433;databaseName=envfish");
        await Assert.That(SecretCodec.DecryptWith(key, "DB_USERNAME", EncDbUsername)).IsEqualTo("ff_app");
        await Assert.That(SecretCodec.DecryptWith(key, "DB_PASSWORD", EncDbPassword)).IsEqualTo("s3cr3t-p@ssw0rd");
    }

    [Test]
    public async Task ReturnsUnmarkedValuesUntouched()
    {
        // The backwards-compatibility guarantee: a fully-plaintext .env keeps working unchanged,
        // which is what lets the decrypt-capable images ship before the file is ever encrypted.
        await Assert.That(SecretCodec.DecryptIfNeeded("PORT", "3000")).IsEqualTo("3000");
        await Assert.That(SecretCodec.DecryptIfNeeded("EMPTY", "")).IsEqualTo("");
        await Assert.That(SecretCodec.DecryptIfNeeded("MISSING", null)).IsNull();
    }

    [Test]
    public async Task DetectsTheMarker()
    {
        await Assert.That(SecretCodec.IsEncrypted(EncDbPassword)).IsTrue();
        await Assert.That(SecretCodec.IsEncrypted("3000")).IsFalse();
        await Assert.That(SecretCodec.IsEncrypted("enc:v2:something")).IsFalse();
        await Assert.That(SecretCodec.IsEncrypted(null)).IsFalse();
    }

    [Test]
    public async Task RefusesAValueMovedToADifferentVariable()
    {
        // The variable name is additional authenticated data, so a ciphertext cannot be relocated
        // from one key to another — e.g. copying the username over the password field.
        InvalidOperationException thrown = Capture(
            () => SecretCodec.DecryptWith(Key(KeyHex), "DB_PASSWORD", EncDbUsername));

        await Assert.That(thrown.Message).Contains("DB_PASSWORD");
    }

    [Test]
    public async Task RefusesATamperedPayload()
    {
        await Assert.That(() => SecretCodec.DecryptWith(Key(KeyHex), "DB_PASSWORD", Tamper(EncDbPassword)))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task RefusesTheWrongKey()
    {
        const string otherKey = "1f1e1d1c1b1a191817161514131211100f0e0d0c0b0a09080706050403020100";

        await Assert.That(() => SecretCodec.DecryptWith(Key(otherKey), "DB_PASSWORD", EncDbPassword))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task RefusesAMalformedPayload()
    {
        InvalidOperationException notBase64 = Capture(
            () => SecretCodec.DecryptWith(Key(KeyHex), "DB_PASSWORD", "enc:v1:not base64!!"));
        await Assert.That(notBase64.Message).Contains("base64url");

        InvalidOperationException tooShort = Capture(
            () => SecretCodec.DecryptWith(Key(KeyHex), "DB_PASSWORD", "enc:v1:AAAA"));
        await Assert.That(tooShort.Message).Contains("too short");
    }

    [Test]
    public async Task NeverExposesTheValueInAFailureMessage()
    {
        // Failure messages travel into logs and LogException rows; they must name the variable but
        // never echo the payload back.
        InvalidOperationException thrown = Capture(
            () => SecretCodec.DecryptWith(Key(KeyHex), "DB_PASSWORD", Tamper(EncDbPassword)));

        await Assert.That(thrown.Message).DoesNotContain(EncDbPassword.Substring(7, 23));
    }
}
