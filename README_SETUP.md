# Чёрный Грааль — Инструкция по установке

## Шаг 1: Создать Telegram-бота

1. Открыть Telegram → найти @BotFather
2. Написать /newbot → дать имя → получить TOKEN
3. Написать @userinfobot → получить свой CHAT_ID

---

## Шаг 2: Деплой на Railway

1. Зайти на railway.app → New Project → Deploy from GitHub
2. Загрузить папку TelegramServer/ на GitHub (или через CLI)
3. В Railway добавить переменные окружения:
   - TELEGRAM_BOT_TOKEN = токен от BotFather
   - TELEGRAM_CHAT_ID   = ваш chat_id
   - BOT_SECRET         = любая секретная строка (например: mySecret123)
4. Деплой запустится автоматически
5. Скопировать URL вида: https://your-app.railway.app

---

## Шаг 3: Установка cBot в cTrader

1. Открыть cTrader → Automate → New cBot
2. Создать новый проект или добавить файлы:
   - FiboBlackGrailBot.cs (главный файл)
   - FiboGrid.cs
   - SwingDetector.cs
   - AlgoSelector.cs
   - RiskManager.cs
   - TelegramBridge.cs
   - SymbolState.cs
3. Добавить NuGet пакет: Newtonsoft.Json
4. Скомпилировать

---

## Шаг 4: Настройка параметров cBot

При добавлении бота на график задать:
- Railway Server URL: https://your-app.railway.app
- Bot Secret: (та же строка что в Railway)
- Символы: XAUUSD,XAGUSD,USOil,NAS100,EURUSD,GBPUSD
- Swing Strength: 3 (рекомендуется)

---

## Шаг 5: Хостинг на cTrader

1. В cTrader → Automate → выбрать бота → кнопка "Host"
2. Бот уходит на серверы cTrader и работает 24/7
3. Браузер и компьютер можно закрывать

---

## Шаг 6: Первый запуск

1. Открыть Telegram → написать боту /help
2. Задать стадию для каждого инструмента:
   /stage XAUUSD 1
   /stage XAGUSD 3
3. Бот автоматически построит сетку и начнёт отслеживать 61.8%

---

## Управление через Telegram

/stage XAUUSD 2     — установить стадию
/status             — статус всех инструментов
/pause XAUUSD       — остановить торговлю по символу
/resume XAUUSD      — возобновить
/report             — дневной отчёт

---

## Стадии и алгоритмы

| Стадия | Название     | Алгоритм | Тейк    | Риск |
|--------|--------------|----------|---------|------|
| 1      | Тренд        | Алго 2   | 61→161  | 5%   |
| 2      | Контртренд   | Алго 1   | 23→123  | 3%   |
| 3      | Тренд        | Алго 2   | 61→161  | 5%   |
| 4      | Коррекция    | Алго 3   | 101→261 | 7%   |
| 5      | Флет         | Алго 3   | 101→261 | 7%   |
| 6      | Тренд        | Алго 2   | 61→161  | 5%   |
