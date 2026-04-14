"""
Telegram Bot + HTTP Bridge для cTrader "Чёрный Грааль"
С inline-кнопками для удобного управления.
"""

import os
import asyncio
import logging
from collections import deque
from datetime import datetime
from typing import Deque, Dict, Optional

from fastapi import FastAPI, HTTPException, Header
from fastapi.responses import JSONResponse
from pydantic import BaseModel
import httpx
from contextlib import asynccontextmanager

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
log = logging.getLogger(__name__)

# ─── Конфигурация ─────────────────────────────────────────────────────────────

TELEGRAM_TOKEN   = os.environ.get("TELEGRAM_BOT_TOKEN", "")
TELEGRAM_CHAT_ID = os.environ.get("TELEGRAM_CHAT_ID", "")
BOT_SECRET       = os.environ.get("BOT_SECRET", "change_me_secret")
TELEGRAM_API     = f"https://api.telegram.org/bot{TELEGRAM_TOKEN}"

SYMBOLS = ["XAUUSD", "XAGUSD", "BRENT", "#USSPX500", "EURUSD", "GBPUSD",
           "USDJPY", "AUDUSD", "USDCAD", "USDCHF", "NZDUSD", "BITCOIN"]

STAGES = {
    "1": "1️⃣ Тренд",
    "2": "2️⃣ Контртренд",
    "3": "3️⃣ Тренд",
    "4": "4️⃣ Коррекция",
    "5": "5️⃣ Флет",
    "6": "6️⃣ Тренд",
}

# Временное хранилище: ожидаем выбор стадии для символа
pending_symbol: Dict[str, str] = {}  # chat_id → symbol

command_queue: Deque[Dict] = deque(maxlen=50)
last_update_id: int = 0

# ─── FastAPI ──────────────────────────────────────────────────────────────────

@asynccontextmanager
async def lifespan(app: FastAPI):
    task = asyncio.create_task(polling_loop())
    log.info("Telegram polling запущен")
    yield
    task.cancel()

app = FastAPI(title="FiboBlackGrail Bridge", lifespan=lifespan)

class NotifyRequest(BaseModel):
    text: str

@app.get("/commands/poll")
async def poll_commands(x_bot_secret: str = Header(None)):
    if x_bot_secret != BOT_SECRET:
        raise HTTPException(status_code=403, detail="Invalid secret")
    commands = list(command_queue)
    command_queue.clear()
    return JSONResponse(content=commands)

@app.post("/notify")
async def notify(req: NotifyRequest, x_bot_secret: str = Header(None)):
    if x_bot_secret != BOT_SECRET:
        raise HTTPException(status_code=403, detail="Invalid secret")
    ok = await send_message(req.text.replace("\\n", "\n"))
    return {"ok": ok}

@app.get("/health")
async def health():
    return {"status": "ok", "time": datetime.utcnow().isoformat(), "queue_size": len(command_queue)}

# ─── Polling ──────────────────────────────────────────────────────────────────

async def polling_loop():
    global last_update_id
    if not TELEGRAM_TOKEN:
        log.warning("TELEGRAM_BOT_TOKEN не задан")
        return

    async with httpx.AsyncClient(timeout=35.0) as client:
        while True:
            try:
                resp = await client.get(
                    f"{TELEGRAM_API}/getUpdates",
                    params={
                        "offset": last_update_id + 1,
                        "timeout": 30,
                        "allowed_updates": ["message", "callback_query"]
                    }
                )
                data = resp.json()
                if not data.get("ok"):
                    await asyncio.sleep(5)
                    continue

                for update in data.get("result", []):
                    last_update_id = update["update_id"]
                    if "message" in update:
                        await handle_message(update["message"])
                    elif "callback_query" in update:
                        await handle_callback(update["callback_query"])

            except asyncio.CancelledError:
                break
            except Exception as e:
                log.error(f"Polling error: {e}")
                await asyncio.sleep(5)

# ─── Обработка текстовых сообщений ───────────────────────────────────────────

