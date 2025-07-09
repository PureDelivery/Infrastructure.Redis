// PureDelivery.Infrastructure.Redis/Extensions/ServiceCollectionExtensions.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PureDelivery.Infrastructure.Redis.Configuration;
using PureDelivery.Infrastructure.Redis.Services.impl;
using PureDelivery.Common.Configuration.Interfaces;
using PureDelivery.Common.Configuration.Services;
using PureDelivery.Shared.Contracts.Common.Services;
using StackExchange.Redis;

namespace PureDelivery.Infrastructure.Redis.Extensions
{
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Добавляет Redis сервисы с типизированной конфигурацией и автоматической валидацией
        /// Работает с любым провайдером конфигурации (Local, Remote, Hybrid, Environment)
        /// </summary>
        public static IServiceCollection AddRedisServices(
            this IServiceCollection services,
            string serviceName = "Redis")
        {
            // Регистрируем типизированную конфигурацию с валидацией
            services.AddSingleton<IConfiguration<RedisConfiguration>>(sp =>
            {
                var configProvider = sp.GetRequiredService<ICustomConfigurationProvider>();
                var config = configProvider.GetConfigurationAsync<RedisConfiguration>(serviceName).Result;

                // Валидация происходит здесь при создании конфигурации
                config.Validate();

                return config; // RedisConfiguration реализует IConfiguration<RedisConfiguration>
            });

            // Регистрируем Redis подключение
            services.AddSingleton<IConnectionMultiplexer>(sp =>
            {
                var config = (RedisConfiguration)sp.GetRequiredService<IConfiguration<RedisConfiguration>>();
                var logger = sp.GetRequiredService<ILogger<IConnectionMultiplexer>>();

                return CreateConnectionMultiplexer(config, logger);
            });

            // Регистрируем сессионный сервис с зависимостями
            services.AddScoped<ISessionService>(sp =>
            {
                var redis = sp.GetRequiredService<IConnectionMultiplexer>();
                var config = sp.GetRequiredService<IConfiguration<RedisConfiguration>>();
                var logger = sp.GetRequiredService<ILogger<RedisSessionService>>();

                return new RedisSessionService(redis, config, logger);
            });

            return services;
        }

        /// <summary>
        /// Создает подключение к Redis с настройками и валидацией
        /// </summary>
        private static IConnectionMultiplexer CreateConnectionMultiplexer(
            RedisConfiguration config,
            ILogger logger)
        {
            var options = ConfigurationOptions.Parse(config.ConnectionString);

            options.ConnectTimeout = config.ConnectTimeout;
            options.SyncTimeout = config.CommandTimeout;
            options.ConnectRetry = config.ConnectRetry;
            options.Ssl = config.Ssl;

            if (!string.IsNullOrEmpty(config.Password))
            {
                options.Password = config.Password;
            }

            try
            {
                var connection = ConnectionMultiplexer.Connect(options);
                logger.LogInformation("Successfully connected to Redis at {ConnectionString}",
                    config.ConnectionString.Split('@').LastOrDefault() ?? config.ConnectionString);

                return connection;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to connect to Redis at {ConnectionString}",
                    config.ConnectionString);
                throw;
            }
        }
    }
}