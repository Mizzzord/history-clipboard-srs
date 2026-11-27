const { test } = require("node:test");
const assert = require("node:assert/strict");
const { addFragment, matches } = require("../../script.js");

test("содержимое, порядок и соседние повторы", () => {
  const entries = [];
  const text = "  А\r\nБ\t";
  for (const value of [text, text, " ", text]) addFragment(entries, value);
  assert.deepEqual(entries.map(entry => entry.text), [text, " ", text]);
  assert.ok(matches(text, "а\r\nб"));
});

test("границы UTF-8 и вытеснение старейшей записи", () => {
  const entries = [];
  assert.ok(addFragment(entries, ""));
  assert.equal(addFragment(entries, "я".repeat(524288)), "");
  assert.ok(addFragment(entries, "я".repeat(524288) + "a"));
  for (let i = 0; i < 500; i++) addFragment(entries, String(i));
  assert.equal(entries.length, 500);
  assert.equal(entries[499].text, "0");
});
