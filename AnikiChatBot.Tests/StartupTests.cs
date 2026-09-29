using Telegram.Bot.Exceptions;

namespace AnikiChatBot.Tests
{
    public class StartupTests
    {
        [Fact]
        public async Task Waits_for_network_with_growing_delays()
        {
            int calls = 0;
            var waits = new List<TimeSpan>();

            string result = await BotWorker.WaitForTelegramAsync(() =>
            {
                calls++;
                if (calls <= 6)
                    throw new RequestException("Bot API Service Failure", new HttpRequestException("Этот хост неизвестен."));
                return Task.FromResult("ok");
            }, CancellationToken.None, (wait, _) => { waits.Add(wait); return Task.CompletedTask; });

            Assert.Equal("ok", result);
            Assert.Equal(7, calls);
            Assert.Equal([5, 10, 20, 40, 60, 60], waits.Select(w => w.TotalSeconds));
        }

        [Fact]
        public async Task Telegram_api_errors_are_not_retried()
        {
            int calls = 0;

            await Assert.ThrowsAsync<ApiRequestException>(() => BotWorker.WaitForTelegramAsync<string>(() =>
            {
                calls++;
                throw new ApiRequestException("Unauthorized", 401);
            }, CancellationToken.None, (_, _) => Task.CompletedTask));

            Assert.Equal(1, calls);
        }
    }
}
