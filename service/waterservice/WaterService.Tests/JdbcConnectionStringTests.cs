using Microsoft.Data.SqlClient;
using TUnit.Core;
using WaterService.Configuration;

namespace WaterService.Tests;

public class JdbcConnectionStringTests
{
    [Test]
    public async Task Build_ConvertsJdbcUrl_ToSqlClientConnectionString()
    {
        string result = JdbcConnectionString.Build(
            "jdbc:sqlserver://testserver.example.com:1433;databaseName=DB_x;encrypt=true;trustServerCertificate=true",
            "the_user",
            "the_pass");

        var builder = new SqlConnectionStringBuilder(result);
        await Assert.That(builder.DataSource).IsEqualTo("testserver.example.com,1433");
        await Assert.That(builder.InitialCatalog).IsEqualTo("DB_x");
        await Assert.That(builder.UserID).IsEqualTo("the_user");
        await Assert.That(builder.Password).IsEqualTo("the_pass");
        await Assert.That(builder.Encrypt).IsNotEqualTo(Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Optional);
        await Assert.That(builder.TrustServerCertificate).IsTrue();
    }

    [Test]
    public async Task Build_HostWithoutPort_KeepsHostAsDataSource()
    {
        string result = JdbcConnectionString.Build(
            "jdbc:sqlserver://localhost;databaseName=fish", "u", "p");

        var builder = new SqlConnectionStringBuilder(result);
        await Assert.That(builder.DataSource).IsEqualTo("localhost");
        await Assert.That(builder.InitialCatalog).IsEqualTo("fish");
    }

    [Test]
    public async Task Build_NativeConnectionString_PassesThroughAndMergesCredentials()
    {
        string result = JdbcConnectionString.Build(
            "Server=foo;Database=bar", "u", "p");

        var builder = new SqlConnectionStringBuilder(result);
        await Assert.That(builder.DataSource).IsEqualTo("foo");
        await Assert.That(builder.InitialCatalog).IsEqualTo("bar");
        await Assert.That(builder.UserID).IsEqualTo("u");
        await Assert.That(builder.Password).IsEqualTo("p");
    }

    [Test]
    public async Task Build_DoesNotOverrideCredentialsAlreadyInUrl()
    {
        string result = JdbcConnectionString.Build(
            "jdbc:sqlserver://h:1433;databaseName=d;user=urluser;password=urlpass",
            "argUser",
            "argPass");

        var builder = new SqlConnectionStringBuilder(result);
        await Assert.That(builder.UserID).IsEqualTo("urluser");
        await Assert.That(builder.Password).IsEqualTo("urlpass");
    }

    [Test]
    public async Task Build_BlankUrl_Throws()
    {
        await Assert.That(() => JdbcConnectionString.Build("", "u", "p"))
            .Throws<ArgumentException>();
    }
}
