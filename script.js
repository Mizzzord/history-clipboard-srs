"use strict";

const historyLimit = 500;
const byteLimit = 1048576;

function addFragment(entries, text, now = new Date()) {
  if (!text.length) return "Введите текст.";
  if (new TextEncoder().encode(text).length > byteLimit) return "Текст больше 1 МиБ.";
  if (entries[0]?.text === text) return "Такой фрагмент уже стоит первым.";
  entries.unshift({ text, time: now });
  if (entries.length > historyLimit) entries.pop();
  return "";
}

function matches(text, query) {
  return text.toLowerCase().includes(query.toLowerCase());
}

function openHistory() {
  const entries = [];
  const get = id => document.getElementById(id);
  const list = get("entries");
  const input = get("input");
  const search = get("search");
  const status = get("status");
  function action(label, run) {
    const button = document.createElement("button");
    button.type = "button";
    button.textContent = label;
    button.addEventListener("click", run);
    return button;
  }
  function showEntry(entry) {
    const item = document.createElement("li");
    const time = document.createElement("time");
    time.dateTime = entry.time.toISOString();
    time.textContent = entry.time.toLocaleString("ru-RU");
    const details = document.createElement("details");
    const preview = document.createElement("summary");
    preview.textContent = entry.text.replace(/\s+/g, " ").trim().slice(0, 120) || "Пробельный фрагмент";
    details.append(preview);
    details.addEventListener("toggle", () => {
      if (details.open && !details.querySelector("pre")) {
        const full = document.createElement("pre");
        full.textContent = entry.text;
        details.append(full);
      }
    });
    const actions = document.createElement("div");
    actions.className = "actions";
    actions.append(action("Скопировать", async () => {
      try {
        await navigator.clipboard.writeText(entry.text);
        status.textContent = "Полный текст скопирован.";
      } catch {
        details.open = true;
        status.textContent = "Браузер запретил копирование. Выделите раскрытый текст и скопируйте вручную.";
      }
    }), action("Удалить", () => {
      entries.splice(entries.indexOf(entry), 1);
      render();
      status.textContent = "Запись удалена.";
      search.focus();
    }));
    item.append(time, details, actions);
    return item;
  }
  function render() {
    const shown = entries.filter(entry => matches(entry.text, search.value));
    list.replaceChildren(...shown.map(showEntry));
    get("count").textContent = search.value ? shown.length + " из " + entries.length : entries.length + " / 500";
    const empty = get("empty");
    empty.hidden = shown.length > 0;
    empty.textContent = entries.length ? "Совпадений нет. Измените запрос." : "История пуста. Добавьте первый фрагмент.";
    get("clear").disabled = entries.length === 0;
  }
  get("add-form").addEventListener("submit", event => {
    event.preventDefault();
    const message = addFragment(entries, input.value);
    status.textContent = message || "Фрагмент добавлен.";
    if (!message) { input.value = ""; render(); }
    input.focus();
  });
  search.addEventListener("input", render);
  get("clear").addEventListener("click", () => {
    if (!window.confirm("Удалить все фрагменты этой вкладки?")) return;
    entries.length = 0;
    render();
    status.textContent = "История очищена.";
  });
}

if (typeof document !== "undefined") openHistory();
if (typeof module !== "undefined") module.exports = { addFragment, matches };
