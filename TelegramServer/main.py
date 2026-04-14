"""
Telegram Bot + HTTP Bridge для cTrader "Чёрный Грааль"
Деплоится на Railway.app

Архитектура:
  Telegram → этот сервер → cBot (через /commands/poll)
  cBot → этот сервер (/notify) → Telegram

ENV переменные (задать в Railway):
  TELEGRAM_BOT_TOKEN  - токен бота от @BotFather
  TELEGRAM_CHAT_ID    - ваш chat_id (получить у @userinfobot)
  BOT_SECRET          - секретный ключ (любая строка, совпадает с cBot)
"""

import os
import asyncio
import logging
from collections import deque
from datetime import datetime
from typing import Deque, Dict

from fastapi import FastAPI, HTTPException, Header, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel
import httpx
from contextlib import asynccontextmanager

# ─── Логирование ──────────────────────────────────────────────────────────────

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s"
)
log = logging.getLogger(__name__)

# ─── Конфигурация ─────────────────────────────────────────────────────────────

TELEGRAM_TOKEN = os.environ.get("TELEGRAM_BOT_TOKEN", "")
TELEGRAM_CHAT_ID = os.environ.get("TELEGRAM_CHAT_ID", "")
BOT_SECRET = os.environ.get("BOT_SECRET", "change_me_secret")
TELEGRAM_API = f"https://api.telegram.org/bot{TELEGRAM_TOKEN}"

# ─── Очередь команд (буфер между Telegram и cBot) ────────────────────────────

# cBot опрашивает /commands/poll, забирает команды
command_queue: Deque[Dict] = deque(maxlen=50)

# Счётчик update_id для Telegram long-poll
last_update_id: int = 0

# ─── FastAPI приложение ───────────────────────────────────────────────────────

@asynccontextmanager
async def lifespan(app: FastAPI):
    # Запускаем фоновый поллинг Telegram при старте
    task = asyncio.create_task(telegram_polling_loop())
    log.info("Telegram polling запущен")
    yield
    task.cancel()

app = FastAPI(title="FiboBlackGrail Bridge", lifespan=lifespan)

# ─── Модели ───────────────────────────────────────────────────────────────────

class NotifyRequest(BaseModel):
    text: str

class CommandItem(BaseModel):
    command: str
    args: str
    id: str

# ─── Endpoints для cBot ───────────────────────────────────────────────────────

@app.get("/commands/poll")
async def poll_commands(x_bot_secret: str = Header(None)):
    """
    cBot опрашивает этот endpoint каждые N секунд.
    Возвращает список накопленных команд и очищает очередь.
    """
    if x_bot_secret != BOT_SECRET:
        raise HTTPException(status_code=403, detail="Invalid secret")

    commands = list(command_queue)
    command_queue.clear()
    return JSONResponse(content=commands)


@app.post("/notify")
async def notify(req: NotifyRequest, x_bot_secret: str = Header(None)):
    """
    cBot отправляет уведомление — мы пересылаем в Telegram.
    """
    if x_bot_secret != BOT_SECRET:
        raise HTTPException(status_code=403, detail="Invalid secret")

    success = await send_telegram(req.text)
    return {"ok": success}


@app.get("/health")
async def health():
    return {
        "status": "ok",
        "time": datetime.utcnow().isoformat(),
        "queue_size": len(command_queue)
    }

# ─── Telegram Polling ─────────────────────────────────────────────────────────

async def telegram_polling_loop():
    """
    Фоновая задача: получает обновления от Telegram, парсит команды.
    """
    global last_update_id

    if not TELEGRAM_TOKEN:
        log.warning("TELEGRAM_BOT_TOKEN не задан — Telegram polling отключён")
        return

    log.info("Начинаю Telegram polling...")

    async with httpx.AsyncClient(timeout=35.0) as client:
        while True:
            try:
                resp = await client.get(
                    f"{TELEGRAM_API}/getUpdates",
                    params={
                        "offset": last_update_id + 1,
                        "timeout": 30,
                        "allowed_updates": ["message"]
                    }
                )
                data = resp.json()

                if not data.get("ok"):
                    await asyncio.sleep(5)
                    continue

                for update in data.get("result", []):
                    last_update_id = update["update_id"]
                    await process_telegram_update(update)

            except asyncio.CancelledError:
                log.info("Telegram polling остановлен")
                break
            except Exception as e:
                log.error(f"Ошибка polling: {e}")
                await asyncio.sleep(5)


