using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;

namespace FiboBlackGrail
{
    /// <summary>
    /// Главный cBot "Чёрный Грааль" — система торговли по Фибоначчи.
    ///
    /// РАБОТА:
    ///  1. На 1H ищем экстремумы → строим Фибо-сетку
    ///  2. Ждём касания 61.8% на 5M с фильтром 15M
    ///  3. Открываем сделку по алгоритму 1/2/3 (зависит от стадии)
    ///  4. При 80% хода → переносим стоп в БУ
    ///  5. При достижении 261% → перестраиваем сетку
    ///  6. Управление через Telegram
    /// </summary>
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
    public class FiboBlackGrailBot : Robot
    {
        // ─── Параметры ───────────────────────────────────────────────

        [Parameter("Railway Server URL", DefaultValue = "https://your-app.railway.app")]
        public string ServerUrl { get; set; }

        [Parameter("Bot Secret", DefaultValue = "change_me_secret")]
        public string BotSecret { get; set; }

        [Parameter("Swing Strength (свечей)", DefaultValue = 3, MinValue = 2, MaxValue = 10)]
        public int SwingStrength { get; set; }

        [Parameter("Таймфрейм старший", DefaultValue = "Hour1")]
        public string TfSenior { get; set; }

        [Parameter("Таймфрейм фильтр", DefaultValue = "Minute15")]
        public string TfFilter { get; set; }

        [Parameter("Таймфрейм вход", DefaultValue = "Minute5")]
        public string TfEntry { get; set; }

        [Parameter("Зона входа ±% от 61.8%", DefaultValue = 0.3, MinValue = 0.05, MaxValue = 1.0)]
        public double EntryZonePercent { get; set; }

        [Parameter("Символы (через запятую)", DefaultValue = "XAUUSD,XAGUSD,USOil,NAS100")]
        public string SymbolsList { get; set; }

        [Parameter("Опрос Telegram (секунд)", DefaultValue = 5, MinValue = 3, MaxValue = 30)]
        public int PollIntervalSeconds { get; set; }

        // ─── Внутренние поля ─────────────────────────────────────────

        private Dictionary<string, SymbolState> _states = new Dictionary<string, SymbolState>();
        private Dictionary<string, SwingDetector> _swingDetectors = new Dictionary<string, SwingDetector>();
        private Dictionary<string, Bars> _barsSenior = new Dictionary<string, Bars>();
        private Dictionary<string, Bars> _barsFilter = new Dictionary<string, Bars>();

        private RiskManager _riskManager;
        private TelegramBridge _telegram;

        private DateTime _lastPoll = DateTime.MinValue;
        private DateTime _lastDrawdownCheck = DateTime.MinValue;
        private bool _drawdownWarned = false;

        // ─── Инициализация ───────────────────────────────────────────

        protected override void OnStart()
        {
            Print("=== Чёрный Грааль | FiboAlex.com | Старт ===");

            _riskManager = new RiskManager(Account, Positions);
            _telegram = new TelegramBridge(ServerUrl, BotSecret, this);

            // Подписываем колбэки Telegram
            _telegram.OnStageChanged = HandleStageChanged;
            _telegram.OnPauseSymbol = HandlePauseSymbol;
            _telegram.OnResumeSymbol = HandleResumeSymbol;
            _telegram.OnStatusRequested = HandleStatusRequested;
            _telegram.OnReportRequested = HandleReportRequested;

            // Инициализируем каждый символ
            var symbols = SymbolsList.Split(',');
            foreach (var rawSymbol in symbols)
            {
                string sym = rawSymbol.Trim();
                if (string.IsNullOrEmpty(sym)) continue;

                InitializeSymbol(sym);
            }

            Positions.Closed += OnPositionClosed;

            _ = _telegram.SendNotificationAsync(
                $"🚀 Бот запущен\n" +
                $"Инструменты: {SymbolsList}\n" +
                $"Задай стадии командой:\n" +
                $"/stage XAUUSD 2\n\n" +
                $"{_riskManager.GetDrawdownStatus()}");

            Print("Бот инициализирован. Жду команды из Telegram...");
        }

