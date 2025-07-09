// PureDelivery.Infrastructure.Redis/Configuration/RedisConfiguration.cs
using PureDelivery.Common.Configuration.Interfaces;
using System.ComponentModel.DataAnnotations;

namespace PureDelivery.Infrastructure.Redis.Configuration
{
    public class RedisConfiguration : IConfiguration<RedisConfiguration>
    {
        public const string SectionName = "Redis";

        [Required]
        public string ConnectionString { get; set; } = string.Empty;
        public string KeyPrefix { get; set; } = "puredelivery:session:";
        public int SessionExpirationHours { get; set; } = 24;
        public int Database { get; set; } = 0;
        public int ConnectTimeout { get; set; } = 5000;
        public int CommandTimeout { get; set; } = 5000;
        public int ConnectRetry { get; set; } = 3;
        public bool Ssl { get; set; } = false;
        public string? Password { get; set; }

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(ConnectionString))
                throw new ArgumentException("Redis connection string cannot be empty", nameof(ConnectionString));

            if (SessionExpirationHours <= 0 || SessionExpirationHours > 168) // 7 дней макс
                throw new ArgumentOutOfRangeException(nameof(SessionExpirationHours),
                    "Session expiration must be between 1 and 168 hours");

            if (Database < 0 || Database > 15)
                throw new ArgumentOutOfRangeException(nameof(Database), "Redis database must be between 0 and 15");

            if (ConnectTimeout <= 0)
                throw new ArgumentOutOfRangeException(nameof(ConnectTimeout), "Connect timeout must be greater than zero");

            if (CommandTimeout <= 0)
                throw new ArgumentOutOfRangeException(nameof(CommandTimeout), "Command timeout must be greater than zero");

            if (ConnectRetry < 0)
                throw new ArgumentOutOfRangeException(nameof(ConnectRetry), "Connect retry must be non-negative");
        }
    }
}