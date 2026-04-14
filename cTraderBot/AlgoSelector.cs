using System;

namespace FiboBlackGrail
{
    /// <summary>
    /// Выбирает алгоритм входа (1, 2, 3) на основе стадии рынка.
    ///
    /// Стадии по системе "Чёрный Грааль":
    ///   1 - Тренд
    ///   2 - Контртренд
    ///   3 - Тренд
    ///   4 - Коррекция
    ///   5 - Флет
    ///   6 - Тренд
    ///
    /// Алгоритмы:
    ///   Алго 1 (23→123): Стоп за 0% Импульса 1, тейк 23%–123%,  риск 3%
    ///   Алго 2 (61→161): Стоп за 0% Импульса 2, тейк 61%–161%,  риск 5%
    ///   Алго 3 (101→261): Стоп за 185%, тейк 101%–261%/423%,    риск 7%
    /// </summary>
    public static class AlgoSelector
    {
        public static int GetAlgo(int stage)
        {
            switch (stage)
            {
                case 1:
                case 3:
                case 6:
                    return 2;  // Тренд — Алго 2, уверенное движение

                case 2:
                    return 1;  // Контртренд — Алго 1, консервативно

                case 4:
                case 5:
                    return 3;  // Коррекция/Флет — Алго 3, расширенные цели

                default:
                    return 2;  // По умолчанию
            }
        }

        /// <summary>
        /// Риск в % от депозита для каждого алгоритма
        /// </summary>
        public static double GetRiskPercent(int algo)
        {
            switch (algo)
            {
                case 1: return 3.0;
                case 2: return 5.0;
                case 3: return 7.0;
                default: return 5.0;
            }
        }

        /// <summary>
        /// Описание алгоритма для логов и Telegram
        /// </summary>
        public static string GetDescription(int algo)
        {
            switch (algo)
            {
                case 1: return "Алго 1 (23→123) | Стоп за 0%";
                case 2: return "Алго 2 (61→161) | Стоп за 0%";
                case 3: return "Алго 3 (101→261) | Стоп за 185%";
                default: return "Неизвестный алгоритм";
            }
        }
    }
}