        private void InitializeSymbol(string sym)
        {
            try
            {
                // Загружаем бары для старшего и фильтрующего ТФ
                var senior = MarketData.GetBars(ParseTimeFrame(TfSenior), sym);
                var filter = MarketData.GetBars(ParseTimeFrame(TfFilter), sym);

                _barsSenior[sym] = senior;
                _barsFilter[sym] = filter;

                // Инициализируем детектор экстремумов на старшем ТФ
                _swingDetectors[sym] = new SwingDetector(senior, SwingStrength);

                // Состояние символа
                _states[sym] = new SymbolState { Symbol = sym };

                Print($"[{sym}] Инициализирован. Свингов найдено: " +
                     $"High={_swingDetectors[sym].LastSwingHigh:F5} " +
                     $"Low={_swingDetectors[sym].LastSwingLow:F5}");
            }
            catch (Exception ex)
            {
                Print($"[{sym}] Ошибка инициализации: {ex.Message}");
            }
        }

        // ─── Основной цикл ───────────────────────────────────────────

        protected override void OnTick()
        {
            // Опрос Telegram команд
            if ((DateTime.UtcNow - _lastPoll).TotalSeconds >= PollIntervalSeconds)
            {
                _lastPoll = DateTime.UtcNow;
                _ = _telegram.PollCommandsAsync();
            }

            // Проверка просадки раз в минуту
            if ((DateTime.UtcNow - _lastDrawdownCheck).TotalSeconds >= 60)
            {
                _lastDrawdownCheck = DateTime.UtcNow;
                CheckDrawdown();
            }

            // Торговая логика для каждого символа
            foreach (var state in _states.Values)
            {
                if (!state.CanTrade) continue;
                CheckEntrySignal(state);
                CheckBreakEven(state);
                CheckGridRebuild(state);
            }
        }

        protected override void OnBar()
        {
            // При новой свече на основном ТФ — обновляем свинги
            foreach (var kvp in _swingDetectors)
            {
                string sym = kvp.Key;
                var detector = kvp.Value;

                bool newExtremum = detector.Update();
                if (newExtremum)
                {
                    Print($"[{sym}] Новый экстремум: H={detector.LastSwingHigh:F5} L={detector.LastSwingLow:F5}");

                    // Если задана стадия — перестраиваем сетку
                    if (_states.ContainsKey(sym) && _states[sym].Stage > 0)
                    {
                        RebuildGrid(_states[sym], "Новый экстремум");
                    }
                }
            }
        }

        // ─── Логика входа ────────────────────────────────────────────

        private void CheckEntrySignal(SymbolState state)
        {
            if (state.ActiveGrid == null) return;

            // Не открываем вход чаще чем раз в 5 минут
            if ((DateTime.UtcNow - state.LastEntryAttempt).TotalMinutes < 5) return;

            var symbolObj = Symbols.GetSymbol(state.Symbol);
            double bid = symbolObj.Bid;
            double ask = symbolObj.Ask;
            double entryLevel = state.ActiveGrid.GetEntryLevel();

            // Зона входа: ±% от уровня 61.8%
            double zone = entryLevel * EntryZonePercent / 100.0;
            bool priceAtLevel;

            if (state.ActiveGrid.IsBullish)
            {
                // Для бычьей сетки — ждём касания снизу
                priceAtLevel = bid >= entryLevel - zone && bid <= entryLevel + zone;
            }
            else
            {
                // Для медвежьей — ждём касания сверху
                priceAtLevel = ask >= entryLevel - zone && ask <= entryLevel + zone;
            }

            if (!priceAtLevel) return;

            // Фильтр 15M — нет противоположного импульса
            if (!CheckFilter15M(state)) return;

            // Проверяем можно ли открыть сделку по риску
            double riskPercent = state.RiskPercent;
            if (!_riskManager.CanOpenNewTrade(riskPercent, out string reason))
            {
                Print($"[{state.Symbol}] Вход заблокирован: {reason}");
                return;
            }

            // Открываем позицию
            OpenPosition(state, symbolObj);
            state.LastEntryAttempt = DateTime.UtcNow;
        }

