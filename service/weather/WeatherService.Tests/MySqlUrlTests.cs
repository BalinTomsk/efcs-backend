using Microsoft.Extensions.Configuration;
using MySqlConnector;
using TUnit.Core;
using WeatherService.Configuration;

namespace WeatherService.Tests;

public class MySqlUrlTests
{
    [Test]
    public async Task Build_ConvertsJdbcUrl_ToMySqlConnectorConnectionString()
    {
        string result = MySqlUrl.Build("jdbc:mysql://envdata.vpc.example:3306/envdata?sslMode=REQUIRED", "the_user", "the_pass");

        var builder = new MySqlConnectionStringBuilder(result);
        await Assert.That(builder.Server).IsEqualTo("envdata.vpc.example");
        await Assert.That(builder.Port).IsEqualTo(3306u);
        await Assert.That(builder.Database).IsEqualTo("envdata");
        await Assert.That(builder.UserID).IsEqualTo("the_user");
        await Assert.That(builder.Password).IsEqualTo("the_pass");
        await Assert.That(builder.SslMode).IsEqualTo(MySqlSslMode.Required);
    }

    [Test]
    public async Task Build_EncryptsByDefault_BecauseTheEnvdataAccountsRequireSsl()
    {
        var builder = new MySqlConnectionStringBuilder(MySqlUrl.Build("jdbc:mysql://db.example:3306/envdata", "u", "p"));

        await Assert.That(builder.SslMode).IsEqualTo(MySqlSslMode.Required);
    }

    [Test]
    public async Task Build_SessionsAreResetAndOutParametersReadAsUserVariables()
    {
        var builder = new MySqlConnectionStringBuilder(MySqlUrl.Build("jdbc:mysql://db.example/envdata", "u", "p"));

        await Assert.That(builder.ConnectionReset).IsTrue();
        await Assert.That(builder.AllowUserVariables).IsTrue();
        await Assert.That(builder.Port).IsEqualTo(3306u);
    }

    [Test]
    public async Task Build_CredentialsInTheUrlWinOverTheSeparateVariables()
    {
        var builder = new MySqlConnectionStringBuilder(
            MySqlUrl.Build("jdbc:mysql://db.example/envdata?user=url_user&password=url%26pass", "env_user", "env_pass"));

        await Assert.That(builder.UserID).IsEqualTo("url_user");
        await Assert.That(builder.Password).IsEqualTo("url&pass");
    }

    [Test]
    public async Task Build_AcceptsANativeConnectionString()
    {
        var builder = new MySqlConnectionStringBuilder(
            MySqlUrl.Build("Server=127.0.0.1;Port=13306;Database=envdata", "u", "p"));

        await Assert.That(builder.Port).IsEqualTo(13306u);
        await Assert.That(builder.UserID).IsEqualTo("u");
    }

    [Test]
    public async Task Build_RejectsBlankAndNonMySqlJdbcUrls()
    {
        await Assert.That(() => MySqlUrl.Build("", "u", "p")).Throws<ArgumentException>();
        await Assert.That(() => MySqlUrl.Build("jdbc:sqlserver://h:1433;databaseName=x", "u", "p"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Registration_ReadsMySqlKeys_AndLeavesTheSqlServerDbKeysAlone()
    {
        // One env file carries both connections: DB_* = SQL Server, MYSQL_* = envdata.
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DB_URL"] = "jdbc:sqlserver://s31.example:1433;databaseName=fish",
            ["DB_USERNAME"] = "mssql_user",
            ["DB_PASSWORD"] = "mssql_pass",
            ["MYSQL_URL"] = "jdbc:mysql://envdata.vpc.example:3306/envdata?sslMode=REQUIRED",
            ["MYSQL_USERNAME"] = "envdata_user",
            ["MYSQL_PASSWORD"] = "envdata_pass",
        }).Build();

        var builder = new MySqlConnectionStringBuilder(ServiceRegistration.BuildConnectionString(configuration));

        await Assert.That(builder.Server).IsEqualTo("envdata.vpc.example");
        await Assert.That(builder.UserID).IsEqualTo("envdata_user");
        await Assert.That(builder.Password).IsEqualTo("envdata_pass");
    }

    [Test]
    public async Task Registration_WithoutMySqlUrl_FailsNamingIt_EvenWhenDbUrlIsSet()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DB_URL"] = "jdbc:sqlserver://s31.example:1433;databaseName=fish",
        }).Build();

        InvalidOperationException? thrown = null;
        try
        {
            ServiceRegistration.BuildConnectionString(configuration);
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }

        await Assert.That(thrown).IsNotNull();
        await Assert.That(thrown!.Message).Contains("MYSQL_URL");
    }
}
