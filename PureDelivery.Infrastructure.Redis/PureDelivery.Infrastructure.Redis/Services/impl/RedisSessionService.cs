using Microsoft.Extensions.Logging;
using PureDelivery.Common.Configuration.Interfaces;
using PureDelivery.Infrastructure.Redis.Configuration;
using PureDelivery.Shared.Contracts.Common.Services;
using PureDelivery.Shared.Contracts.Domain.Enums;
using PureDelivery.Shared.Contracts.DTOs.Session;
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

        private string GetSessionKey(string sessionId) => $"{_config.KeyPrefix}{sessionId}";
        private string GetUserSessionsKey(string userId) => $"{_config.KeyPrefix}user:{userId}";

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

        public async Task<SessionDto> CreateSessionAsync(string userId)
        {
            try
            {
                var sessionId = Guid.NewGuid().ToString();
                var session = new SessionDto
                {
                    SessionId = sessionId,
                    UserId = userId,
                    OrderState = new OrderStateDto
                    {
                        Status = OrderStatus.Cart,
                        CreatedAt = DateTime.UtcNow,
                        LastUpdated = DateTime.UtcNow,
                        Items = new List<OrderItemSessionDto>(),
                        DeliveryStatus = DeliveryStatus.Pending
                    }
                };

                var key = GetSessionKey(sessionId);
                var sessionJson = JsonSerializer.Serialize(session, _jsonOptions);
                var expiry = TimeSpan.FromHours(_config.SessionExpirationHours);

                await _database.StringSetAsync(key, sessionJson, expiry);

                // Добавляем сессию в список пользователя
                var userSessionsKey = GetUserSessionsKey(userId);
                await _database.SetAddAsync(userSessionsKey, sessionId);
                await _database.KeyExpireAsync(userSessionsKey, expiry);

                _logger.LogInformation("Created new session {SessionId} for user {UserId}", sessionId, userId);
                return session;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating session for user {UserId}", userId);
                throw;
            }
        }

        public async Task<bool> UpdateOrderStateAsync(string sessionId, OrderStateDto orderState)
        {
            try
            {
                var session = await GetSessionAsync(sessionId);
                if (session == null) return false;

                orderState.LastUpdated = DateTime.UtcNow;
                session.OrderState = orderState;

                return await SaveSessionAsync(session);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating order state for session {SessionId}", sessionId);
                return false;
            }
        }

        public async Task<bool> UpdateRestaurantAsync(string sessionId, RestaurantSessionDto restaurant)
        {
            try
            {
                var session = await GetSessionAsync(sessionId);
                if (session == null) return false;

                session.OrderState.Restaurant = restaurant;
                session.OrderState.LastUpdated = DateTime.UtcNow;

                return await SaveSessionAsync(session);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating restaurant for session {SessionId}", sessionId);
                return false;
            }
        }

        public async Task<bool> AddItemToCartAsync(string sessionId, OrderItemSessionDto item)
        {
            try
            {
                var session = await GetSessionAsync(sessionId);
                if (session == null) return false;

                session.OrderState.Items ??= new List<OrderItemSessionDto>();

                // Проверяем, есть ли уже такой товар в корзине (по ID и опциям)
                var existingItem = session.OrderState.Items.FirstOrDefault(i =>
                    i.Id == item.Id &&
                    AreOptionsEqual(i.SelectedOptions, item.SelectedOptions) &&
                    i.SpecialInstructions == item.SpecialInstructions);

                if (existingItem != null)
                {
                    existingItem.Quantity += item.Quantity;
                }
                else
                {
                    session.OrderState.Items.Add(item);
                }

                session.OrderState.LastUpdated = DateTime.UtcNow;
                return await SaveSessionAsync(session);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding item to cart for session {SessionId}", sessionId);
                return false;
            }
        }

        public async Task<bool> RemoveItemFromCartAsync(string sessionId, string itemId)
        {
            try
            {
                var session = await GetSessionAsync(sessionId);
                if (session == null) return false;

                if (session.OrderState.Items != null)
                {
                    session.OrderState.Items = session.OrderState.Items.Where(i => i.Id != itemId).ToList();
                    session.OrderState.LastUpdated = DateTime.UtcNow;
                }

                return await SaveSessionAsync(session);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing item from cart for session {SessionId}", sessionId);
                return false;
            }
        }

        public async Task<bool> UpdateItemQuantityAsync(string sessionId, string itemId, int quantity)
        {
            try
            {
                var session = await GetSessionAsync(sessionId);
                if (session == null) return false;

                var item = session.OrderState.Items?.FirstOrDefault(i => i.Id == itemId);
                if (item == null) return false;

                if (quantity <= 0)
                {
                    return await RemoveItemFromCartAsync(sessionId, itemId);
                }

                item.Quantity = quantity;
                session.OrderState.LastUpdated = DateTime.UtcNow;
                return await SaveSessionAsync(session);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating item quantity for session {SessionId}", sessionId);
                return false;
            }
        }

        public async Task<bool> ClearCartAsync(string sessionId)
        {
            try
            {
                var session = await GetSessionAsync(sessionId);
                if (session == null) return false;

                session.OrderState.Items = new List<OrderItemSessionDto>();
                session.OrderState.LastUpdated = DateTime.UtcNow;

                return await SaveSessionAsync(session);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing cart for session {SessionId}", sessionId);
                return false;
            }
        }

        public async Task<bool> UpdateDeliveryInfoAsync(string sessionId, DeliveryInfoDto deliveryInfo)
        {
            try
            {
                var session = await GetSessionAsync(sessionId);
                if (session == null) return false;

                session.OrderState.Delivery = deliveryInfo;
                session.OrderState.LastUpdated = DateTime.UtcNow;

                return await SaveSessionAsync(session);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating delivery info for session {SessionId}", sessionId);
                return false;
            }
        }

        public async Task<bool> UpdatePaymentInfoAsync(string sessionId, PaymentInfoDto paymentInfo)
        {
            try
            {
                var session = await GetSessionAsync(sessionId);
                if (session == null) return false;

                session.OrderState.Payment = paymentInfo;
                session.OrderState.LastUpdated = DateTime.UtcNow;

                return await SaveSessionAsync(session);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating payment info for session {SessionId}", sessionId);
                return false;
            }
        }

        public async Task<bool> UpdateOrderStatusAsync(string sessionId, OrderStatus status)
        {
            try
            {
                var session = await GetSessionAsync(sessionId);
                if (session == null) return false;

                session.OrderState.Status = status;
                session.OrderState.LastUpdated = DateTime.UtcNow;

                return await SaveSessionAsync(session);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating order status for session {SessionId}", sessionId);
                return false;
            }
        }

        public async Task<bool> AssignCourierAsync(string sessionId, CourierSessionDto courier)
        {
            try
            {
                var session = await GetSessionAsync(sessionId);
                if (session == null) return false;

                session.OrderState.Courier = courier;
                session.OrderState.DeliveryStatus = DeliveryStatus.CourierAssigned;
                session.OrderState.LastUpdated = DateTime.UtcNow;

                return await SaveSessionAsync(session);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error assigning courier for session {SessionId}", sessionId);
                return false;
            }
        }

        public async Task<bool> UpdateCourierLocationAsync(string sessionId, CourierLocationDto location)
        {
            try
            {
                var session = await GetSessionAsync(sessionId);
                if (session == null || session.OrderState.Courier == null) return false;

                location.LastUpdated = DateTime.UtcNow;
                session.OrderState.Courier.Location = location;
                session.OrderState.LastUpdated = DateTime.UtcNow;

                return await SaveSessionAsync(session);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating courier location for session {SessionId}", sessionId);
                return false;
            }
        }

        public async Task<bool> UpdateDeliveryStatusAsync(string sessionId, DeliveryStatus status)
        {
            try
            {
                var session = await GetSessionAsync(sessionId);
                if (session == null) return false;

                session.OrderState.DeliveryStatus = status;
                session.OrderState.LastUpdated = DateTime.UtcNow;

                return await SaveSessionAsync(session);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating delivery status for session {SessionId}", sessionId);
                return false;
            }
        }

        public async Task<bool> DeleteSessionAsync(string sessionId)
        {
            try
            {
                var session = await GetSessionAsync(sessionId);
                if (session == null) return false;

                var key = GetSessionKey(sessionId);
                await _database.KeyDeleteAsync(key);

                // Удаляем из списка пользователя
                var userSessionsKey = GetUserSessionsKey(session.UserId);
                await _database.SetRemoveAsync(userSessionsKey, sessionId);

                _logger.LogInformation("Deleted session {SessionId}", sessionId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting session {SessionId}", sessionId);
                return false;
            }
        }

        public async Task<bool> SessionExistsAsync(string sessionId)
        {
            try
            {
                var key = GetSessionKey(sessionId);
                return await _database.KeyExistsAsync(key);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking session existence {SessionId}", sessionId);
                return false;
            }
        }

        public async Task<bool> UpdateLastActivityAsync(string sessionId)
        {
            try
            {
                var session = await GetSessionAsync(sessionId);
                if (session == null) return false;

                session.OrderState.LastUpdated = DateTime.UtcNow;
                return await SaveSessionAsync(session);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating last activity for session {SessionId}", sessionId);
                return false;
            }
        }

        public async Task<List<SessionDto>> GetUserSessionsAsync(string userId)
        {
            try
            {
                var userSessionsKey = GetUserSessionsKey(userId);
                var sessionIds = await _database.SetMembersAsync(userSessionsKey);

                var sessions = new List<SessionDto>();
                foreach (var sessionId in sessionIds)
                {
                    var session = await GetSessionAsync(sessionId!);
                    if (session != null)
                    {
                        sessions.Add(session);
                    }
                }

                return sessions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting user sessions for user {UserId}", userId);
                return new List<SessionDto>();
            }
        }

        private async Task<bool> SaveSessionAsync(SessionDto session)
        {
            try
            {
                var key = GetSessionKey(session.SessionId);
                var sessionJson = JsonSerializer.Serialize(session, _jsonOptions);
                var expiry = TimeSpan.FromHours(_config.SessionExpirationHours);

                await _database.StringSetAsync(key, sessionJson, expiry);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving session {SessionId}", session.SessionId);
                return false;
            }
        }

        /// <summary>
        /// Сравнивает два списка опций меню на равенство
        /// </summary>
        private static bool AreOptionsEqual(List<MenuItemOptionDto> options1, List<MenuItemOptionDto> options2)
        {
            if (options1.Count != options2.Count) return false;

            // Сортируем по ID для корректного сравнения
            var sorted1 = options1.OrderBy(o => o.Id).ToList();
            var sorted2 = options2.OrderBy(o => o.Id).ToList();

            for (int i = 0; i < sorted1.Count; i++)
            {
                if (sorted1[i].Id != sorted2[i].Id ||
                    sorted1[i].Name != sorted2[i].Name ||
                    sorted1[i].Type != sorted2[i].Type ||
                    sorted1[i].AdditionalPrice != sorted2[i].AdditionalPrice)
                {
                    return false;
                }
            }

            return true;
        }
    }
}