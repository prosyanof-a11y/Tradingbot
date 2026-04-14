using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using cAlgo.API;
using Newtonsoft.Json;

namespace FiboBlackGrail
{
    /// <summary>
    /// Мост между cBot и Telegram-сервером на Railway.
    /// cBot:
    ///   - Опрашивает Railway каждые 5 секунд на предмет команд (long-poll)
    ///   - Отправляет уведомления о сделках на Railway
    /// </summary>
    public class TelegramBridge
    {
        private readonly string _serverUrl;
        private readonly string _botSecret;
        private readonly Robot _robot;
        private static readonly HttpClient _http = new HttpClient();

        // Колбэки для обработки команд из Telegram
        public Action<string, int> OnStageChanged;    // (symbol, stage)
        public Action<string> OnPauseSymbol;          // (symbol)
        public Action<string> OnResumeSymbol;         // (symbol)
        public Action OnReportRequested;
        public Action OnStatusRequested;

        public TelegramBridge(string serverUrl, string botSecret, Robot robot)
        {
            _serverUrl = serverUrl.TrimEnd('/');
            _botSecret = botSecret;
            _robot = robot;
            _http.Timeout = TimeSpan.FromSeconds(10);
            _http.DefaultRequestHeaders.Add("X-Bot-Secret", _botSecret);
        }

        /// <summary>
        /// Опрашивает сервер на предмет новых команд.
        /// Вызывать в Timer или OnTick с debounce.
        /// </summary>
        public async Task PollCommandsAsync()
        {
            try
            {
                var response = await _http.GetAsync($"{_serverUrl}/commands/poll");
                if (!response.IsSuccessStatusCode) return;

                var json = await response.Content.ReadAsStringAsync();
                var commands = JsonConvert.DeserializeObject<List<BotCommand>>(json);
                if (commands == null || commands.Count == 0) return;

                foreach (var cmd in commands)
                {
                    ProcessCommand(cmd);
                }
            }
            catch (Exception ex)
            {
                _robot.Print($"[Telegram] Ошибка опроса: {ex.Message}");
            }
        }

        private void ProcessCommand(BotCommand cmd)
        {
            _robot.Print($"[Telegram] Команда: {cmd.Command} {cmd.Args}");

            switch (cmd.Command.ToLower())
            {
                case "/stage":
                    var parts = cmd.Args.Split(' ');
                    if (parts.Length >= 2 && int.TryParse(parts[1], out int stage))
                    {
                        string symbol = parts[0].ToUpper();
                        OnStageChanged?.Invoke(symbol, stage);
                    }
                    break;

                case "/pause":
                    OnPauseSymbol?.Invoke(cmd.Args.ToUpper());
                    break;

                case "/resume":
                    OnResumeSymbol?.Invoke(cmd.Args.ToUpper());
                    break;

                case "/status":
                    OnStatusRequested?.Invoke();
                    break;

                case "/report":
                    OnReportRequested?.Invoke();
                    break;
            }
        }

        /// <summary>
        /// Отправляет уведомление в Telegram через Railway-сервер
        /// </summary>
        public async Task SendNotificationAsync(string message)
        {
            try
            {
                var payload = JsonConvert.SerializeObject(new { text = message });
                var content = new StringContent(payload, Encoding.UTF8, "application/json");
                await _http.PostAsync($"{_serverUrl}/notify", content);
            }
            catch (Exception ex)
            {
                _robot.Print($"[Telegram] Ошибка отправки: {ex.Message}");
            }
        }

        // Удобные методы для типовых сообщений

        public void NotifyTradeOpened(string symbol, string direction, int algo,
                                      double entry, double sl, double tp,
                                      double lots, double riskPercent)
        {
            string dir = direction == "Buy" ? "📈 BUY" : "📉 SELL";
            string msg = $"{dir} {symbol}\n" +
                        $"🎯 {AlgoSelector.GetDescription(algo)}\n" +
                        $"💰 Вход: {entry:F5}\n" +
                        $"🛑 Стоп: {sl:F5}\n" +
                        $"✅ Тейк: {tp:F5}\n" +
                        $"📊 Объём: {lots} лот | Риск: {riskPercent:F1}%";
            _ = SendNotificationAsync(msg);
        }

        public void NotifyTradeClosed(string symbol, string direction,
                                      double profit, string reason)
        {
            string emoji = profit >= 0 ? "✅" : "❌";
            string dir = direction == "Buy" ? "BUY" : "SELL";
            string msg = $"{emoji} Закрыта {dir} {symbol}\n" +
                        $"💵 P&L: {profit:+0.00;-0.00} USD\n" +
                        $"📝 Причина: {reason}";
            _ = SendNotificationAsync(msg);
        }

        public void NotifyBreakEven(string symbol, double newSl)
        {
            string msg = $"🔒 {symbol} — стоп перенесён в БУ\n" +
                        $"Новый стоп: {newSl:F5}";
            _ = SendNotificationAsync(msg);
        }

        public void NotifyGridRebuilt(string symbol, string reason)
        {
            string msg = $"🔄 {symbol} — сетка перестроена\n" +
                        $"Причина: {reason}";
            _ = SendNotificationAsync(msg);
        }

        public void NotifyDrawdownWarning(double percent)
        {
            string msg = $"⚠️ ВНИМАНИЕ: Просадка {percent:F1}%\n" +
                        $"Лимит: {RiskManager.MaxTotalDrawdownPercent}%";
            _ = SendNotificationAsync(msg);
        }

        public void NotifyDrawdownLimit(double percent)
        {
            string msg = $"🛑 ЛИМИТ ПРОСАДКИ ДОСТИГНУТ: {percent:F1}%\n" +
                        $"Новые входы ЗАБЛОКИРОВАНЫ до снижения просадки!";
            _ = SendNotificationAsync(msg);
        }

        public void SendStatus(string statusText)
        {
            _ = SendNotificationAsync($"📊 СТАТУС\n{statusText}");
        }
    }

    public class BotCommand
    {
        [JsonProperty("command")]
        public string Command { get; set; }

        [JsonProperty("args")]
        public string Args { get; set; }

        [JsonProperty("id")]
        public string Id { get; set; }
    }
}
