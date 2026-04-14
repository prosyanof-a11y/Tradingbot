using System;
using cAlgo.API;
using cAlgo.API.Internals;

namespace FiboBlackGrail
{
    /// <summary>
    /// Находит экстремумы (swing high / swing low) на заданном таймфрейме.
    /// Метод: N свечей слева и справа должны быть ниже/выше текущей свечи.
    /// Цены берутся строго по ТЕНЯМ (High/Low), как в системе.
    /// </summary>
    public class SwingDetector
    {
        private readonly Bars _bars;
        private readonly int _strength;  // количество свечей для подтверждения

        public double LastSwingHigh { get; private set; }
        public double LastSwingLow { get; private set; }
        public DateTime LastSwingHighTime { get; private set; }
        public DateTime LastSwingLowTime { get; private set; }

        // Предпоследние экстремумы для построения сетки
        public double PrevSwingHigh { get; private set; }
        public double PrevSwingLow { get; private set; }
        public DateTime PrevSwingHighTime { get; private set; }
        public DateTime PrevSwingLowTime { get; private set; }

        public SwingDetector(Bars bars, int strength = 3)
        {
            _bars = bars;
            _strength = strength;

            // Инициализируем начальные значения
            LastSwingHigh = 0;
            LastSwingLow = double.MaxValue;
            ScanHistory();
        }

        /// <summary>
        /// Сканирует исторические данные для нахождения последних экстремумов.
        /// Вызывается при инициализации.
        /// </summary>
        private void ScanHistory()
        {
            int count = 0;
            int barsCount = _bars.Count;

            for (int i = barsCount - _strength - 1; i >= _strength; i--)
            {
                if (IsSwingHigh(i) && count < 2)
                {
                    if (count == 0)
                    {
                        LastSwingHigh = _bars.HighPrices[i];
                        LastSwingHighTime = _bars.OpenTimes[i];
                    }
                    else
                    {
                        PrevSwingHigh = _bars.HighPrices[i];
                        PrevSwingHighTime = _bars.OpenTimes[i];
                    }
                    count++;
                }
            }

            count = 0;
            for (int i = barsCount - _strength - 1; i >= _strength; i--)
            {
                if (IsSwingLow(i) && count < 2)
                {
                    if (count == 0)
                    {
                        LastSwingLow = _bars.LowPrices[i];
                        LastSwingLowTime = _bars.OpenTimes[i];
                    }
                    else
                    {
                        PrevSwingLow = _bars.LowPrices[i];
                        PrevSwingLowTime = _bars.OpenTimes[i];
                    }
                    count++;
                }
            }
        }

        /// <summary>
        /// Обновляет экстремумы на новой свече. Вызывать в OnBar().
        /// </summary>
        public bool Update()
        {
            bool newExtremumFound = false;
            int checkIndex = _strength; // проверяем свечу strength баров назад

            if (IsSwingHigh(checkIndex))
            {
                double newHigh = _bars.HighPrices[checkIndex];
                if (Math.Abs(newHigh - LastSwingHigh) > 0.000001)
                {
                    PrevSwingHigh = LastSwingHigh;
                    PrevSwingHighTime = LastSwingHighTime;
                    LastSwingHigh = newHigh;
                    LastSwingHighTime = _bars.OpenTimes[checkIndex];
                    newExtremumFound = true;
                }
            }

            if (IsSwingLow(checkIndex))
            {
                double newLow = _bars.LowPrices[checkIndex];
                if (Math.Abs(newLow - LastSwingLow) > 0.000001)
                {
                    PrevSwingLow = LastSwingLow;
                    PrevSwingLowTime = LastSwingLowTime;
                    LastSwingLow = newLow;
                    LastSwingLowTime = _bars.OpenTimes[checkIndex];
                    newExtremumFound = true;
                }
            }

            return newExtremumFound;
        }

        /// <summary>
        /// Определяет является ли свеча i максимальным экстремумом.
        /// N свечей слева и справа должны иметь High ниже.
        /// </summary>
        private bool IsSwingHigh(int index)
        {
            if (index < _strength || index >= _bars.Count - _strength)
                return false;

            double high = _bars.HighPrices[index];

            for (int j = 1; j <= _strength; j++)
            {
                if (_bars.HighPrices[index - j] >= high) return false;
                if (_bars.HighPrices[index + j] >= high) return false;
            }

            return true;
        }

        /// <summary>
        /// Определяет является ли свеча i минимальным экстремумом.
        /// N свечей слева и справа должны иметь Low выше.
        /// </summary>
        private bool IsSwingLow(int index)
        {
            if (index < _strength || index >= _bars.Count - _strength)
                return false;

            double low = _bars.LowPrices[index];

            for (int j = 1; j <= _strength; j++)
            {
                if (_bars.LowPrices[index - j] <= low) return false;
                if (_bars.LowPrices[index + j] <= low) return false;
            }

            return true;
        }

        /// <summary>
        /// Возвращает сетку Фибо для бычьего сценария:
        /// от последнего SwingLow (0%) до последнего SwingHigh (100%)
        /// </summary>
        public FiboGrid BuildBullishGrid(string symbol, int stage)
        {
            if (LastSwingLow >= LastSwingHigh)
                return null;

            var grid = new FiboGrid(
                symbol,
                LastSwingLow, LastSwingHigh,
                isBullish: true,
                startTime: LastSwingLowTime,
                endTime: LastSwingHighTime
            );
            grid.Stage = stage;
            return grid;
        }

        /// <summary>
        /// Возвращает сетку Фибо для медвежьего сценария:
        /// от последнего SwingHigh (0%) до последнего SwingLow (100%)
        /// </summary>
        public FiboGrid BuildBearishGrid(string symbol, int stage)
        {
            if (LastSwingHigh <= LastSwingLow)
                return null;

            var grid = new FiboGrid(
                symbol,
                LastSwingLow, LastSwingHigh,
                isBullish: false,
                startTime: LastSwingHighTime,
                endTime: LastSwingLowTime
            );
            grid.Stage = stage;
            return grid;
        }
    }
}