async def handle_message(message: dict):
    chat_id = str(message.get("chat", {}).get("id", ""))
    text = message.get("text", "").strip()

    if TELEGRAM_CHAT_ID and chat_id != TELEGRAM_CHAT_ID:
        await send_message("❌ Доступ запрещён", chat_id)
        return

    if not text:
        return

    log.info(f"MSG: {text}")
    parts = text.split(maxsplit=1)
    cmd = parts[0].lower()
    args = parts[1].strip() if len(parts) > 1 else ""

    if cmd in ("/start", "/menu"):
        await show_main_menu(chat_id)

    elif cmd == "/help":
        await send_message(help_text(), chat_id)

    elif cmd == "/status":
        push_command("/status", "")
        await send_message("📊 Запрашиваю статус...", chat_id)

    elif cmd == "/report":
        push_command("/report", "")
        await send_message("📈 Запрашиваю отчёт...", chat_id)

    elif cmd == "/pause":
        sym = args.upper() or "ALL"
        push_command("/pause", sym)
        await send_message(f"⏸ Пауза: {sym}", chat_id)

    elif cmd == "/resume":
        sym = args.upper() or "ALL"
        push_command("/resume", sym)
        await send_message(f"▶️ Возобновлено: {sym}", chat_id)

    elif cmd == "/stage":
        # Поддержка текстовой команды: /stage XAUUSD 2
        sparts = args.split()
        if len(sparts) == 2:
            sym, stage = sparts[0].upper(), sparts[1]
            if stage in STAGES:
                push_command("/stage", f"{sym} {stage}")
                await send_message(
                    f"✅ {sym}: {STAGES[stage]}\n"
                    f"Алго {get_algo(int(stage))} | Риск {get_risk(int(stage))}%",
                    chat_id
                )
            else:
                await send_message("❌ Стадия 1–6", chat_id)
        else:
            # Нет аргументов — показываем меню выбора инструмента
            await show_symbol_menu(chat_id, "Выбери инструмент для установки стадии:")

    else:
        await show_main_menu(chat_id)

# ─── Обработка нажатий кнопок ────────────────────────────────────────────────

async def handle_callback(cb: dict):
    chat_id = str(cb.get("message", {}).get("chat", {}).get("id", ""))
    msg_id  = cb.get("message", {}).get("message_id")
    data    = cb.get("data", "")
    cb_id   = cb.get("id")

    # Подтверждаем нажатие (убирает часики)
    await answer_callback(cb_id)

    log.info(f"CALLBACK: {data} chat={chat_id}")

    # ── Открыть меню выбора инструмента
    if data == "set_stage":
        await edit_message(chat_id, msg_id,
            "📍 Выбери инструмент:", symbol_keyboard())

    # ── Выбор инструмента для стадии
    elif data.startswith("sym:"):
        sym = data[4:]
        pending_symbol[chat_id] = sym
        await edit_message(chat_id, msg_id,
            f"📍 {sym} — выбери стадию:",
            stage_keyboard(sym))

    # ── Выбор стадии
    if data.startswith("stage:"):
        _, sym, stage = data.split(":")
        push_command("/stage", f"{sym} {stage}")
        algo = get_algo(int(stage))
        risk = get_risk(int(stage))
        await edit_message(chat_id, msg_id,
            f"✅ *{sym}*: {STAGES[stage]}\n"
            f"Алго {algo} | Риск {risk}%\n\n"
            f"Хочешь настроить ещё инструмент?",
            back_keyboard())

    # ── Пауза / Возобновление конкретного символа
    elif data.startswith("pause:"):
        sym = data[6:]
        push_command("/pause", sym)
        await edit_message(chat_id, msg_id, f"⏸ {sym} — пауза", back_keyboard())

    elif data.startswith("resume:"):
        sym = data[7:]
        push_command("/resume", sym)
        await edit_message(chat_id, msg_id, f"▶️ {sym} — возобновлено", back_keyboard())

    # ── Статус / Отчёт
    elif data == "status":
        push_command("/status", "")
        await edit_message(chat_id, msg_id, "📊 Статус запрошен...", back_keyboard())

    elif data == "report":
        push_command("/report", "")
        await edit_message(chat_id, msg_id, "📈 Отчёт запрошен...", back_keyboard())

    # ── Назад в главное меню
    elif data == "menu":
        await edit_message(chat_id, msg_id, "🏠 Главное меню:", main_keyboard())

    # ── Управление инструментом (пауза/возобновить)
    elif data == "manage":
        await edit_message(chat_id, msg_id,
            "⚙️ Управление инструментами:", manage_keyboard())

# ─── Клавиатуры ───────────────────────────────────────────────────────────────

def main_keyboard():
    return {
        "inline_keyboard": [
            [{"text": "🎯 Установить стадию", "callback_data": "set_stage"}],
            [
                {"text": "📊 Статус",  "callback_data": "status"},
                {"text": "📈 Отчёт",   "callback_data": "report"},
            ],
            [{"text": "⚙️ Управление",  "callback_data": "manage"}],
        ]
    }

def symbol_keyboard(action_prefix: str = "sym"):
    """Кнопки выбора инструмента."""
    rows = []
    row = []
    for i, sym in enumerate(SYMBOLS):
        row.append({"text": sym, "callback_data": f"{action_prefix}:{sym}"})
        if len(row) == 3:
            rows.append(row)
            row = []
    if row:
        rows.append(row)
    rows.append([{"text": "🏠 Меню", "callback_data": "menu"}])
    return {"inline_keyboard": rows}

