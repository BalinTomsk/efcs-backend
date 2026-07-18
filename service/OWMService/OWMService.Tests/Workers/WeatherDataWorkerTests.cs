namespace OWMService.Tests.Workers
{
    using Moq;
    using OWMService.Config;
    using OWMService.Logging;
    using OWMService.Workers;
    using System;
    using Xunit;

    public class WeatherDataWorkerTests
    {
        private readonly Mock<IEventLogger> m_mockLogger;
        private readonly WeatherDataWorkerWg m_worker;
        private readonly TimeSpan m_defaultBudget = TimeSpan.FromHours(8);

        public WeatherDataWorkerTests()
        {
            m_mockLogger = new Mock<IEventLogger>();
            m_worker = new WeatherDataWorkerWg(m_mockLogger.Object);
        }

        #region Constructor Tests

        [Fact]
        public void Constructor_WithValidLogger_ShouldInitialize()
        {
            // Arrange & Act
            var worker = new WeatherDataWorkerWg(m_mockLogger.Object);

            // Assert
            Assert.NotNull(worker);
        }

        [Fact]
        public void Constructor_WithNullLogger_ShouldThrowArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => new WeatherDataWorkerWg(null));
        }

        #endregion

        #region Process Tests

        [Fact]
        public void Process_WithEmptyConnectionString_ShouldReturnFalse()
        {
            // Arrange
            var settings = new Settings
            {
                Server = "",
                DbName = "",
                UserName = "",
                UserPassword = ""
            };

            // Act
            var result = m_worker.Process(settings, m_defaultBudget);

            // Assert
            Assert.False(result);
            m_mockLogger.Verify(l => l.LogInfo(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void Process_WithNullConnectionString_ShouldReturnFalse()
        {
            // Arrange
            var settings = new Settings
            {
                Server = null,
                DbName = null,
                UserName = null,
                UserPassword = null
            };

            // Act
            var result = m_worker.Process(settings, m_defaultBudget);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public void Process_WithInvalidConnection_ShouldReturnFalseAndLogError()
        {
            // Arrange
            var settings = new Settings
            {
                Server = "invalid_server_12345",
                DbName = "testdb",
                UserName = "testuser",
                UserPassword = "wrongpass"
            };

            // Act
            var result = m_worker.Process(settings, m_defaultBudget);

            // Assert
            Assert.False(result);
            m_mockLogger.Verify(
                l => l.LogError(It.IsAny<string>()),
                Times.Once,
                "Should log error on connection failure");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Process_WithVariousInvalidSettings_ShouldReturnFalse(string invalidValue)
        {
            // Arrange
            var settings = new Settings
            {
                Server = invalidValue,
                DbName = "testdb",
                UserName = "user",
                UserPassword = "pass"
            };

            // Act
            var result = m_worker.Process(settings, m_defaultBudget);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public void Process_WithInvalidConnectionString_ShouldLogErrorMessage()
        {
            // Arrange
            var settings = new Settings
            {
                Server = "nonexistent_server_xyz_invalid",
                DbName = "testdb",
                UserName = "invaliduser",
                UserPassword = "invalidpass"
            };

            // Act
            var result = m_worker.Process(settings, m_defaultBudget);

            // Assert
            Assert.False(result);
            m_mockLogger.Verify(
                l => l.LogError(It.Is<string>(msg => msg.Contains("OWMService Failed to connect"))),
                Times.Once,
                "Should log connection failure message");
        }

        [Fact]
        public void Process_ShouldReturnResult()
        {
            // Arrange
            var settings = new Settings
            {
                Server = "",
                DbName = "",
                UserName = "",
                UserPassword = ""
            };

            // Act
            var result = m_worker.Process(settings, m_defaultBudget);

            // Assert
            Assert.False(result);
        }

        #endregion

        #region IWeatherDataWorker Interface Tests

        [Fact]
        public void WeatherDataWorker_ShouldImplementIWeatherDataWorker()
        {
            // Assert
            Assert.IsAssignableFrom<IWeatherDataWorker>(m_worker);
        }

        #endregion
    }
}