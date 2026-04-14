using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using cAlgo.API;
using cAlgo.API.Internals;

/// <summary>
/// ==========================================
///   ЧЁРНЫЙ ГРААЛЬ — FiboAlex.com
///   Торговый бот по системе Фибоначчи
///   Telegram: управление стадиями и сделками
/// ==========================================
/// </summary>
namespace cAlgo.Robots
{
    // ════════════════════════════════════════════════════════════════
    //  FIBO GRID — уровни Фибоначчи
    // ════════════════════════════════════════════════════════════════

    public class FiboGrid
    {
        public double SwingLow { get; private set; }
        public double SwingHigh { get; private set; }
        public bool IsBullish { get; private set; }
        public string Symbol { get; private set; }
        public int Stage { get; set; }

        public double Level0 { get; private set; }
        public double Level50 { get; private set; }
        public double Level61 { get; private set; }
        public double Level100 { get; private set; }
        public double Level123 { get; private set; }
        public double Level161 { get; private set; }
        public double Level261 { get; private set; }

        public bool Target261Reached { get; set; }

        public FiboGrid(string symbol, double low, double high, bool isBullish, int stage)
        {
            Symbol = symbol;
            SwingLow = low;
            SwingHigh = high;
            IsBullish = isBullish;
            Stage = stage;
            Calculate();
        }

        private void Calculate()
        {
            double r = SwingHigh - SwingLow;
            if (IsBullish)
            {
                Level0   = SwingLow;
                Level50  = SwingLow + r * 0.500;
                Level61  = SwingLow + r * 0.618;
                Level100 = SwingHigh;
                Level123 = SwingLow + r * 1.236;
                Level161 = SwingLow + r * 1.618;
                Level261 = SwingLow + r * 2.618;
            }
            else
            {
                Level0   = SwingHigh;
                Level50  = SwingHigh - r * 0.500;
                Level61  = SwingHigh - r * 0.618;
                Level100 = SwingLow;
                Level123 = SwingHigh - r * 1.236;
                Level161 = SwingHigh - r * 1.618;
                Level261 = SwingHigh - r * 2.618;
            }
        }

        public double GetEntry() => Level61;

        public double GetStopLoss(int algo)
        {
            double extra = (SwingHigh - SwingLow) * 0.85;
            if (algo == 3)
                return IsBullish ? Level0 - extra : Level0 + extra;
            return Level0;
        }

        public double GetTakeProfit(int algo)
        {
            if (algo == 1) return Level123;
            if (algo == 3) return Level261;
            return Level161;
        }

        // 80% хода от входа до тейка → переносим стоп в БУ
        public double GetBreakEvenTrigger(int algo)
        {
            double entry = GetEntry();
            double tp = GetTakeProfit(algo);
            return IsBullish
                ? entry + (tp - entry) * 0.80
                : entry - (entry - tp) * 0.80;
        }

        public bool NeedsRebuild(double price) =>
            IsBullish ? price >= Level261 : price <= Level261;

        public override string ToString() =>
            $"[{Symbol} {(IsBullish ? "BULL" : "BEAR")}] " +
            $"0%={Level0:F5} 61%={Level61:F5} 161%={Level161:F5} 261%={Level261:F5}";
    }

    // ════════════════════════════════════════════════════════════════
    //  SWING DETECTOR — поиск экстремумов по теням свечей
    // ════════════════════════════════════════════════════════════════

    public class SwingDetector
    {
        private readonly Bars _bars;
        private readonly int _strength;

        public double LastHigh { get; private set; }
        public double LastLow { get; private set; }

        public SwingDetector(Bars bars, int strength)
        {
            _bars = bars;
            _strength = strength;
            LastHigh = 0;
            LastLow = double.MaxValue;
            ScanHistory();
        }

        private void ScanHistory()
        {
            int n = _bars.Count;
            int found = 0;
            for (int i = n - _strength - 1; i >= _strength && found < 1; i--)
                if (IsSwingHigh(i)) { LastHigh = _bars.HighPrices[i]; found++; }

            found = 0;
            for (int i = n - _strength - 1; i >= _strength && found < 1; i--)
                if (IsSwingLow(i)) { LastLow = _bars.LowPrices[i]; found++; }
        }