        private bool CheckFilter15M(SymbolState state)
        {
            if (!_barsFilter.ContainsKey(state.Symbol)) return true;

            var bars15m = _barsFilter[state.Symbol];
            if (bars15m.Count < 3) return true;

            // Для бычьей сетки: последние 2 свечи 15M не должны быть сильно медвежьими
            double lastClose = bars15m.ClosePrices.Last(0);
            double prevClose = bars15m.ClosePrices.Last(1);
            double lastOpen = bars15m.OpenPrices.Last(0);

            if (state.ActiveGrid.IsBullish)
            {
                // Если последняя свеча 15M сильно медвежья (тело > 70% свечи) — пропускаем
                double body = lastOpen - lastClose;
                double range = bars15m.HighPrices.Last(0) - bars15m.LowPrices.Last(0);
                if (range > 0 && body / range > 0.7 && lastClose < lastOpen)
                {
                    Print($"[{state.Symbol}] Фильтр 15M: медвежий импульс, пропускаем вход");
                    return false;
                }
            }
            else
            {
                double body = lastClose - lastOpen;
                double range = bars15m.HighPrices.Last(0) - bars15m.LowPrices.Last(0);
                if (range > 0 && body / range > 0.7 && lastClose > lastOpen)
                {
                    Print($"[{state.Symbol}] Фильтр 15M: бычий импульс, пропускаем вход");
                    return false;
                }
            }

            return true;
        }

        private void OpenPosition(SymbolState state, Symbol symbolObj)
        {
            var grid = state.ActiveGrid;
            int algo = state.Algo;

            double entry = symbolObj.Ask;  // для Buy
            double sl = grid.GetStopLoss(algo);
            double tp = grid.GetTakeProfit(algo);

            if (!grid.IsBullish)
                entry = symbolObj.Bid;  // для Sell

            double lots = _riskManager.CalculateLotSize(
                symbolObj, entry, sl, state.RiskPercent, this);

            TradeType direction = grid.IsBullish ? TradeType.Buy : TradeType.Sell;

            var result = ExecuteMarketOrder(
                direction, state.Symbol, lots,
                label: $"FiboGrail_{state.Symbol}_A{algo}",
                stopLossPips: null,
                takeProfitPips: null
            );

            if (result.IsSuccessful)
            {
                var pos = result.Position;

                // Устанавливаем SL и TP в ценах
                ModifyPosition(pos, sl, tp);

                state.ActivePositionIds.Add(pos.Id);
                state.TotalTrades++;

                Print($"[{state.Symbol}] ✅ Открыта {direction} | " +
                     $"Вход={entry:F5} SL={sl:F5} TP={tp:F5} " +
                     $"Объём={lots} Алго={algo}");

                _telegram.NotifyTradeOpened(
                    state.Symbol, direction.ToString(), algo,
                    entry, sl, tp, lots, state.RiskPercent);
            }
            else
            {
                Print($"[{state.Symbol}] ❌ Ошибка открытия: {result.Error}");
            }
        }

        // ─── Управление позициями ────────────────────────────────────

        private void CheckBreakEven(SymbolState state)
        {
            if (state.ActiveGrid == null) return;

            foreach (int posId in state.ActivePositionIds.ToList())
            {
                var pos = Positions.FirstOrDefault(p => p.Id == posId);
                if (pos == null) continue;

                double beLevel = state.ActiveGrid.GetBreakEvenTrigger(state.Algo);
                bool shouldMoveToBE = false;

                if (state.ActiveGrid.IsBullish && pos.TradeType == TradeType.Buy)
                    shouldMoveToBE = pos.CurrentPrice >= beLevel && pos.StopLoss < pos.EntryPrice;
                else if (!state.ActiveGrid.IsBullish && pos.TradeType == TradeType.Sell)
                    shouldMoveToBE = pos.CurrentPrice <= beLevel && pos.StopLoss > pos.EntryPrice;

                if (shouldMoveToBE)
                {
                    double newSl = pos.EntryPrice;
                    ModifyPosition(pos, newSl, pos.TakeProfit);
                    Print($"[{state.Symbol}] 🔒 Стоп перенесён в БУ: {newSl:F5}");
                    _telegram.NotifyBreakEven(state.Symbol, newSl);
                }
            }
        }

