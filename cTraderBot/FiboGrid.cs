using System;
using System.Collections.Generic;

namespace FiboBlackGrail
{
    /// <summary>
    /// Представляет уровни Фибоначчи, построенные от двух экстремумов.
    /// Уровни: 0, 23.6, 38.2, 50, 61.8, 100, 123.6, 161.8, 261.8, 423.6, 685.4
    /// </summary>
    public class FiboGrid
    {
        public DateTime StartTime { get; private set; }
        public DateTime EndTime { get; private set; }

        // Экстремумы: SwingLow = 0%, SwingHigh = 100% (для бычьей сетки)
        public double SwingLow { get; private set; }
        public double SwingHigh { get; private set; }
        public bool IsBullish { get; private set; }  // true = строим снизу вверх

        // Ключевые уровни цен
        public double Level0 { get; private set; }
        public double Level23 { get; private set; }
        public double Level38 { get; private set; }
        public double Level50 { get; private set; }
        public double Level61 { get; private set; }
        public double Level100 { get; private set; }
        public double Level123 { get; private set; }
        public double Level161 { get; private set; }
        public double Level261 { get; private set; }
        public double Level423 { get; private set; }
        public double Level685 { get; private set; }

        // Статус достижения целей
        public bool Target123Reached { get; set; }
        public bool Target161Reached { get; set; }
        public bool Target261Reached { get; set; }

        // Имя инструмента и алгоритм
        public string Symbol { get; private set; }
        public int Stage { get; set; }  // стадия рынка 1-6

        public FiboGrid(string symbol, double low, double high, bool isBullish,
                        DateTime startTime, DateTime endTime)
        {
            Symbol = symbol;
            SwingLow = low;
            SwingHigh = high;
            IsBullish = isBullish;
            StartTime = startTime;
            EndTime = endTime;

            CalculateLevels();
        }

        private void CalculateLevels()
        {
            double range = SwingHigh - SwingLow;

            if (IsBullish)
            {
                // 0% = SwingLow (начало импульса вверх)
                // 100% = SwingHigh (конец импульса)
                // Уровни выше 100% — цели прогрессии
                Level0   = SwingLow;
                Level23  = SwingLow + range * 0.236;
                Level38  = SwingLow + range * 0.382;
                Level50  = SwingLow + range * 0.500;
                Level61  = SwingLow + range * 0.618;
                Level100 = SwingHigh;
                Level123 = SwingLow + range * 1.236;
                Level161 = SwingLow + range * 1.618;
                Level261 = SwingLow + range * 2.618;
                Level423 = SwingLow + range * 4.236;
                Level685 = SwingLow + range * 6.854;
            }
            else
            {
                // Медвежья сетка — 0% = SwingHigh, 100% = SwingLow
                Level0   = SwingHigh;
                Level23  = SwingHigh - range * 0.236;
                Level38  = SwingHigh - range * 0.382;
                Level50  = SwingHigh - range * 0.500;
                Level61  = SwingHigh - range * 0.618;
                Level100 = SwingLow;
                Level123 = SwingHigh - range * 1.236;
                Level161 = SwingHigh - range * 1.618;
                Level261 = SwingHigh - range * 2.618;
                Level423 = SwingHigh - range * 4.236;
                Level685 = SwingHigh - range * 6.854;
            }
        }

        /// <summary>
        /// Возвращает уровень входа для выбранного алгоритма.
        /// Алго 1: вход на 61.8% (коррекция после 1-го импульса к 100%)
        /// Алго 2: вход на 61.8%
        /// Алго 3: вход на 61.8%
        /// </summary>
        public double GetEntryLevel()
        {
            return Level61;
        }

        /// <summary>
        /// Стоп-лосс для алгоритма
        /// </summary>
        public double GetStopLoss(int algo)
        {
            switch (algo)
            {
                case 1: return Level0;      // за 0% Импульса 1
                case 2: return Level0;      // за 0% Импульса 2 (минимальная погрешность)
                case 3:                     // за 185 (ниже 0% на 85% от импульса)
                    double extra = (SwingHigh - SwingLow) * 0.85;
                    return IsBullish ? Level0 - extra : Level0 + extra;
                default: return Level0;
            }
        }

        /// <summary>
        /// Тейк-профит для алгоритма
        /// </summary>
        public double GetTakeProfit(int algo)
        {
            switch (algo)
            {
                case 1: return Level123;   // 23→123
                case 2: return Level161;   // 61→161
                case 3: return Level261;   // 101→261
                default: return Level123;
            }
        }

        /// <summary>
        /// Уровень безубытка (80% хода от входа до тейка)
        /// </summary>
        public double GetBreakEvenTrigger(int algo)
        {
            double entry = GetEntryLevel();
            double tp = GetTakeProfit(algo);
            return entry + (tp - entry) * 0.80;
        }

        /// <summary>
        /// Проверяет нужно ли перестроить сетку (достигли 261%)
        /// </summary>
        public bool NeedsRebuild(double currentPrice)
        {
            if (IsBullish)
                return currentPrice >= Level261;
            else
                return currentPrice <= Level261;
        }

        /// <summary>
        /// Краткое описание сетки для лога
        /// </summary>
        public override string ToString()
        {
            string dir = IsBullish ? "BULL" : "BEAR";
            return $"FiboGrid[{Symbol} {dir}] 0%={Level0:F5} 61%={Level61:F5} " +
                   $"100%={Level100:F5} 161%={Level161:F5} 261%={Level261:F5}";
        }
    }
}
