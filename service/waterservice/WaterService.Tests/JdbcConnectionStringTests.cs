using Microsoft.Data.SqlClient;
using WaterService.Configuration;
using Xunit;

namespace WaterService.Tests;

public class JdbcConnectionStringTests
{
    [Fact]
    public void Build_ConvertsJdbcUrl_ToSqlClientConnectionString()
    {
        string result = JdbcConnectionString.Build(
            "jdbc:sqlserver://testserver.example.com:1433;databaseName=DB_x;encrypt=true;trustServerCertificate=true",
            "the_user",
            "the_pass");

        var builder = new SqlConnectionStringBuilder(result);
        Assert.Equal("testserver.example.com,1433", builder.DataSource);
        Assert.Equal("DB_x", builder.InitialCatalog);
        Assert.Equal("the_user", builder.UserID);
        Assert.Equal("the_pass", builder.Password);
        Assert.True(builder.Encrypt);
        Assert.True(builder.TrustServerCertificate);
    }

    [Fact]
    public void Build_HostWithoutPort_KeepsHostAsDataSource()
    {
        string result = JdbcConnectionString.Build(
            "jdbc:sqlserver://localhost;databaseName=fish", "u", "p");

        var builder = new SqlConnectionStringBuilder(result);
        Assert.Equal("localhost", builder.DataSource);
        Assert.Equal("fish", builder.InitialCatalog);
    }

    [Fact]
    public void Build_NativeConnectionString_PassesThroughAndMergesCredentials()
    {
        string result = JdbcConnectionString.Build(
            "Server=foo;Database=bar", "u", "p");

        var builder = new SqlConnectionStringBuilder(result);
        Assert.Equal("foo", builder.DataSource);
        Assert.Equal("bar", builder.InitialCatalog);
        Assert.Equal("u", builder.UserID);
        Assert.Equal("p", builder.Password);
    }

    [Fact]
    public void Build_DoesNotOverrideCredentialsAlreadyInUrl()
    {
        string result = JdbcConnectionString.Build(
            "jdbc:sqlserver://h:1433;databaseName=d;user=urluser;password=urlpass",
            "argUser",
            "argPass");

        var builder = new SqlConnectionStringBuilder(result);
        Assert.Equal("urluser", builder.UserID);
        Assert.Equal("urlpass", builder.Password);
    }

    [Fact]
    public void Build_BlankUrl_Throws()
    {
        Assert.Throws<ArgumentException>(() => JdbcConnectionString.Build("", "u", "p"));
    }
}