async def process_telegram_update(update: dict):
    """
    Обрабатывает входящее сообщение от Telegram.
    Парсит команды и кладёт в очередь для cBot.
    """
    message = update.get("message")
    if not message:
        return

    chat_id = str(message.get("chat", {}).get("id", ""))
    text = message.get("text", "").strip()

    # Проверяем что это наш авторизованный чат
    if TELEGRAM_CHAT_ID and chat_id != TELEGRAM_CHAT_ID:
        log.warning(f"Сообщение от неизвестного chat_id: {chat_id}")
        await send_telegram("❌ Доступ запрещён", chat_id)
        return

    if not text:
        return

    log.info(f"Telegram: {text}")

    # Парсим команду
    parts = text.split(maxsplit=1)
    command = parts[0].lower()
    args = parts[1] if len(parts) > 1 else ""

    known_commands = ["/stage", "/pause", "/resume", "/status", "/report",
                      "/grid", "/help", "/stop", "/start"]

    if command not in known_commands:
        await send_telegram(
            f"❓ Неизвестная команда: {command}\n"
            f"Введите /help для списка команд"
        )
        return

    if command == "/help":
        await send_telegram(get_help_text())
        return

    # Кладём команду в очередь для cBot
    cmd_item = {
        "command": command,
        "args": args,
        "id": str(update["update_id"])
    }
    command_queue.append(cmd_item)

    # Подтверждаем получение
    confirmations = {
        "/stage": f"📝 Команда получена: установить стадию {args}",
        "/pause": f"⏸ Команда получена: пауза {args}",
        "/resume": f"▶️ Команда получена: возобновить {args}",
        "/status": "📊 Запрос статуса отправлен...",
        "/report": "📈 Запрос отчёта отправлен...",
        "/grid": f"📐 Запрос сетки {args}...",
        "/stop": "⏹ Команда остановки отправлена",
    }
    await send_telegram(confirmations.get(command, "✅ Команда отправлена"))


def get_help_text():
    return """📚 *КОМАНДЫ ЧЁРНЫЙ ГРААЛЬ*

🎯 *Управление стадиями:*
`/stage XAUUSD 1` — Тренд
`/stage XAUUSD 2` — Контртренд
`/stage XAUUSD 3` — Тренд
`/stage XAUUSD 4` — Коррекция
`/stage XAUUSD 5` — Флет
`/stage XAUUSD 6` — Тренд
`/stage all 1` — всем инструментам

⏸ *Управление торговлей:*
`/pause XAUUSD` — остановить торговлю
`/pause all` — остановить всё
`/resume XAUUSD` — возобновить

📊 *Информация:*
`/status` — статус всех инструментов
`/grid XAUUSD` — уровни сетки
`/report` — дневной отчёт

📌 *Инструменты:* XAUUSD, XAGUSD, USOil, NAS100

📌 *Алгоритмы:*
• Стадия 1,3,6 → Алго 2 (61→161) риск 5%
• Стадия 2 → Алго 1 (23→123) риск 3%
• Стадия 4,5 → Алго 3 (101→261) риск 7%"""


async def send_telegram(text: str, chat_id: str = None) -> bool:
    """Отправляет сообщение в Telegram."""
    target = chat_id or TELEGRAM_CHAT_ID
    if not target or not TELEGRAM_TOKEN:
        log.warning("Telegram не настроен — сообщение не отправлено")
        return False

    try:
        async with httpx.AsyncClient(timeout=10.0) as client:
            resp = await client.post(
                f"{TELEGRAM_API}/sendMessage",
                json={
                    "chat_id": target,
                    "text": text,
                    "parse_mode": "Markdown"
                }
            )
            return resp.json().get("ok", False)
    except Exception as e:
        log.error(f"Ошибка отправки в Telegram: {e}")
        return False
