using Microsoft.Extensions.Logging;
using PureDelivery.Common.Configuration.Interfaces;
using PureDelivery.Infrastructure.Redis.Configuration;
using PureDelivery.Shared.Contracts.Common.Services;
using PureDelivery.Shared.Contracts.Domain.Enums;
using PureDelivery.Shared.Contracts.DTOs.Identity.Requests;
using PureDelivery.Shared.Contracts.DTOs.SessionDTO;
using StackExchange.Redis;
using System.Text.Json;

namespace PureDelivery.Infrastructure.Redis.Services.impl
{
    /// <summary>
    /// Redis реализация сервиса сессий
    /// </summary>
    public class RedisSessionService : ISessionService
    {
        private readonly IDatabase _database;
        private readonly RedisConfiguration _config;
        private readonly ILogger<RedisSessionService> _logger;
        private readonly JsonSerializerOptions _jsonOptions;

        public RedisSessionService(
            IConnectionMultiplexer redis,
            IConfiguration<RedisConfiguration> config,
            ILogger<RedisSessionService> logger)
        {
            config.Validate();

            _config = (RedisConfiguration)config;
            _database = redis.GetDatabase(_config.Database);
            _logger = logger;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            };

            _logger.LogInformation("Redis Session Service initialized with prefix: {KeyPrefix}", _config.KeyPrefix);
        }

        private string GetSessionKey(string sessionId) => $"{_config.KeyPrefix}session:{sessionId}";
        private string GetUserActiveSessionKey(string userId) => $"{_config.KeyPrefix}user:{userId}:active";

        public async Task<SessionDto?> GetSessionAsync(string sessionId)
        {
            try
            {
                var key = GetSessionKey(sessionId);
                var sessionData = await _database.StringGetAsync(key);

                if (!sessionData.HasValue)
                {
                    _logger.LogDebug("Session {SessionId} not found", sessionId);
                    return null;
                }

                var session = JsonSerializer.Deserialize<SessionDto>(sessionData!, _jsonOptions);
                _logger.LogDebug("Retrieved session {SessionId}", sessionId);
                return session;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving session {SessionId}", sessionId);
                return null;
            }
        }

        public async Task<SessionDto?> GetUserActiveSessionAsync(string userId)
        {
            try
            {
                var userSessionKey = GetUserActiveSessionKey(userId);
                var sessionId = await _database.StringGetAsync(userSessionKey);

                if (!sessionId.HasValue)
                {
                    _logger.LogDebug("No active session found for user {UserId}", userId);
                    return null;
                }

                return await GetSessionAsync(sessionId!);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving active session for user {UserId}", userId);
                return null;
            }
        }

        public async Task<SessionDto> CreateSessionAsync(string userId)
        {
            try
            {
                // Удаляем предыдущие сессии пользователя
                await DeleteAllUserSessionsAsync(userId);

                var sessionId = Guid.NewGuid().ToString();
                var session = new SessionDto
                {
                    SessionId = sessionId,
                    UserId = userId
                };

                await SaveSessionAsync(session);

                // Сохраняем ссылку на активную сессию пользователя
                var userSessionKey = GetUserActiveSessionKey(userId);
                await _database.StringSetAsync(userSessionKey, sessionId, TimeSpan.FromMinutes(_config.SessionExpirationMinutes));

                _logger.LogInformation("Created new session {SessionId} for user {UserId}", sessionId, userId);
                return session;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating session for user {UserId}", userId);
                throw;
            }
        }

        public async Task<SessionDto> AddCustomerSessionDataAsync(string sessionId, CustomerSessionDto customerData)
        {
            try
            {
                var session = await GetSessionAsync(sessionId);
                if (session == null)
                {
                    _logger.LogWarning("Session {SessionId} not found", sessionId);
                    throw new InvalidOperationException($"Session {sessionId} not found");
                }

                // Добавляем данные клиента в существующую сессию
                session.CustomerSessionDto = customerData;

                var success = await SaveSessionAsync(session);

                if (!success)
                {
                    _logger.LogError("Failed to save session {SessionId} with customer data", sessionId);
                    throw new InvalidOperationException($"Failed to save session {sessionId}");
                }

                _logger.LogInformation("Added customer data to existing session {SessionId}", sessionId);

                return session;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding customer data to session {SessionId}", sessionId);
                throw;
            }
        }

        public async Task<SessionDto> UpdateCustomerDataAsync(string sessionId, CustomerSessionDto customerData)
        {
            try
            {
                var session = await GetSessionAsync(sessionId);
                if (session == null)
                {
                    _logger.LogWarning("Session {SessionId} not found for update", sessionId);
                    throw new InvalidOperationException($"Session {sessionId} not found");
                }

                session.CustomerSessionDto = customerData;

                var success = await SaveSessionAsync(session);

                if (!success)
                {
                    _logger.LogError("Failed to update session {SessionId} with customer data", sessionId);
                    throw new InvalidOperationException($"Failed to update session {sessionId}");
                }

                _logger.LogInformation("Updated customer data in session {SessionId}", sessionId);
                return session;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating customer data for session {SessionId}", sessionId);
                throw;
            }
        }

