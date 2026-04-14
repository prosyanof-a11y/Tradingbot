using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;

namespace FiboBlackGrail
{
    /// <summary>
    /// Управление рисками:
    /// - Расчёт объёма позиции по % от депозита
    /// - Контроль общей просадки (лимит 20%)
    /// - Контроль открытых позиций
    /// </summary>
    public class RiskManager
    {
        private readonly IAccount _account;
        private readonly Positions _positions;

        public const double MaxTotalDrawdownPercent = 20.0;
        public const double WarnDrawdownPercent = 15.0;

        public RiskManager(IAccount account, Positions positions)
        {
            _account = account;
            _positions = positions;
        }

        /// <summary>
        /// Текущая просадка по всем позициям в % от баланса.
        /// Учитываем только убыточные позиции.
        /// Прибыльные — не считаем (по условию системы).
        /// </summary>
        public double GetCurrentDrawdownPercent()
        {
            double totalLoss = 0;

            foreach (var pos in _positions)
            {
                if (pos.NetProfit < 0)
                    totalLoss += Math.Abs(pos.NetProfit);
            }

            double balance = _account.Balance;
            if (balance <= 0) return 0;

            return (totalLoss / balance) * 100.0;
        }

        /// <summary>
        /// Можно ли открывать новую сделку с учётом текущей просадки и рискового %.
        /// </summary>
        public bool CanOpenNewTrade(double riskPercent, out string reason)
        {
            double currentDrawdown = GetCurrentDrawdownPercent();
            double availableRisk = MaxTotalDrawdownPercent - currentDrawdown;

            if (currentDrawdown >= MaxTotalDrawdownPercent)
            {
                reason = $"❌ Просадка {currentDrawdown:F1}% достигла лимита {MaxTotalDrawdownPercent}%. Новые входы заблокированы.";
                return false;
            }

            if (riskPercent > availableRisk)
            {
                reason = $"⚠️ Риск {riskPercent}% превышает доступный лимит {availableRisk:F1}%. Уменьшен до {availableRisk:F1}%.";
                // Разрешаем но с меньшим риском
                return true;
            }

            reason = string.Empty;
            return true;
        }

        /// <summary>
        /// Эффективный риск с учётом текущей просадки.
        /// </summary>
        public double GetEffectiveRiskPercent(double requestedRisk)
        {
            double currentDrawdown = GetCurrentDrawdownPercent();
            double availableRisk = MaxTotalDrawdownPercent - currentDrawdown;
            return Math.Min(requestedRisk, availableRisk);
        }

        /// <summary>
        /// Рассчитывает объём позиции в лотах.
        /// Формула: Лот = (Баланс × Риск%) / (СтопВПипсах × СтоимостьПипса)
        /// </summary>
        public double CalculateLotSize(Symbol symbol, double entryPrice, double stopLoss,
                                       double riskPercent, Robot robot)
        {
            double effectiveRisk = GetEffectiveRiskPercent(riskPercent);
            double riskAmount = _account.Balance * effectiveRisk / 100.0;

            // Расстояние до стопа в пипсах
            double stopDistancePrice = Math.Abs(entryPrice - stopLoss);
            double stopDistancePips = stopDistancePrice / symbol.PipSize;

            if (stopDistancePips <= 0)
            {
                robot.Print($"[RiskManager] Ошибка: стоп-расстояние = 0 для {symbol.Name}");
                return symbol.VolumeInUnitsMin;
            }

            // Стоимость 1 пипса для 1 лота
            double pipValuePerLot = symbol.PipValue;

            double volumeInUnits = (riskAmount / (stopDistancePips * pipValuePerLot));

            // Округляем до минимального шага
            double step = symbol.VolumeInUnitsStep;
            volumeInUnits = Math.Floor(volumeInUnits / step) * step;

            // Ограничиваем min/max
            volumeInUnits = Math.Max(symbol.VolumeInUnitsMin, volumeInUnits);
            volumeInUnits = Math.Min(symbol.VolumeInUnitsMax, volumeInUnits);

            robot.Print($"[RiskManager] {symbol.Name}: риск={effectiveRisk}% " +
                       $"сумма={riskAmount:F2} стоп={stopDistancePips:F1}пипс " +
                       $"объём={volumeInUnits}");

            return volumeInUnits;
        }

        /// <summary>
        /// Статус просадки для отчёта
        /// </summary>
        public string GetDrawdownStatus()
        {
            double dd = GetCurrentDrawdownPercent();
            string emoji = dd >= MaxTotalDrawdownPercent ? "🛑" :
                           dd >= WarnDrawdownPercent ? "⚠️" : "✅";

            return $"{emoji} Просадка: {dd:F1}% / {MaxTotalDrawdownPercent}%";
        }
    }
}