        private void CheckGridRebuild(SymbolState state)
        {
            if (state.ActiveGrid == null) return;

            var symbolObj = Symbols.GetSymbol(state.Symbol);
            double price = (symbolObj.Bid + symbolObj.Ask) / 2;

            if (state.ActiveGrid.NeedsRebuild(price) && !state.ActiveGrid.Target261Reached)
            {
                state.ActiveGrid.Target261Reached = true;
                Print($"[{state.Symbol}] 🔄 Достигнут уровень 261% — перестройка сетки");
                RebuildGrid(state, "Достижение 261% цели");
            }
        }

        private void RebuildGrid(SymbolState state, string reason)
        {
            if (!_swingDetectors.ContainsKey(state.Symbol)) return;

            var detector = _swingDetectors[state.Symbol];

            FiboGrid newGrid = state.ActiveGrid?.IsBullish ?? true
                ? detector.BuildBullishGrid(state.Symbol, state.Stage)
                : detector.BuildBearishGrid(state.Symbol, state.Stage);

            if (newGrid != null)
            {
                state.ActiveGrid = newGrid;
                Print($"[{state.Symbol}] Сетка перестроена: {newGrid}");
                _telegram.NotifyGridRebuilt(state.Symbol, reason);
            }
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var pos = args.Position;

            // Ищем к какому символу относится
            foreach (var state in _states.Values)
            {
                if (state.ActivePositionIds.Contains(pos.Id))
                {
                    state.ActivePositionIds.Remove(pos.Id);
                    state.TotalPnL += pos.NetProfit;

                    if (pos.NetProfit >= 0) state.WinTrades++;

                    string reason = args.Reason.ToString();
                    Print($"[{pos.SymbolName}] Закрыта позиция: {pos.NetProfit:+0.00;-0.00} | {reason}");
                    _telegram.NotifyTradeClosed(pos.SymbolName, pos.TradeType.ToString(),
                                               pos.NetProfit, reason);
                    break;
                }
            }
        }

        // ─── Контроль просадки ───────────────────────────────────────

        private void CheckDrawdown()
        {
            double dd = _riskManager.GetCurrentDrawdownPercent();

            if (dd >= RiskManager.MaxTotalDrawdownPercent)
            {
                _telegram.NotifyDrawdownLimit(dd);
            }
            else if (dd >= RiskManager.WarnDrawdownPercent && !_drawdownWarned)
            {
                _drawdownWarned = true;
                _telegram.NotifyDrawdownWarning(dd);
            }
            else if (dd < RiskManager.WarnDrawdownPercent)
            {
                _drawdownWarned = false;
            }
        }

        // ─── Обработка Telegram команд ───────────────────────────────

        private void HandleStageChanged(string symbol, int stage)
        {
            if (symbol.ToUpper() == "ALL")
            {
                foreach (var state in _states.Values)
                {
                    SetStage(state, stage);
                }
                _ = _telegram.SendNotificationAsync($"✅ Стадия {stage} установлена для всех инструментов");
            }
            else if (_states.ContainsKey(symbol))
            {
                SetStage(_states[symbol], stage);
                _ = _telegram.SendNotificationAsync(
                    $"✅ {symbol}: Стадия {stage} | " +
                    $"Алго {_states[symbol].Algo} | " +
                    $"Риск {_states[symbol].RiskPercent}%");
            }
            else
            {
                _ = _telegram.SendNotificationAsync($"❌ Символ {symbol} не найден");
            }
        }