        public bool Update()
        {
            bool changed = false;
            int idx = _strength;

            if (IsSwingHigh(idx))
            {
                double h = _bars.HighPrices[idx];
                if (Math.Abs(h - LastHigh) > 1e-10) { LastHigh = h; changed = true; }
            }
            if (IsSwingLow(idx))
            {
                double l = _bars.LowPrices[idx];
                if (Math.Abs(l - LastLow) > 1e-10) { LastLow = l; changed = true; }
            }
            return changed;
        }

        private bool IsSwingHigh(int i)
        {
            if (i < _strength || i >= _bars.Count - _strength) return false;
            double h = _bars.HighPrices[i];
            for (int j = 1; j <= _strength; j++)
                if (_bars.HighPrices[i - j] >= h || _bars.HighPrices[i + j] >= h) return false;
            return true;
        }

        private bool IsSwingLow(int i)
        {
            if (i < _strength || i >= _bars.Count - _strength) return false;
            double l = _bars.LowPrices[i];
            for (int j = 1; j <= _strength; j++)
                if (_bars.LowPrices[i - j] <= l || _bars.LowPrices[i + j] <= l) return false;
            return true;
        }

        public FiboGrid BuildGrid(string symbol, int stage, bool bullish)
        {
            if (LastLow >= LastHigh) return null;
            return new FiboGrid(symbol, LastLow, LastHigh, bullish, stage);
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  ALGO SELECTOR — выбор алгоритма по стадии
    // ════════════════════════════════════════════════════════════════

    public static class AlgoSelector
    {
        // Стадии 1,3,6=Тренд → Алго2  |  2=Контртренд → Алго1  |  4,5=Коррекция/Флет → Алго3
        public static int GetAlgo(int stage)
        {
            if (stage == 2) return 1;
            if (stage == 4 || stage == 5) return 3;
            return 2;
        }

        public static double GetRisk(int algo)
        {
            if (algo == 1) return 3.0;
            if (algo == 3) return 7.0;
            return 5.0;
        }

        public static bool IsBullishStage(int stage) =>
            stage == 1 || stage == 3 || stage == 6;
    }

    // ════════════════════════════════════════════════════════════════
    //  SYMBOL STATE — состояние одного инструмента
    // ════════════════════════════════════════════════════════════════

    public class SymbolState
    {
        public string Symbol;
        public int Stage = 0;
        public bool IsPaused = false;
        public FiboGrid Grid = null;
        public List<int> PositionIds = new List<int>();
        public DateTime LastEntry = DateTime.MinValue;
        public int Algo => Stage > 0 ? AlgoSelector.GetAlgo(Stage) : 2;
        public double Risk => AlgoSelector.GetRisk(Algo);
        public bool CanTrade => !IsPaused && Stage > 0 && Grid != null;

        public string StatusLine()
        {
            string s = Stage > 0 ? $"Стадия {Stage}" : "Стадия не задана";
            string p = IsPaused ? " [ПАУЗА]" : "";
            string g = Grid != null ? $"| 61%={Grid.Level61:F5}" : "| нет сетки";
            return $"{Symbol}: {s}{p} | Алго {Algo} {g}";
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  TELEGRAM BRIDGE — связь с Railway сервером
    // ════════════════════════════════════════════════════════════════

    public class TelegramBridge
    {
        private readonly string _url;
        private readonly string _secret;
        private readonly Robot _bot;
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        public Action<string, int> OnStage;
        public Action<string> OnPause;
        public Action<string> OnResume;
        public Action OnStatus;
        public Action OnReport;

        public TelegramBridge(string url, string secret, Robot bot)
        {
            _url = url.TrimEnd('/');
            _secret = secret;
            _bot = bot;
            _http.DefaultRequestHeaders.Remove("X-Bot-Secret");
            _http.DefaultRequestHeaders.Add("X-Bot-Secret", secret);
        }

        public async Task PollAsync()
        {
            try
            {
                var resp = await _http.GetAsync($"{_url}/commands/poll");
                if (!resp.IsSuccessStatusCode) return;
                var json = await resp.Content.ReadAsStringAsync();
                ParseCommands(json);
            }
            catch { }
        }

        private void ParseCommands(string json)
        {
            // Простой парсинг без Newtonsoft — ищем "command" и "args" в JSON
            if (string.IsNullOrEmpty(json) || json == "[]") return;

            // Разбиваем массив на объекты
            var items = json.Split(new[] { "},{", "}, {" }, StringSplitOptions.None);
            foreach (var item in items)
            {
                string cmd = ExtractJsonString(item, "command");
                string args = ExtractJsonString(item, "args");
                if (string.IsNullOrEmpty(cmd)) continue;

                _bot.Print($"[TG] Команда: {cmd} {args}");

                switch (cmd.ToLower())
                {
                    case "/stage":
                        var parts = args.Trim().Split(' ');
                        if (parts.Length >= 2 && int.TryParse(parts[1], out int st))
                            OnStage?.Invoke(parts[0].ToUpper(), st);
                        break;
                    case "/pause":   OnPause?.Invoke(args.Trim().ToUpper()); break;
                    case "/resume":  OnResume?.Invoke(args.Trim().ToUpper()); break;
                    case "/status":  OnStatus?.Invoke(); break;
                    case "/report":  OnReport?.Invoke(); break;
                }
            }
        }

        private string ExtractJsonString(string json, string key)
        {
            string search = $"\"{key}\":\"";
            int start = json.IndexOf(search);
            if (start < 0) return "";
            start += search.Length;
            int end = json.IndexOf("\"", start);
            if (end < 0) return "";
            return json.Substring(start, end - start);
        }

        public async Task SendAsync(string text)
        {
            try
            {
                // Экранируем кавычки в тексте
                string safe = text.Replace("\"", "'").Replace("\n", "\\n");
                var body = $"{{\"text\":\"{safe}\"}}";
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                await _http.PostAsync($"{_url}/notify", content);
            }
            catch { }
        }

        public void TradeOpened(string sym, string dir, int algo, double e, double sl, double tp, double lots, double risk)
        {
            string d = dir == "Buy" ? "📈 BUY" : "📉 SELL";
            string algoDesc = algo == 1 ? "Алго1 (23→123)" : algo == 2 ? "Алго2 (61→161)" : "Алго3 (101→261)";
            _ = SendAsync($"{d} {sym}\\n{algoDesc}\\nВход: {e:F5}\\nСтоп: {sl:F5}\\nТейк: {tp:F5}\\nОбъём: {lots} | Риск: {risk:F1}%");
        }

        public void TradeClosed(string sym, string dir, double pnl, string reason)
        {
            string e = pnl >= 0 ? "✅" : "❌";
            _ = SendAsync($"{e} Закрыта {dir} {sym}\\nP&L: {pnl:+0.00;-0.00}$\\n{reason}");
        }

        public void BreakEven(string sym, double sl) =>
            _ = SendAsync($"🔒 {sym} — стоп в БУ: {sl:F5}");

        public void GridRebuilt(string sym, string reason) =>
            _ = SendAsync($"🔄 {sym} — сетка перестроена: {reason}");

        public void DrawdownWarn(double pct) =>
            _ = SendAsync($"⚠️ Просадка {pct:F1}% — приближается лимит 20%");

        public void DrawdownLimit(double pct) =>
            _ = SendAsync($"🛑 Просадка {pct:F1}% — новые входы заблокированы!");

        public void Notify(string text) => _ = SendAsync(text);
    }

    // ════════════════════════════════════════════════════════════════
    //  ГЛАВНЫЙ CBOT
    // ════════════════════════════════════════════════════════════════

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
    public class FiboBlackGrail : Robot
    {
        [Parameter("Railway Server URL", DefaultValue = "https://tradingbot-production-f8ae.up.railway.app")]
        public string ServerUrl { get; set; }

        [Parameter("Bot Secret", DefaultValue = "BlackGrail2026")]
        public string BotSecret { get; set; }

        [Parameter("Символы (через запятую)", DefaultValue = "XAUUSD,XAGUSD,BRENT,#USSPX500,EURUSD,GBPUSD,USDJPY,AUDUSD,USDCAD,USDCHF,NZDUSD,BITCOIN")]
        public string SymbolsList { get; set; }

        [Parameter("Swing Strength (свечей)", DefaultValue = 3, MinValue = 2, MaxValue = 8)]
        public int SwingStrength { get; set; }

        [Parameter("Зона входа ±% от 61.8%", DefaultValue = 0.3, MinValue = 0.05, MaxValue = 1.5)]
        public double EntryZonePct { get; set; }

        [Parameter("Макс просадка %", DefaultValue = 20.0, MinValue = 5.0, MaxValue = 50.0)]
        public double MaxDrawdown { get; set; }

        // ── Внутренние поля ──────────────────────────────────────────

        private Dictionary<string, SymbolState> _states = new Dictionary<string, SymbolState>();
        private Dictionary<string, SwingDetector> _swings = new Dictionary<string, SwingDetector>();
        private Dictionary<string, Bars> _bars1H = new Dictionary<string, Bars>();
        private Dictionary<string, Bars> _bars15M = new Dictionary<string, Bars>();

        private TelegramBridge _tg;
        private DateTime _lastPoll = DateTime.MinValue;
        private DateTime _lastDDCheck = DateTime.MinValue;
        private bool _ddWarned = false;

        // ── Инициализация ─────────────────────────────────────────────

        protected override void OnStart()
        {
            Print("=== ЧЁРНЫЙ ГРААЛЬ | FiboAlex.com | Старт ===");

            _tg = new TelegramBridge(ServerUrl, BotSecret, this);
            _tg.OnStage = HandleStage;
            _tg.OnPause = sym => SetPause(sym, true);
            _tg.OnResume = sym => SetPause(sym, false);
            _tg.OnStatus = SendStatus;
            _tg.OnReport = SendReport;

            foreach (var raw in SymbolsList.Split(','))
            {
                string sym = raw.Trim();
                if (string.IsNullOrEmpty(sym)) continue;
                try
                {
                    _bars1H[sym] = MarketData.GetBars(TimeFrame.Hour, sym);
                    _bars15M[sym] = MarketData.GetBars(TimeFrame.Minute15, sym);
                    _swings[sym] = new SwingDetector(_bars1H[sym], SwingStrength);
                    _states[sym] = new SymbolState { Symbol = sym };
                    Print($"[{sym}] OK — H={_swings[sym].LastHigh:F5} L={_swings[sym].LastLow:F5}");
                }
                catch (Exception ex) { Print($"[{sym}] Ошибка: {ex.Message}"); }
            }

            Positions.Closed += OnPosClosed;

            _tg.Notify($"🚀 Чёрный Грааль запущен\\nИнструменты: {SymbolsList}\\nЗадай стадии: /stage XAUUSD 1");
            Print("Жду команды из Telegram...");
        }

        // ── Основной цикл ─────────────────────────────────────────────

        protected override void OnTick()
        {
            // Telegram polling
            if ((DateTime.UtcNow - _lastPoll).TotalSeconds >= 5)
            {
                _lastPoll = DateTime.UtcNow;
                _ = _tg.PollAsync();
            }

            // Проверка просадки раз в минуту
            if ((DateTime.UtcNow - _lastDDCheck).TotalSeconds >= 60)
            {
                _lastDDCheck = DateTime.UtcNow;
                CheckDrawdown();
            }

            // Торговля по каждому символу
            foreach (var st in _states.Values)
            {
                if (!st.CanTrade) continue;
                CheckEntry(st);
                CheckBreakEven(st);
                CheckRebuild(st);
            }
        }

        protected override void OnBar()
        {
            // Обновляем свинги на каждой новой 1H свече
            foreach (var kv in _swings)
            {
                if (kv.Value.Update() && _states.ContainsKey(kv.Key) && _states[kv.Key].Stage > 0)
                    RebuildGrid(_states[kv.Key], "Новый экстремум 1H");
            }
        }

        // ── Вход в сделку ─────────────────────────────────────────────

        private void CheckEntry(SymbolState st)
        {
            // Только одна открытая позиция на инструмент
            if (st.PositionIds.Count > 0) return;

            if ((DateTime.UtcNow - st.LastEntry).TotalMinutes < 5) return;

            var sym = Symbols.GetSymbol(st.Symbol);
            double price = (sym.Bid + sym.Ask) / 2;
            double entry = st.Grid.GetEntry();
            double zone = entry * EntryZonePct / 100.0;

            bool atLevel = Math.Abs(price - entry) <= zone;
            if (!atLevel) return;

            if (!PassFilter15M(st)) return;
            if (!CanOpenByDrawdown(st.Risk)) return;

            OpenTrade(st, sym);
            st.LastEntry = DateTime.UtcNow;
        }

        private bool PassFilter15M(SymbolState st)
        {
            if (!_bars15M.ContainsKey(st.Symbol)) return true;
            var b = _bars15M[st.Symbol];
            if (b.Count < 3) return true;

            double body = Math.Abs(b.ClosePrices.Last(0) - b.OpenPrices.Last(0));
            double range = b.HighPrices.Last(0) - b.LowPrices.Last(0);
            if (range < 1e-10) return true;

            bool strongBear = (b.ClosePrices.Last(0) < b.OpenPrices.Last(0)) && (body / range > 0.7);
            bool strongBull = (b.ClosePrices.Last(0) > b.OpenPrices.Last(0)) && (body / range > 0.7);

            if (st.Grid.IsBullish && strongBear) { Print($"[{st.Symbol}] Фильтр 15M: медвежий импульс"); return false; }
            if (!st.Grid.IsBullish && strongBull) { Print($"[{st.Symbol}] Фильтр 15M: бычий импульс"); return false; }
            return true;
        }

        private void OpenTrade(SymbolState st, Symbol sym)
        {
            int algo = st.Algo;
            double sl = st.Grid.GetStopLoss(algo);
            double tp = st.Grid.GetTakeProfit(algo);
            TradeType dir = st.Grid.IsBullish ? TradeType.Buy : TradeType.Sell;
            double entry = dir == TradeType.Buy ? sym.Ask : sym.Bid;

            double lots = CalcLots(sym, entry, sl, st.Risk);

            var res = ExecuteMarketOrder(dir, st.Symbol, lots, $"FG_{st.Symbol}_A{algo}");
            if (!res.IsSuccessful) { Print($"[{st.Symbol}] Ошибка ордера: {res.Error}"); return; }

            ModifyPosition(res.Position, sl, tp, ProtectionType.Absolute);
            st.PositionIds.Add(res.Position.Id);

            Print($"[{st.Symbol}] ✅ {dir} | Entry={entry:F5} SL={sl:F5} TP={tp:F5} Lots={lots}");
            _tg.TradeOpened(st.Symbol, dir.ToString(), algo, entry, sl, tp, lots, st.Risk);
        }

        private double CalcLots(Symbol sym, double entry, double sl, double riskPct)
        {
            double riskAmt = Account.Balance * riskPct / 100.0;
            double dist = Math.Abs(entry - sl);
            double pips = dist / sym.PipSize;
            if (pips < 0.001) return sym.VolumeInUnitsMin;

            double vol = riskAmt / (pips * sym.PipValue);
            double step = sym.VolumeInUnitsStep;
            vol = Math.Floor(vol / step) * step;
            return Math.Max(sym.VolumeInUnitsMin, Math.Min(sym.VolumeInUnitsMax, vol));
        }

        // ── Управление позициями ──────────────────────────────────────

        private void CheckBreakEven(SymbolState st)
        {
            foreach (int id in st.PositionIds.ToList())
            {
                var pos = Positions.FirstOrDefault(p => p.Id == id);
                if (pos == null) continue;

                double beLevel = st.Grid.GetBreakEvenTrigger(st.Algo);
                bool move = st.Grid.IsBullish
                    ? pos.CurrentPrice >= beLevel && pos.StopLoss < pos.EntryPrice
                    : pos.CurrentPrice <= beLevel && (pos.StopLoss > pos.EntryPrice || pos.StopLoss == 0);

                if (move)
                {
                    ModifyPosition(pos, pos.EntryPrice, pos.TakeProfit, ProtectionType.Absolute);
                    Print($"[{st.Symbol}] 🔒 БУ → {pos.EntryPrice:F5}");
                    _tg.BreakEven(st.Symbol, pos.EntryPrice);
                }
            }
        }

        private void CheckRebuild(SymbolState st)
        {
            var sym = Symbols.GetSymbol(st.Symbol);
            double price = (sym.Bid + sym.Ask) / 2;
            if (!st.Grid.Target261Reached && st.Grid.NeedsRebuild(price))
            {
                st.Grid.Target261Reached = true;
                RebuildGrid(st, "Достижение 261%");
            }
        }

        private void RebuildGrid(SymbolState st, string reason)
        {
            if (!_swings.ContainsKey(st.Symbol)) return;
            bool bull = AlgoSelector.IsBullishStage(st.Stage);
            var grid = _swings[st.Symbol].BuildGrid(st.Symbol, st.Stage, bull);
            if (grid != null)
            {
                st.Grid = grid;
                Print($"[{st.Symbol}] Сетка: {grid}");
                _tg.GridRebuilt(st.Symbol, reason);
            }
        }

        private void OnPosClosed(PositionClosedEventArgs e)
        {
            var pos = e.Position;
            foreach (var st in _states.Values)
            {
                if (!st.PositionIds.Contains(pos.Id)) continue;
                st.PositionIds.Remove(pos.Id);
                _tg.TradeClosed(pos.SymbolName, pos.TradeType.ToString(),
                                pos.NetProfit, e.Reason.ToString());
                break;
            }
        }

        // ── Управление рисками ────────────────────────────────────────

        private double GetDrawdownPct()
        {
            double loss = Positions.Where(p => p.NetProfit < 0).Sum(p => Math.Abs(p.NetProfit));
            return Account.Balance > 0 ? loss / Account.Balance * 100 : 0;
        }

        private bool CanOpenByDrawdown(double risk)
        {
            double dd = GetDrawdownPct();
            if (dd >= MaxDrawdown) { Print($"Просадка {dd:F1}% ≥ лимита — вход заблокирован"); return false; }
            return true;
        }

        private void CheckDrawdown()
        {
            double dd = GetDrawdownPct();
            if (dd >= MaxDrawdown && !_ddWarned) { _ddWarned = true; _tg.DrawdownLimit(dd); }
            else if (dd >= MaxDrawdown * 0.75 && !_ddWarned) { _ddWarned = true; _tg.DrawdownWarn(dd); }
            else if (dd < MaxDrawdown * 0.5) _ddWarned = false;
        }

        // ── Telegram команды ──────────────────────────────────────────

        private void HandleStage(string symbol, int stage)
        {
            if (symbol == "ALL")
            {
                foreach (var st in _states.Values) ApplyStage(st, stage);
                _tg.Notify($"✅ Стадия {stage} установлена для всех");
            }
            else if (_states.ContainsKey(symbol))
            {
                ApplyStage(_states[symbol], stage);
                var st = _states[symbol];
                _tg.Notify($"✅ {symbol}: Стадия {stage} | Алго {st.Algo} | Риск {st.Risk}%");
            }
            else _tg.Notify($"❌ Символ {symbol} не найден");
        }

        private void ApplyStage(SymbolState st, int stage)
        {
            st.Stage = stage;
            RebuildGrid(st, $"Стадия {stage}");
        }

        private void SetPause(string symbol, bool pause)
        {
            if (symbol == "ALL") { foreach (var st in _states.Values) st.IsPaused = pause; }
            else if (_states.ContainsKey(symbol)) _states[symbol].IsPaused = pause;
            string action = pause ? "приостановлена ⏸" : "возобновлена ▶️";
            _tg.Notify($"{symbol}: торговля {action}");
        }

        private void SendStatus()
        {
            var sb = new StringBuilder();
            sb.Append($"📊 СТАТУС\\n");
            sb.Append($"Просадка: {GetDrawdownPct():F1}% / {MaxDrawdown}%\\n");
            sb.Append($"Позиций: {Positions.Count}\\n---\\n");
            foreach (var st in _states.Values) sb.Append(st.StatusLine() + "\\n");
            _tg.Notify(sb.ToString());
        }

        private void SendReport()
        {
            double total = Positions.Sum(p => p.NetProfit);
            _tg.Notify($"📈 ОТЧЁТ\\nБаланс: {Account.Balance:F2}\\nP&L открытых: {total:+0.00;-0.00}$\\nПросадка: {GetDrawdownPct():F1}%");
        }

        protected override void OnStop()
        {
            _tg.Notify("⏹ Бот остановлен");
            Print("=== ЧЁРНЫЙ ГРААЛЬ | Остановлен ===");
        }
    }
}
