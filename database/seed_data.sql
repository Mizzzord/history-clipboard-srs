-- Run once on a separate empty demonstration database.
PRAGMA foreign_keys = ON;
BEGIN;
INSERT INTO entries (id, text, created_at) VALUES
    (1, 'Заказать упаковку', '2026-11-27T09:00:00.0000000+03:00'),
    (2, 'Согласовать время доставки', '2026-11-27T09:10:00.0000000+03:00'),
    (3, 'Книга на выходные', '2026-11-27T09:15:00.0000000+03:00');
INSERT INTO tags (id, name) VALUES (1, 'Работа'), (2, 'Доставка');
INSERT INTO entry_tags (entry_id, tag_id) VALUES (1, 1), (2, 1), (2, 2);
COMMIT;
