using System;
using System.Collections.Generic;

namespace FiboBlackGrail
{
    /// <summary>
    /// Состояние торговли по одному инструменту.
    /// Отдельный экземпляр для каждого символа.
    /// </summary>
    public class SymbolState
    {
        public string Symbol { get; set; }
        public int Stage { get; set; } = 0;          // стадия рынка 1-6 (0 = не задана)
        public bool IsPaused { get; set; } = false;  // ручная пауза торговли
        public bool IsEnabled { get; set; } = true;  // включён ли инструмент

        // Текущая активная сетка
        public FiboGrid ActiveGrid { get; set; } = null;

        // Флаги для предотвращения двойных входов
        public bool EntrySignalActive { get; set; } = false;
        public DateTime LastEntryAttempt { get; set; } = DateTime.MinValue;

        // Идентификаторы открытых позиций по этому символу
        public List<int> ActivePositionIds { get; set; } = new List<int>();

        // Статистика по символу
        public int TotalTrades { get; set; } = 0;
        public int WinTrades { get; set; } = 0;
        public double TotalPnL { get; set; } = 0;

        public int Algo => Stage > 0 ? AlgoSelector.GetAlgo(Stage) : 2;
        public double RiskPercent => AlgoSelector.GetRiskPercent(Algo);

        public bool CanTrade => IsEnabled && !IsPaused && Stage > 0 && ActiveGrid != null;

        public string GetStatusLine()
        {
            string stageStr = Stage > 0 ? $"Стадия {Stage}" : "Стадия не задана";
            string pauseStr = IsPaused ? " [ПАУЗА]" : "";
            string gridStr = ActiveGrid != null ? $"| Сетка: 61%={ActiveGrid.Level61:F5}" : "| Сетка: нет";
            return $"{Symbol}: {stageStr}{pauseStr} | Алго {Algo} {gridStr}";
        }
    }
}