        public async Task<bool> DeleteSessionAsync(string sessionId)
        {
            try
            {
                var session = await GetSessionAsync(sessionId);
                if (session == null)
                {
                    return false;
                }

                var sessionKey = GetSessionKey(sessionId);
                var userSessionKey = GetUserActiveSessionKey(session.UserId);

                await _database.KeyDeleteAsync(sessionKey);
                await _database.KeyDeleteAsync(userSessionKey);

                _logger.LogInformation("Deleted session {SessionId} for user {UserId}", sessionId, session.UserId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting session {SessionId}", sessionId);
                return false;
            }
        }

        public async Task<bool> DeleteAllUserSessionsAsync(string userId)
        {
            try
            {
                var userSessionKey = GetUserActiveSessionKey(userId);
                var sessionId = await _database.StringGetAsync(userSessionKey);

                if (sessionId.HasValue)
                {
                    var sessionKey = GetSessionKey(sessionId!);
                    await _database.KeyDeleteAsync(sessionKey);
                    await _database.KeyDeleteAsync(userSessionKey);

                    _logger.LogInformation("Deleted all sessions for user {UserId}", userId);
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting all sessions for user {UserId}", userId);
                return false;
            }
        }

        // В вашем SessionService добавьте этот метод:

        public async Task<SessionValidationResult> IsSessionValidAsync(
            string sessionId,
            string currentIpAddress,
            string currentUserAgent)
        {
            try
            {
                // Получаем сессию
                var session = await GetSessionAsync(sessionId);
                if (session == null)
                {
                    _logger.LogWarning("Session {SessionId} not found", sessionId);
                    return SessionValidationResult.Invalid("Session not found");
                }

                // Проверяем IP адрес
                if (session.IpAddress != currentIpAddress)
                {
                    _logger.LogWarning("IP mismatch for session {SessionId}: stored {StoredIP}, current {CurrentIP}",
                        sessionId, session.IpAddress, currentIpAddress);
                    return SessionValidationResult.Invalid("IP address mismatch");
                }

                // Проверяем User Agent
                if (session.UserAgent != currentUserAgent)
                {
                    _logger.LogWarning("User Agent mismatch for session {SessionId}: stored {StoredUA}, current {CurrentUA}",
                        sessionId, session.UserAgent?.Substring(0, Math.Min(session.UserAgent.Length, 50)),
                        currentUserAgent?.Substring(0, Math.Min(currentUserAgent?.Length ?? 0, 50)));
                    return SessionValidationResult.Invalid("User Agent mismatch");
                }

                _logger.LogDebug("Session {SessionId} validated successfully for user {UserId}",
                    sessionId, session.UserId);

                return SessionValidationResult.Valid(session.UserId, session.CustomerSessionDto, session.Role);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating session {SessionId}", sessionId);
                return SessionValidationResult.Invalid("Session validation error");
            }
        }

        public async Task<bool> SaveSessionAsync(SessionDto session)
        {
            try
            {
                var key = GetSessionKey(session.SessionId);
                var sessionJson = JsonSerializer.Serialize(session, _jsonOptions);
                var expiry = TimeSpan.FromMinutes(_config.SessionExpirationMinutes);

                await _database.StringSetAsync(key, sessionJson, expiry);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving session {SessionId}", session.SessionId);
                return false;
            }
        }

        public async Task<SessionDto> CreateSessionWithDataAsync(string userId, CustomerSessionDto? customerData, AuthenticateRequest authenticateRequest, UserRole role = UserRole.Customer)
        {
            try
            {
                // Удаляем предыдущие сессии пользователя
                await DeleteAllUserSessionsAsync(userId);

                var sessionId = Guid.NewGuid().ToString();
                var session = new SessionDto
                {
                    SessionId = sessionId,
                    UserId = userId,
                    Role = role,
                    CustomerSessionDto = customerData,
                    IpAddress = authenticateRequest.UserIP,
                    UserAgent = authenticateRequest.UserAgent
                };

                await SaveSessionAsync(session);

                // Сохраняем ссылку на активную сессию пользователя
                var userSessionKey = GetUserActiveSessionKey(userId);
                await _database.StringSetAsync(userSessionKey, sessionId, TimeSpan.FromMinutes(_config.SessionExpirationMinutes));

                _logger.LogInformation("Created new session with data {SessionId} for user {UserId} with role {Role}", sessionId, userId, role);
                return session;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating session with data for user {UserId}", userId);
                throw;
            }
        }
    }
}