def stage_keyboard(sym: str):
    """Кнопки выбора стадии 1–6."""
    rows = [
        [
            {"text": "1️⃣ Тренд",      "callback_data": f"stage:{sym}:1"},
            {"text": "2️⃣ Контртренд", "callback_data": f"stage:{sym}:2"},
            {"text": "3️⃣ Тренд",      "callback_data": f"stage:{sym}:3"},
        ],
        [
            {"text": "4️⃣ Коррекция",  "callback_data": f"stage:{sym}:4"},
            {"text": "5️⃣ Флет",       "callback_data": f"stage:{sym}:5"},
            {"text": "6️⃣ Тренд",      "callback_data": f"stage:{sym}:6"},
        ],
        [{"text": "🏠 Меню", "callback_data": "menu"}]
    ]
    return {"inline_keyboard": rows}

def manage_keyboard():
    """Выбор инструмента для паузы/возобновления."""
    rows = []
    for sym in SYMBOLS:
        rows.append([
            {"text": f"⏸ {sym}",  "callback_data": f"pause:{sym}"},
            {"text": f"▶️ {sym}", "callback_data": f"resume:{sym}"},
        ])
    rows.append([{"text": "🏠 Меню", "callback_data": "menu"}])
    return {"inline_keyboard": rows}

def back_keyboard():
    return {"inline_keyboard": [[{"text": "🏠 Меню", "callback_data": "menu"}]]}

# ─── Вспомогательные функции ──────────────────────────────────────────────────

async def show_main_menu(chat_id: str):
    await send_message("🏠 *Чёрный Грааль* — главное меню:", chat_id, main_keyboard())

async def show_symbol_menu(chat_id: str, text: str):
    await send_message(text, chat_id, symbol_keyboard())

def push_command(command: str, args: str):
    command_queue.append({
        "command": command,
        "args": args,
        "id": str(datetime.utcnow().timestamp())
    })

def get_algo(stage: int) -> int:
    if stage == 2: return 1
    if stage in (4, 5): return 3
    return 2

def get_risk(stage: int) -> float:
    algo = get_algo(stage)
    return {1: 3.0, 2: 5.0, 3: 7.0}.get(algo, 5.0)

async def send_message(text: str, chat_id: str = None, keyboard: dict = None) -> bool:
    target = chat_id or TELEGRAM_CHAT_ID
    if not target or not TELEGRAM_TOKEN:
        return False
    payload = {
        "chat_id": target,
        "text": text,
        "parse_mode": "Markdown"
    }
    if keyboard:
        payload["reply_markup"] = keyboard
    try:
        async with httpx.AsyncClient(timeout=10.0) as client:
            resp = await client.post(f"{TELEGRAM_API}/sendMessage", json=payload)
            return resp.json().get("ok", False)
    except Exception as e:
        log.error(f"send_message error: {e}")
        return False

async def edit_message(chat_id: str, msg_id: int, text: str, keyboard: dict = None):
    payload = {
        "chat_id": chat_id,
        "message_id": msg_id,
        "text": text,
        "parse_mode": "Markdown"
    }
    if keyboard:
        payload["reply_markup"] = keyboard
    try:
        async with httpx.AsyncClient(timeout=10.0) as client:
            await client.post(f"{TELEGRAM_API}/editMessageText", json=payload)
    except Exception as e:
        log.error(f"edit_message error: {e}")

async def answer_callback(callback_id: str):
    try:
        async with httpx.AsyncClient(timeout=5.0) as client:
            await client.post(f"{TELEGRAM_API}/answerCallbackQuery",
                              json={"callback_query_id": callback_id})
    except:
        pass

def help_text() -> str:
    return (
        "📚 *ЧЁРНЫЙ ГРААЛЬ — Управление*\n\n"
        "Используй кнопки: /menu\n\n"
        "Или текстовые команды:\n"
        "`/stage XAUUSD 1` — установить стадию\n"
        "`/pause XAUUSD` — пауза (ALL — все)\n"
        "`/resume XAUUSD` — возобновить\n"
        "_Инструменты: XAUUSD, XAGUSD, BRENT,_\n"
        "_#USSPX500, EURUSD, GBPUSD, USDJPY,_\n"
        "_AUDUSD, USDCAD, USDCHF, NZDUSD, BITCOIN_\n\n"
        "`/status` — статус всех\n"
        "`/report` — отчёт\n\n"
        "*Стадии:*\n"
        "1,3,6 → Алго 2 (61→161) 5%\n"
        "2 → Алго 1 (23→123) 3%\n"
        "4,5 → Алго 3 (101→261) 7%"
    )
