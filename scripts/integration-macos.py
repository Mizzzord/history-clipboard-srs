#!/usr/bin/env python3
import argparse
import json
from pathlib import Path
import sqlite3
import subprocess
import time

parser = argparse.ArgumentParser(description="Проверка настоящего буфера на macOS с отдельной QA-базой")
parser.add_argument("--data-dir", required=True, type=Path)
args = parser.parse_args()
directory = args.data_dir.resolve()
if "artifacts" not in directory.parts or not directory.name.startswith("qa-"):
    raise SystemExit("Разрешена только отдельная папка artifacts/qa-*; не используйте пользовательскую историю.")
database = directory / "history.db"
report = directory / "native-report.json"
stop = directory / "restore-clipboard.signal"
stop.unlink(missing_ok=True)
original = subprocess.check_output(["pbpaste"])
fixture_a = "QA-A Привет 👋\r\n\tПолный текст без изменений  "
fixture_b = "QA-B Второй фрагмент для поиска"
exact = "я" * (1_048_576 // 2)
fixtures = [fixture_a, fixture_b, exact, exact + "a", "QA-C После большой записи"]
checks = []
latencies = []
success = False


def rows():
    with sqlite3.connect(database) as connection:
        return connection.execute("SELECT text FROM entries ORDER BY id DESC").fetchall()


def copy(text):
    subprocess.run(["pbcopy"], input=text.encode("utf-8"), check=True)


def expect_count(count, timeout=2):
    started = time.monotonic()
    while time.monotonic() - started < timeout:
        if len(rows()) == count:
            return time.monotonic() - started
        time.sleep(0.025)
    raise AssertionError(f"Ожидалось записей: {count}; получено: {len(rows())}")


try:
    base = len(rows())
    copy(fixture_a)
    latencies.append(expect_count(base + 1))
    assert rows()[0][0] == fixture_a
    checks.append("Точный Unicode-текст, CRLF, табуляция и пробелы через настоящий буфер")
    copy(fixture_a)
    time.sleep(0.6)
    assert len(rows()) == base + 1
    checks.append("Последовательный повтор не создаёт запись")
    copy(fixture_b)
    latencies.append(expect_count(base + 2))
    copy(fixture_a)
    latencies.append(expect_count(base + 3))
    checks.append("А → Б → А создаёт три записи")
    copy("")
    time.sleep(0.6)
    assert len(rows()) == base + 3
    checks.append("Пустой буфер не сохраняется")
    file_fixture = directory / "clipboard-file-fixture.txt"
    file_fixture.write_text("Disposable QA fixture")
    subprocess.run(["swift", "-e", "import AppKit; let p = NSPasteboard.general; p.clearContents(); guard p.writeObjects([NSURL(fileURLWithPath:CommandLine.arguments[1])]), p.setString(\"QA-FILE Не собирать файлы\", forType: .string) else { fatalError(\"Cannot prepare file clipboard fixture\") }", str(file_fixture)], check=True)
    time.sleep(0.6)
    assert len(rows()) == base + 3
    checks.append("Копирование файла с текстовым представлением не сохраняется")
    copy(exact)
    latencies.append(expect_count(base + 4))
    assert rows()[0][0] == exact
    copy(exact + "a")
    time.sleep(0.7)
    assert len(rows()) == base + 4
    checks.append("1 МиБ UTF-8 принят; 1 МиБ + 1 байт отклонён без обрезки")
    copy(fixtures[-1])
    latencies.append(expect_count(base + 5))
    checks.append("Сбор продолжается после отклонения большой записи")
    success = True
finally:
    report.write_text(json.dumps({"success": success, "checks": checks, "capture_seconds_after_pbcopy": latencies}, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps({"success": success, "checks": len(checks), "max_capture_seconds": max(latencies, default=0), "report": str(report)}, ensure_ascii=False), flush=True)
    print("Исходный буфер хранится только в памяти. Приостановите или закройте QA-приложение и создайте restore-clipboard.signal в его QA-папке.", flush=True)
    deadline = time.monotonic() + 900
    while not stop.exists() and time.monotonic() < deadline:
        time.sleep(0.25)
    if stop.exists():
        current = subprocess.check_output(["pbpaste"])
        if current.decode("utf-8", errors="replace").startswith("QA-") or current in [f.encode("utf-8") for f in fixtures] or current == b"":
            subprocess.run(["pbcopy"], input=original, check=True)
            print("Исходный буфер восстановлен после остановки QA-сбора.", flush=True)
        else:
            print("Буфер изменился вне проверки; оставлен без изменений.", flush=True)
    else:
        print("Автовосстановление не выполнено: нет подтверждения остановки QA-сбора.", flush=True)

raise SystemExit(0 if success else 1)