        private void SetStage(SymbolState state, int stage)
        {
            state.Stage = stage;

            // Определяем направление сетки по стадии
            bool buildBullish;
            switch (stage)
            {
                case 1: case 3: case 6: buildBullish = true; break;  // Тренд вверх
                case 2: buildBullish = false; break;                  // Контртренд
                case 4: case 5: buildBullish = true; break;           // По умолчанию
                default: buildBullish = true; break;
            }

            if (_swingDetectors.ContainsKey(state.Symbol))
            {
                var grid = buildBullish
                    ? _swingDetectors[state.Symbol].BuildBullishGrid(state.Symbol, stage)
                    : _swingDetectors[state.Symbol].BuildBearishGrid(state.Symbol, stage);

                if (grid != null)
                {
                    state.ActiveGrid = grid;
                    Print($"[{state.Symbol}] Стадия {stage} → {grid}");
                }
            }
        }

        private void HandlePauseSymbol(string symbol)
        {
            if (_states.ContainsKey(symbol))
            {
                _states[symbol].IsPaused = true;
                _ = _telegram.SendNotificationAsync($"⏸ {symbol} — торговля приостановлена");
            }
        }

        private void HandleResumeSymbol(string symbol)
        {
            if (_states.ContainsKey(symbol))
            {
                _states[symbol].IsPaused = false;
                _ = _telegram.SendNotificationAsync($"▶️ {symbol} — торговля возобновлена");
            }
        }

        private void HandleStatusRequested()
        {
            var lines = new System.Text.StringBuilder();
            lines.AppendLine(_riskManager.GetDrawdownStatus());
            lines.AppendLine("─────────────────");

            foreach (var state in _states.Values)
            {
                lines.AppendLine(state.GetStatusLine());
            }

            lines.AppendLine("─────────────────");
            lines.AppendLine($"Открытых позиций: {Positions.Count}");

            _telegram.SendStatus(lines.ToString());
        }

        private void HandleReportRequested()
        {
            var lines = new System.Text.StringBuilder();
            lines.AppendLine("📈 ДНЕВНОЙ ОТЧЁТ");
            lines.AppendLine("─────────────────");

            double totalPnL = 0;
            int totalTrades = 0;
            int totalWins = 0;

            foreach (var state in _states.Values)
            {
                if (state.TotalTrades > 0)
                {
                    double wr = state.TotalTrades > 0
                        ? (double)state.WinTrades / state.TotalTrades * 100 : 0;
                    lines.AppendLine($"{state.Symbol}: {state.TotalTrades} сделок | " +
                                    $"WR {wr:F0}% | P&L {state.TotalPnL:+0.00;-0.00}$");
                    totalPnL += state.TotalPnL;
                    totalTrades += state.TotalTrades;
                    totalWins += state.WinTrades;
                }
            }

            lines.AppendLine("─────────────────");
            double totalWR = totalTrades > 0 ? (double)totalWins / totalTrades * 100 : 0;
            lines.AppendLine($"ИТОГО: {totalTrades} сделок | WR {totalWR:F0}% | P&L {totalPnL:+0.00;-0.00}$");
            lines.AppendLine(_riskManager.GetDrawdownStatus());

            _telegram.SendStatus(lines.ToString());
        }

        // ─── Утилиты ─────────────────────────────────────────────────

        private TimeFrame ParseTimeFrame(string tf)
        {
            return tf switch
            {
                "Minute5"  => TimeFrame.Minute5,
                "Minute15" => TimeFrame.Minute15,
                "Minute30" => TimeFrame.Minute30,
                "Hour1"    => TimeFrame.Hour,
                "Hour4"    => TimeFrame.Hour4,
                "Daily"    => TimeFrame.Daily,
                _          => TimeFrame.Hour
            };
        }

        protected override void OnStop()
        {
            _ = _telegram.SendNotificationAsync("⏹ Бот остановлен");
            Print("=== Чёрный Грааль | Остановлен ===");
        }
    }
}
